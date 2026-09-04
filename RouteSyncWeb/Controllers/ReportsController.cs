using FleetWise.Models;
using FleetWise.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using static Postgrest.Constants;


namespace FleetWise.Controllers
{
    [Authorize]
    [RequirePermission("reports")]
    public class ReportsController : Controller
    {
        private readonly Supabase.Client _supabase;
        private const int PageSize = 5;

        public ReportsController(Supabase.Client supabase) => _supabase = supabase;

        public async Task<IActionResult> Index() => View();

        /// <summary>
        /// Main data endpoint for the reports page: the stat cards, one page of the table,
        /// its pagination, and the passenger and revenue summary cards.
        /// </summary>
        /// <remarks>Everything is filtered by the page's route and date selection, and the
        /// summary cards additionally by their own period.</remarks>
        [HttpGet]
        public async Task<IActionResult> GetData(
            int? routeId,
            DateTime? date,
            int page = 1,
            string passengerPeriod = "This Week",
            string revenuePeriod = "This Week")
        {
            if (page < 1) page = 1;
            // Defaults to the current operational day, 06:00 to 05:59 the next morning,
            // rather than the calendar day. Before 6 AM that is still the previous day.
            var anchor = (date ?? PhClock.OperationalDay).Date;

            // Reference data.
            var routesResp = await _supabase.From<BusRoute>().Get();
            var routes = routesResp.Models;
            var routeNames = routes.ToDictionary(r => r.RouteId, r => r.RouteName);

            var usersResp = await _supabase.From<UserModel>().Get();
            var userNames = usersResp.Models.ToDictionary(u => u.UserId, u => $"{u.FirstName} {u.LastName}");

            var vehiclesResp = await _supabase.From<Vehicle>().Get();
            var vehiclesById = vehiclesResp.Models.ToDictionary(v => v.VehicleId, v => v);

            var tripsResp = await _supabase.From<Trip>().Get();
            var allTrips = tripsResp.Models;

            int Passengers(Trip t) => t.TotalBoarded;

            bool MatchesGlobalFilters(Trip t) =>
                (!routeId.HasValue || routeId.Value == 0 || t.RouteId == routeId.Value) &&
                (!date.HasValue || t.Date.Date == anchor);

            // Every filtered trip. Drives the stat cards, including missed trips.
            var filtered = allTrips
                .Where(MatchesGlobalFilters)
                .OrderByDescending(t => t.Date)
                .ThenBy(t => t.TripId)
                .ToList();

            // The table lists completed trips only. Those are the ones with settled
            // passenger and revenue figures; in-progress and missed trips would add rows
            // with nothing to report.
            var tableTrips = filtered
                .Where(t => string.Equals(t.TripStatus, "Completed", StringComparison.OrdinalIgnoreCase))
                .ToList();

            int totalCount = tableTrips.Count;
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));
            if (page > totalPages) page = totalPages;
            var pageTrips = tableTrips.Skip((page - 1) * PageSize).Take(PageSize).ToList();

            var tableRows = pageTrips.Select(t => new
            {
                tripId = t.TripId,
                driverName = userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A",
                vehicleId = t.VehicleId,
                plateNumber = vehiclesById.TryGetValue(t.VehicleId, out var v) ? v.PlateNumber : "N/A",
                routeName = routeNames.TryGetValue(t.RouteId, out var rn) ? rn : "N/A",
                shiftType = t.ShiftType,
                passengers = Passengers(t),
                revenue = t.EstimatedRevenue,
                status = DeriveStatus(t)
            });

            // Stat cards.
            int completedTrips = filtered.Count(t => string.Equals(t.TripStatus, "completed", StringComparison.OrdinalIgnoreCase));
            // Missed is never stored. It is derived from the shift window, so this counts
            // trips past their window that never started or completed.
            //
            // Not the same as the delayed figure on the dispatch board, which is a trip
            // still inside its window that has yet to depart. That one can still be
            // rescued and belongs to the day being worked; this one is service that was
            // not delivered.
            int missedTrips = filtered.Count(t => DeriveStatus(t) == "Missed");

            int totalPassengers = filtered.Sum(Passengers);
            decimal totalRevenue = filtered.Where(Earned).Sum(t => t.EstimatedRevenue);

            var prevDay = anchor.AddDays(-1);
            var prevDayTrips = allTrips.Where(t =>
                (!routeId.HasValue || routeId.Value == 0 || t.RouteId == routeId.Value) &&
                t.Date.Date == prevDay).ToList();

            int prevPassengers = prevDayTrips.Sum(Passengers);
            decimal prevRevenue = prevDayTrips.Where(Earned).Sum(t => t.EstimatedRevenue);

            // Summary cards (passenger / revenue).
            var passengerSummary = BuildSummary(allTrips, Passengers, routeNames, routeId, anchor, passengerPeriod, isPassenger: true);
            var revenueSummary = BuildSummary(allTrips, Passengers, routeNames, routeId, anchor, revenuePeriod, isPassenger: false);

            return Json(new
            {
                routes = routes
                    .OrderBy(r => r.RouteId)
                    .Select(r => new { id = r.RouteId, name = r.RouteName }),

                stats = new
                {
                    totalTrips = completedTrips,
                    missedTrips,
                    totalPassengers,
                    totalRevenue,
                    passengerDelta = totalPassengers - prevPassengers,
                    revenueDelta = totalRevenue - prevRevenue
                },

                table = new
                {
                    rows = tableRows,
                    page,
                    totalPages,
                    totalCount,
                    from = totalCount == 0 ? 0 : (page - 1) * PageSize + 1,
                    to = Math.Min(page * PageSize, totalCount)
                },

                passengerSummary,
                revenueSummary
            });
        }

        private static object BuildSummary(
            List<Trip> allTrips,
            Func<Trip, int> passengers,
            Dictionary<int, string> routeNames,
            int? routeId,
            DateTime anchor,
            string period,
            bool isPassenger)
        {
            var (start, end, prevStart, prevEnd) = GetRange(anchor, period);

            bool InRoute(Trip t) => !routeId.HasValue || routeId.Value == 0 || t.RouteId == routeId.Value;

            var current = allTrips.Where(t => InRoute(t) && t.Date.Date >= start && t.Date.Date <= end).ToList();
            var previous = allTrips.Where(t => InRoute(t) && t.Date.Date >= prevStart && t.Date.Date <= prevEnd).ToList();

            decimal total = isPassenger ? current.Sum(passengers) : current.Where(Earned).Sum(t => t.EstimatedRevenue);
            decimal prevTotal = isPassenger ? previous.Sum(passengers) : previous.Where(Earned).Sum(t => t.EstimatedRevenue);

            double deltaPct = prevTotal == 0
                ? (total == 0 ? 0 : 100)
                : (double)((total - prevTotal) / prevTotal * 100);

            // Seven-day series for the mini chart, Monday to Sunday, covering the week
            // containing the selected date.
            int offset = ((int)anchor.DayOfWeek + 6) % 7; // days since Monday
            var weekStart = anchor.AddDays(-offset);
            var weekData = new decimal[7];
            for (int i = 0; i < 7; i++)
            {
                var day = weekStart.AddDays(i);
                var dayTrips = allTrips.Where(t => InRoute(t) && t.Date.Date == day);
                weekData[i] = isPassenger ? dayTrips.Sum(passengers) : dayTrips.Where(Earned).Sum(t => t.EstimatedRevenue);
            }

            // The three busiest routes in the selected period.
            var topRoutes = current
                .GroupBy(t => t.RouteId)
                .Select(g => new
                {
                    routeId = g.Key,
                    name = routeNames.TryGetValue(g.Key, out var rn) ? rn : $"Route {g.Key:00}",
                    value = isPassenger ? (decimal)g.Sum(passengers) : g.Where(Earned).Sum(t => t.EstimatedRevenue)
                })
                .OrderByDescending(r => r.value)
                .Take(3)
                .ToList();

            decimal topSum = topRoutes.Sum(r => r.value);
            var topRoutesWithPct = topRoutes.Select(r => new
            {
                r.routeId,
                r.name,
                value = r.value,
                pct = topSum == 0 ? 0 : Math.Round((double)(r.value / topSum * 100))
            });

            return new
            {
                total,
                deltaPct = Math.Round(deltaPct, 1),
                comparisonLabel = period switch
                {
                    "This Day" => "vs Yesterday",
                    "Last Week" => "vs Previous Week",
                    "This Month" => "vs Last Month",
                    _ => "vs Last Week"
                },
                weekLabels = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" },
                weekData,
                topRoutes = topRoutesWithPct
            };
        }

        private static (DateTime start, DateTime end, DateTime prevStart, DateTime prevEnd) GetRange(DateTime anchor, string period)
        {
            switch (period)
            {
                case "This Day":
                    return (anchor, anchor, anchor.AddDays(-1), anchor.AddDays(-1));

                case "Last Week":
                    {
                        int offset = ((int)anchor.DayOfWeek + 6) % 7;
                        var thisWeekStart = anchor.AddDays(-offset);
                        var lastWeekStart = thisWeekStart.AddDays(-7);
                        var lastWeekEnd = thisWeekStart.AddDays(-1);
                        return (lastWeekStart, lastWeekEnd, lastWeekStart.AddDays(-7), lastWeekEnd.AddDays(-7));
                    }

                case "This Month":
                    {
                        var monthStart = new DateTime(anchor.Year, anchor.Month, 1);
                        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
                        var prevMonthStart = monthStart.AddMonths(-1);
                        var prevMonthEnd = monthStart.AddDays(-1);
                        return (monthStart, monthEnd, prevMonthStart, prevMonthEnd);
                    }

                default: // "This Week"
                    {
                        int offset = ((int)anchor.DayOfWeek + 6) % 7;
                        var weekStart = anchor.AddDays(-offset);
                        var weekEnd = weekStart.AddDays(6);
                        return (weekStart, weekEnd, weekStart.AddDays(-7), weekEnd.AddDays(-7));
                    }
            }
        }

        /// <summary>The trips behind one of the figures on a summary card.</summary>
        /// <remarks>
        /// A card states a number and nothing else, so an operator reading a bad one has to
        /// go looking for the trips that made it. Each card answers for itself here, with
        /// the same day and route the page is already showing.
        /// </remarks>
        [HttpGet]
        public async Task<IActionResult> StatBreakdown(string kind, int? routeId, DateTime? date)
        {
            var anchor = (date ?? PhClock.OperationalDay).Date;

            var routesResp = await _supabase.From<BusRoute>().Get();
            var routeNames = routesResp.Models.ToDictionary(r => r.RouteId, r => r.RouteName);

            var usersResp = await _supabase.From<UserModel>().Get();
            var userNames = usersResp.Models.ToDictionary(u => u.UserId, u => $"{u.FirstName} {u.LastName}");

            var vehiclesResp = await _supabase.From<Vehicle>().Get();
            var plates = vehiclesResp.Models.ToDictionary(v => v.VehicleId, v => v.PlateNumber);

            var tripsResp = await _supabase.From<Trip>().Get();

            var day = tripsResp.Models
                .Where(t => t.Date.Date == anchor)
                .Where(t => !routeId.HasValue || routeId.Value == 0 || t.RouteId == routeId.Value)
                .OrderBy(t => t.RouteId)
                .ThenBy(t => t.ShiftStartTime)
                .ToList();

            // Each card counts a different thing, so each answers with the trips it counted.
            var trips = kind switch
            {
                "missed" => day.Where(t => DeriveStatus(t) == "Missed").ToList(),
                "passengers" => day.Where(t => t.TotalBoarded > 0).ToList(),
                "revenue" => day.Where(Earned).Where(t => t.EstimatedRevenue > 0).ToList(),
                _ => day.Where(Earned).ToList(),
            };

            string Route(Trip t) => routeNames.TryGetValue(t.RouteId, out var rn) ? rn : $"Route {t.RouteId}";
            string Driver(Trip t) => userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A";
            string Bus(Trip t) => plates.TryGetValue(t.VehicleId, out var pn) ? $"{t.VehicleId} · {pn}" : t.VehicleId;

            var (title, columns) = kind switch
            {
                "missed" => ("Missed Trips",
                    new[] { "Trip ID", "Route", "Driver", "Bus", "Shift", "Scheduled" }),
                "passengers" => ("Passengers Carried",
                    new[] { "Trip ID", "Route", "Driver", "Bus", "Shift", "Status", "Passengers" }),
                "revenue" => ("Revenue Earned",
                    new[] { "Trip ID", "Route", "Driver", "Bus", "Shift", "Passengers", "Revenue" }),
                _ => ("Completed Trips",
                    new[] { "Trip ID", "Route", "Driver", "Bus", "Shift", "Passengers", "Revenue" }),
            };

            Func<Trip, string[]> row = kind switch
            {
                "missed" => t => new[] { t.TripId, Route(t), Driver(t), Bus(t), t.ShiftType ?? "", ShiftRange(t) },
                "passengers" => t => new[] { t.TripId, Route(t), Driver(t), Bus(t), t.ShiftType ?? "", DeriveStatus(t), t.TotalBoarded.ToString("N0") },
                _ => t => new[] { t.TripId, Route(t), Driver(t), Bus(t), t.ShiftType ?? "", t.TotalBoarded.ToString("N0"), Money(t) },
            };

            return Json(new
            {
                title,
                date = anchor.ToString("MMMM d, yyyy"),
                count = trips.Count,
                columns,
                rows = trips.Select(row).ToList(),
            });
        }

        /// <summary>Trip detail for the modal, requested when a row's view button is used.</summary>
        [HttpGet]
        public async Task<IActionResult> TripDetail(string tripId)
        {
            if (string.IsNullOrEmpty(tripId))
                return BadRequest("Trip ID is required.");

            Trip? tripResponse;
            try
            {
                tripResponse = await _supabase
                    .From<Trip>()
                    .Filter("trip_id", Operator.Equals, tripId)
                    .Single();
            }
            catch
            {
                return NotFound("Trip not found.");
            }

            if (tripResponse is null)
                return NotFound("Trip not found.");

            UserModel? driverResponse = null;
            try
            {
                driverResponse = await _supabase
                    .From<UserModel>()
                    .Filter("user_id", Operator.Equals, tripResponse.DriverId.ToString())
                    .Single();
            }
            catch { /* driver may not exist */ }

            Vehicle? vehicleResponse = null;
            try
            {
                vehicleResponse = await _supabase
                    .From<Vehicle>()
                    .Filter("vehicle_id", Operator.Equals, tripResponse.VehicleId)
                    .Single();
            }
            catch { /* vehicle may not exist */ }

            BusRoute? routeResponse = null;
            try
            {
                routeResponse = await _supabase
                    .From<BusRoute>()
                    .Filter("route_id", Operator.Equals, tripResponse.RouteId.ToString())
                    .Single();
            }
            catch { /* route may not exist */ }

            var telemetryResponse = await _supabase
                .From<TelemetryData>()
                .Filter("trip_id", Operator.Equals, tripId)
                .Order("timestamp", Ordering.Descending)
                .Limit(1)
                .Get();

            var latestTelemetry = telemetryResponse.Models.FirstOrDefault();

            // Telemetry passenger counts are sparse and frequently zero on real trips, so
            // the trip's own total is used instead. Without the fallback the modal reports
            // zero for trips that carried passengers.
            var liveBoarded = latestTelemetry?.TotalPassengers ?? 0;
            if (liveBoarded <= 0) liveBoarded = tripResponse.TotalBoarded;

            var result = new
            {
                tripId = tripResponse.TripId,
                shiftType = tripResponse.ShiftType,
                shiftStart = ShiftStartAt(tripResponse).ToString("hh:mm tt"),
                shiftEnd = ShiftEndLabel(tripResponse),
                routeName = routeResponse?.RouteName ?? "N/A",
                vehicleType = "Bus", // the vehicle_type column was dropped; every unit is a bus
                vehicleId = vehicleResponse?.VehicleId ?? "N/A",
                plateNumber = vehicleResponse?.PlateNumber ?? "N/A",
                driverName = driverResponse != null
                    ? $"{driverResponse.FirstName} {driverResponse.LastName}"
                    : "N/A",
                driverId = tripResponse.DriverId,
                totalPassengers = liveBoarded,
                estimatedRevenue = tripResponse.EstimatedRevenue,
                tripStatus = DeriveStatus(tripResponse),
                date = tripResponse.Date.ToString("MMMM dd, yyyy")
            };

            return Json(result);
        }
        /// <summary>Filter options for the report generation modal.</summary>
        [HttpGet]
        public async Task<IActionResult> GetFilterOptions()
        {
            var routesResp = await _supabase.From<BusRoute>().Order("route_name", Postgrest.Constants.Ordering.Ascending).Get();
            var usersResp = await _supabase.From<UserModel>().Get();
            var vehiclesResp = await _supabase.From<Vehicle>().Get();
            var tripsResp = await _supabase.From<Trip>().Get();

            // Role 2 is the driver role.
            var driverIds = usersResp.Models
                .Where(u => u.RoleId == 2)
                .Select(u => u.UserId)
                .ToHashSet();

            // Routes each driver has driven, taken from their trips.
            var driverRoutes = tripsResp.Models
                .Where(t => driverIds.Contains(t.DriverId))
                .GroupBy(t => t.DriverId)
                .ToDictionary(g => g.Key, g => g.Select(t => t.RouteId).Distinct().ToList());

            // Routes each vehicle has run, taken from its trips.
            var vehicleRoutes = tripsResp.Models
                .GroupBy(t => t.VehicleId)
                .ToDictionary(g => g.Key, g => g.Select(t => t.RouteId).Distinct().ToList());

            var drivers = usersResp.Models
                .Where(u => u.RoleId == 2)
                .OrderBy(u => u.LastName)
                .Select(u => new
                {
                    id = u.UserId,
                    name = $"{u.FirstName} {u.LastName}",
                    routeIds = driverRoutes.TryGetValue(u.UserId, out var r) ? r : new List<int>()
                });

            var vehicles = vehiclesResp.Models
                .OrderBy(v => v.VehicleId)
                .Select(v => new
                {
                    id = v.VehicleId,
                    label = $"{v.VehicleId} — {v.PlateNumber}",
                    routeIds = vehicleRoutes.TryGetValue(v.VehicleId, out var r) ? r : new List<int>()
                });

            var routes = routesResp.Models
                .Select(r => new { id = r.RouteId, name = r.RouteName });

            return Json(new { routes, drivers, vehicles });
        }

        /// <summary>Builds the report preview, grouped by route.</summary>
        [HttpGet]
        public async Task<IActionResult> GenerateReport(
            string reportType,
            DateTime? date,
            DateTime? dateTo,
            int? routeId,
            int? driverId,
            string? vehicleId)
        {
            // Defaults to the current operational day, 06:00 to 05:59 the next morning,
            // rather than the calendar day. Before 6 AM that is still the previous day.
            var (from, to) = Period(date, dateTo);

            // Reference data.
            var routesResp = await _supabase.From<BusRoute>().Get();
            var routeNames = routesResp.Models.ToDictionary(r => r.RouteId, r => r.RouteName);

            var usersResp = await _supabase.From<UserModel>().Get();
            var userNames = usersResp.Models.ToDictionary(u => u.UserId, u => $"{u.FirstName} {u.LastName}");

            var vehiclesResp = await _supabase.From<Vehicle>().Get();
            var vehiclesById = vehiclesResp.Models.ToDictionary(v => v.VehicleId, v => v);

            var tripsResp = await _supabase.From<Trip>().Get();
            var allTrips = tripsResp.Models;

            int Passengers(Trip t) => t.TotalBoarded;

            // Apply filters.
            var filtered = ReportableTrips(allTrips, from, to, routeId, driverId, vehicleId);

            object groups = reportType switch
            {
                "Passenger" => BuildPassengerReport(filtered, routeNames, userNames, Passengers),
                "Revenue" => BuildRevenueReport(filtered, routeNames, userNames, vehiclesById),
                _ => BuildDailyTripReport(filtered, routeNames, userNames, vehiclesById, Passengers),
            };

            // Totals across every filtered trip, shown at the end of the report.
            var totals = new
            {
                trips = filtered.Count,
                passengers = filtered.Sum(Passengers),
                revenue = filtered.Where(Earned).Sum(t => t.EstimatedRevenue),
            };

            // The heading is written here rather than in the browser, so the preview and the
            // downloaded file name the same days in the same words.
            return Json(new { groups, totals, period = PeriodLabel(from, to) });
        }

        /// <summary>The trips a report covers, filtered and in reading order.</summary>
        /// <remarks>
        /// A report says what happened, so a trip still running or not yet due out is left
        /// out: it has nothing to report and would arrive as a row of zeroes, reading as a
        /// service that ran and carried nobody.
        ///
        /// One that never ran is kept and marked missed. A shift the fleet failed to cover
        /// is part of the day's account, and a report that hid it would show a day going
        /// better than it did.
        /// </remarks>
        private static List<Trip> ReportableTrips(
            IEnumerable<Trip> all, DateTime from, DateTime to,
            int? routeId, int? driverId, string? vehicleId) =>
            all.Where(t => t.Date.Date >= from && t.Date.Date <= to)
               .Where(t => !routeId.HasValue || t.RouteId == routeId.Value)
               .Where(t => !driverId.HasValue || t.DriverId == driverId.Value)
               .Where(t => string.IsNullOrEmpty(vehicleId) || t.VehicleId == vehicleId)
               .Where(Concluded)
               .OrderBy(t => t.RouteId)
               .ThenBy(t => t.Date)
               .ThenBy(t => t.ShiftStartTime)
               .ToList();

        /// <summary>The days a report covers, as a pair of operational days.</summary>
        /// <remarks>
        /// Both ends default to the current operational day, so asking for no dates gives
        /// the same single day it always did. A pair given backwards is read as the range
        /// the reader meant rather than refused, since the two boxes carry no order of
        /// their own and an empty report would not say why.
        /// </remarks>
        private static (DateTime From, DateTime To) Period(DateTime? from, DateTime? to)
        {
            var a = (from ?? PhClock.OperationalDay).Date;
            var b = (to ?? from ?? PhClock.OperationalDay).Date;
            return b < a ? (b, a) : (a, b);
        }

        /// <summary>How the covered days read on the report itself.</summary>
        private static string PeriodLabel(DateTime from, DateTime to) =>
            from == to
                ? OpDayLabel(from)
                : $"{from:MMMM d, yyyy} to {to:MMMM d, yyyy}"
                  + $"  •  {(to - from).Days + 1} operational days";

        /// <summary>The covered days as a file name fragment.</summary>
        private static string PeriodStamp(DateTime from, DateTime to) =>
            from == to ? $"{from:yyyy-MM-dd}" : $"{from:yyyy-MM-dd}_to_{to:yyyy-MM-dd}";

        /// <summary>Whether a trip has finished, whether by running or by being missed.</summary>
        private static bool Concluded(Trip t) => DeriveStatus(t) is "Completed" or "Missed";

        /// <summary>
        /// What a trip took, which is nothing unless it ran.
        /// </summary>
        /// <remarks>
        /// A missed trip carries whatever revenue was planned for it. Printing that figure
        /// beside the trips that earned theirs would add money to the day that nobody paid.
        /// </remarks>
        private static string Money(Trip t) => $"₱{EarnedAmount(t):N2}";

        /// <summary>The same figure unformatted, for a column a spreadsheet will add up.</summary>
        private static decimal EarnedAmount(Trip t) => Earned(t) ? t.EstimatedRevenue : 0m;

        private static object BuildDailyTripReport(
            List<Trip> trips,
            Dictionary<int, string> routeNames,
            Dictionary<int, string> userNames,
            Dictionary<string, Vehicle> vehiclesById,
            Func<Trip, int> passengers)
        {
            var groups = trips
                .GroupBy(t => routeNames.TryGetValue(t.RouteId, out var rn) ? rn : $"Route {t.RouteId}")
                .Select(g => new
                {
                    groupName = g.Key,
                    columns = new[] { "Trip ID", "Date", "Driver", "Bus ID", "Shift", "Shift Time", "Status", "Passengers", "Revenue" },
                    rows = g.Select(t => new[]
                    {
                        t.TripId,
                        t.Date.ToString("MMM dd, yyyy"),
                        userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A",
                        vehiclesById.TryGetValue(t.VehicleId, out var v) ? v.PlateNumber : t.VehicleId,
                        t.ShiftType ?? "",
                        ShiftRange(t),
                        DeriveStatus(t),
                        passengers(t).ToString(),
                        Money(t)
                    }).ToList()
                })
                .ToList();

            return groups;
        }

        private static object BuildPassengerReport(
            List<Trip> trips,
            Dictionary<int, string> routeNames,
            Dictionary<int, string> userNames,
            Func<Trip, int> passengers)
        {
            var groups = trips
                .GroupBy(t => routeNames.TryGetValue(t.RouteId, out var rn) ? rn : $"Route {t.RouteId}")
                .Select(g => new
                {
                    groupName = g.Key,
                    columns = new[] { "Trip ID", "Date", "Driver", "Shift", "Shift Time", "Status", "Passengers" },
                    rows = g.Select(t => new[]
                    {
                        t.TripId,
                        t.Date.ToString("MMM dd, yyyy"),
                        userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A",
                        t.ShiftType ?? "",
                        ShiftRange(t),
                        DeriveStatus(t),
                        passengers(t).ToString()
                    }).ToList()
                })
                .ToList();

            return groups;
        }

        private static object BuildRevenueReport(
            List<Trip> trips,
            Dictionary<int, string> routeNames,
            Dictionary<int, string> userNames,
            Dictionary<string, Vehicle> vehiclesById)
        {
            var groups = trips
                .GroupBy(t => routeNames.TryGetValue(t.RouteId, out var rn) ? rn : $"Route {t.RouteId}")
                .Select(g => new
                {
                    groupName = g.Key,
                    columns = new[] { "Trip ID", "Date", "Driver", "Bus ID", "Shift", "Status", "Revenue" },
                    rows = g.Select(t => new[]
                    {
                        t.TripId,
                        t.Date.ToString("MMM dd, yyyy"),
                        userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A",
                        vehiclesById.TryGetValue(t.VehicleId, out var v) ? v.PlateNumber : t.VehicleId,
                        t.ShiftType ?? "",
                        DeriveStatus(t),
                        Money(t)
                    }).ToList()
                })
                .ToList();

            return groups;
        }

        /// <summary>Renders the report as a PDF.</summary>
        [HttpGet]
        public async Task<IActionResult> DownloadReport(
            string reportType,
            DateTime? date,
            DateTime? dateTo,
            int? routeId,
            int? driverId,
            string? vehicleId)
        {
            // Defaults to the current operational day, 06:00 to 05:59 the next morning,
            // rather than the calendar day. Before 6 AM that is still the previous day.
            var (from, to) = Period(date, dateTo);

            var routesResp = await _supabase.From<BusRoute>().Get();
            var routeNames = routesResp.Models.ToDictionary(r => r.RouteId, r => r.RouteName);

            var usersResp = await _supabase.From<UserModel>().Get();
            var userNames = usersResp.Models.ToDictionary(u => u.UserId, u => $"{u.FirstName} {u.LastName}");

            var vehiclesResp = await _supabase.From<Vehicle>().Get();
            var vehiclesById = vehiclesResp.Models.ToDictionary(v => v.VehicleId, v => v);

            var tripsResp = await _supabase.From<Trip>().Get();

            int Passengers(Trip t) => t.TotalBoarded;

            var filtered = ReportableTrips(tripsResp.Models, from, to, routeId, driverId, vehicleId);

            // Build report data.
            string reportTitle = reportType switch
            {
                "Passenger" => "Passenger Reports",
                "Revenue" => "Revenue Reports",
                _ => "Daily Trip Reports"
            };

            string fileName = reportType switch
            {
                "Passenger" => $"PassengerReport_{PeriodStamp(from, to)}.pdf",
                "Revenue" => $"RevenueReport_{PeriodStamp(from, to)}.pdf",
                _ => $"DailyTripReport_{PeriodStamp(from, to)}.pdf"
            };

            string[] columns = reportType switch
            {
                "Passenger" => new[] { "Trip ID", "Date", "Driver", "Route", "Shift", "Shift Time", "Status", "Passengers" },
                "Revenue" => new[] { "Trip ID", "Date", "Driver", "Bus ID", "Route", "Shift", "Status", "Revenue" },
                _ => new[] { "Trip ID", "Date", "Driver", "Bus ID", "Route", "Shift", "Actual Start", "Actual End", "Status", "Passengers", "Revenue" }
            };

            Func<Trip, string[]> rowBuilder = reportType switch
            {
                "Passenger" => t => new[]
                {
                    t.TripId,
                    t.Date.ToString("MMM dd, yyyy"),
                    userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A",
                    routeNames.TryGetValue(t.RouteId, out var rn) ? rn : "N/A",
                    t.ShiftType ?? "",
                    ShiftRange(t),
                    DeriveStatus(t),
                    Passengers(t).ToString()
                },
                "Revenue" => t => new[]
                {
                    t.TripId,
                    t.Date.ToString("MMM dd, yyyy"),
                    userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A",
                    vehiclesById.TryGetValue(t.VehicleId, out var v) ? v.PlateNumber : t.VehicleId,
                    routeNames.TryGetValue(t.RouteId, out var rn) ? rn : "N/A",
                    t.ShiftType ?? "",
                    DeriveStatus(t),
                    Money(t)
                },
                _ => t => new[]
                {
                    t.TripId,
                    t.Date.ToString("MMM dd, yyyy"),
                    userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A",
                    vehiclesById.TryGetValue(t.VehicleId, out var v) ? v.PlateNumber : t.VehicleId,
                    routeNames.TryGetValue(t.RouteId, out var rn) ? rn : "N/A",
                    t.ShiftType ?? "",
                    FmtActual(t.ActualStartTime),
                    FmtActual(t.ActualEndTime),
                    DeriveStatus(t),
                    Passengers(t).ToString(),
                    Money(t)
                }
            };

            // Grouped by route name.
            var groups = filtered
                .GroupBy(t => routeNames.TryGetValue(t.RouteId, out var rn) ? rn : $"Route {t.RouteId}")
                .Select(g => new ReportGroup
                {
                    GroupName = g.Key,
                    Rows = g.Select(rowBuilder).ToList()
                })
                .ToList();

            // Generate PDF with QuestPDF.
            QuestPDF.Settings.License = LicenseType.Community;

            var accentColor = reportType switch
            {
                "Passenger" => "#3B82F6",
                "Revenue" => "#D63384",
                _ => "#27AE60"
            };

            var pdfBytes = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4.Landscape());
                    page.Margin(32, Unit.Point);
                    page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(9).FontColor("#2D3748"));

                    // Header.
                    page.Header().Column(col =>
                    {
                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Column(inner =>
                            {
                                inner.Item().Text("RouteSync")
                                    .Bold().FontSize(16).FontColor(accentColor);
                                inner.Item().Text(reportTitle)
                                    .Bold().FontSize(11).FontColor("#2D3748");
                            });
                            row.ConstantItem(220).AlignRight().Column(inner =>
                            {
                                inner.Item().Text("Operational Day")
                                    .Bold().FontSize(8).FontColor("#9AA5B4").LetterSpacing(0.06f);
                                inner.Item().Text(PeriodLabel(from, to))
                                    .Bold().FontSize(9.5f).FontColor("#2D3748");
                                inner.Item().Text($"Generated: {PhClock.Now:MMM dd, yyyy hh:mm tt}")
                                    .FontSize(8).FontColor("#9AA5B4");
                            });
                        });

                        col.Item().PaddingTop(6).LineHorizontal(1.5f)
                            .LineColor(accentColor);
                    });

                    // Content.
                    page.Content().PaddingTop(12).Column(col =>
                    {
                        if (groups.Count == 0)
                        {
                            col.Item().AlignCenter().PaddingTop(40)
                                .Text("No data found for the selected filters.")
                                .FontColor("#9AA5B4").FontSize(10);
                            return;
                        }

                        foreach (var group in groups)
                        {
                            // Group label
                            col.Item().PaddingBottom(4).Text(group.GroupName)
                                .Bold().FontSize(9).FontColor("#9AA5B4")
                                .LetterSpacing(0.06f);

                            // Table
                            col.Item().PaddingBottom(14).Table(table =>
                            {
                                // Column definitions
                                table.ColumnsDefinition(def =>
                                {
                                    foreach (var _ in columns)
                                        def.RelativeColumn();
                                });

                                // Header row
                                table.Header(header =>
                                {
                                    foreach (var c in columns)
                                    {
                                        header.Cell()
                                            .Background("#F8F9FA")
                                            .BorderBottom(1).BorderColor("#E8ECF0")
                                            .Padding(5)
                                            .Text(c)
                                            .Bold().FontSize(8).FontColor("#9AA5B4");
                                    }
                                });

                                // Data rows
                                bool alt = false;
                                foreach (var rowData in group.Rows)
                                {
                                    foreach (var cell in rowData)
                                    {
                                        table.Cell()
                                            .Background(alt ? "#FAFBFC" : "#FFFFFF")
                                            .BorderBottom(1).BorderColor("#F0F0F0")
                                            .Padding(5)
                                            .Text(cell ?? "")
                                            .FontSize(8.5f);
                                    }
                                    alt = !alt;
                                }
                            });
                        }

                        // Clear grand total at the end.
                        var totalTrips = filtered.Count;
                        var totalPax = filtered.Sum(Passengers);
                        var totalRev = filtered.Where(Earned).Sum(t => t.EstimatedRevenue);
                        string totalsText = reportType switch
                        {
                            "Passenger" => $"Total Trips: {totalTrips}        Total Passengers: {totalPax:N0}",
                            "Revenue" => $"Total Trips: {totalTrips}        Total Revenue: ₱{totalRev:N2}",
                            _ => $"Total Trips: {totalTrips}        Total Passengers: {totalPax:N0}        Total Revenue: ₱{totalRev:N2}",
                        };
                        col.Item().PaddingTop(6).BorderTop(1.5f).BorderColor(accentColor)
                            .PaddingTop(8).Row(row =>
                            {
                                row.RelativeItem().Text("TOTAL")
                                    .Bold().FontSize(10).FontColor(accentColor).LetterSpacing(0.06f);
                                row.RelativeItem().AlignRight().Text(totalsText)
                                    .Bold().FontSize(10).FontColor("#2D3748");
                            });
                    });

                    // Footer.
                    page.Footer().AlignCenter()
                        .Text(text =>
                        {
                            text.Span("RouteSync  •  ").FontColor("#9AA5B4").FontSize(8);
                            text.Span(reportTitle).FontColor("#9AA5B4").FontSize(8);
                            text.Span("  •  Page ").FontColor("#9AA5B4").FontSize(8);
                            text.CurrentPageNumber().FontColor("#9AA5B4").FontSize(8);
                            text.Span(" of ").FontColor("#9AA5B4").FontSize(8);
                            text.TotalPages().FontColor("#9AA5B4").FontSize(8);
                        });
                });
            }).GeneratePdf();

            return File(pdfBytes, "application/pdf", fileName);
        }

        /// <summary>Renders the report as CSV.</summary>
        [HttpGet]
        public async Task<IActionResult> DownloadReportCsv(
            string reportType,
            DateTime? date,
            DateTime? dateTo,
            int? routeId,
            int? driverId,
            string? vehicleId)
        {
            // Defaults to the current operational day, 06:00 to 05:59 the next morning,
            // rather than the calendar day. Before 6 AM that is still the previous day.
            var (from, to) = Period(date, dateTo);

            var routesResp = await _supabase.From<BusRoute>().Get();
            var routeNames = routesResp.Models.ToDictionary(r => r.RouteId, r => r.RouteName);

            var usersResp = await _supabase.From<UserModel>().Get();
            var userNames = usersResp.Models.ToDictionary(u => u.UserId, u => $"{u.FirstName} {u.LastName}");

            var vehiclesResp = await _supabase.From<Vehicle>().Get();
            var vehiclesById = vehiclesResp.Models.ToDictionary(v => v.VehicleId, v => v);

            var tripsResp = await _supabase.From<Trip>().Get();

            int Passengers(Trip t) => t.TotalBoarded;

            var filtered = ReportableTrips(tripsResp.Models, from, to, routeId, driverId, vehicleId);

            string CsvEscape(string s) =>
                s != null && (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
                    ? $"\"{s.Replace("\"", "\"\"")}\""
                    : (s ?? "");

            var sb = new System.Text.StringBuilder();
            string fileName;

            // A banner naming the operational day the report covers, then a blank line.
            var reportLabel = reportType switch
            {
                "Passenger" => "Passenger Report",
                "Revenue" => "Revenue Report",
                _ => "Daily Trip Report"
            };
            sb.AppendLine("RouteSync - " + reportLabel);
            sb.AppendLine((from == to ? "Operational Day," : "Period,") + CsvEscape(PeriodLabel(from, to)));
            sb.AppendLine();

            switch (reportType)
            {
                case "Passenger":
                    fileName = $"PassengerReport_{PeriodStamp(from, to)}.csv";
                    sb.AppendLine("Trip ID,Date,Driver,Route,Shift,Shift Time,Status,Passengers");
                    foreach (var t in filtered)
                        sb.AppendLine(string.Join(",",
                            CsvEscape(t.TripId),
                            CsvEscape(t.Date.ToString("MMM dd, yyyy")),
                            CsvEscape(userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A"),
                            CsvEscape(routeNames.TryGetValue(t.RouteId, out var rn) ? rn : "N/A"),
                            CsvEscape(t.ShiftType ?? ""),
                            CsvEscape(ShiftRange(t, "-")),
                            CsvEscape(DeriveStatus(t)),
                            Passengers(t).ToString()));
                    break;

                case "Revenue":
                    fileName = $"RevenueReport_{PeriodStamp(from, to)}.csv";
                    sb.AppendLine("Trip ID,Date,Driver,Bus ID,Route,Shift,Status,Revenue");
                    foreach (var t in filtered)
                        sb.AppendLine(string.Join(",",
                            CsvEscape(t.TripId),
                            CsvEscape(t.Date.ToString("MMM dd, yyyy")),
                            CsvEscape(userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A"),
                            CsvEscape(vehiclesById.TryGetValue(t.VehicleId, out var v) ? v.PlateNumber : t.VehicleId),
                            CsvEscape(routeNames.TryGetValue(t.RouteId, out var rn) ? rn : "N/A"),
                            CsvEscape(t.ShiftType ?? ""),
                            CsvEscape(DeriveStatus(t)),
                            EarnedAmount(t).ToString("F2")));
                    break;

                default:
                    fileName = $"DailyTripReport_{PeriodStamp(from, to)}.csv";
                    sb.AppendLine("Trip ID,Date,Driver,Bus ID,Route,Shift,Shift Time,Actual Start,Actual End,Status,Passengers,Revenue");
                    foreach (var t in filtered)
                        sb.AppendLine(string.Join(",",
                            CsvEscape(t.TripId),
                            CsvEscape(t.Date.ToString("MMM dd, yyyy")),
                            CsvEscape(userNames.TryGetValue(t.DriverId, out var dn) ? dn : "N/A"),
                            CsvEscape(vehiclesById.TryGetValue(t.VehicleId, out var v) ? v.PlateNumber : t.VehicleId),
                            CsvEscape(routeNames.TryGetValue(t.RouteId, out var rn) ? rn : "N/A"),
                            CsvEscape(t.ShiftType ?? ""),
                            CsvEscape(ShiftRange(t, "-")),
                            CsvEscape(FmtActual(t.ActualStartTime)),
                            CsvEscape(FmtActual(t.ActualEndTime)),
                            CsvEscape(DeriveStatus(t)),
                            Passengers(t).ToString(),
                            EarnedAmount(t).ToString("F2")));
                    break;
            }

            // Clear grand total at the end.
            sb.AppendLine();
            sb.AppendLine("TOTAL");
            sb.AppendLine($"Total Trips,{filtered.Count}");
            if (reportType != "Revenue")
                sb.AppendLine($"Total Passengers,{filtered.Sum(Passengers)}");
            if (reportType != "Passenger")
                sb.AppendLine($"Total Revenue,{filtered.Where(Earned).Sum(t => t.EstimatedRevenue):F2}");

            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            return File(bytes, "text/csv", fileName);
        }

        // Shift-time helpers.
        //
        // A trip is dated by the day it starts. A shift whose end is at or before its start
        // runs overnight and ends the following day, so these build times from the trip's
        // own date rather than today's, and roll the end forward when needed.
        private static bool IsOvernight(Trip t) => t.ShiftEndTime <= t.ShiftStartTime;
        private static DateTime ShiftStartAt(Trip t) => t.Date.Date + t.ShiftStartTime;
        private static DateTime ShiftEndAt(Trip t) => t.Date.Date + t.ShiftEndTime
            + (IsOvernight(t) ? TimeSpan.FromDays(1) : TimeSpan.Zero);

        /// <summary>Whether a trip has earned its revenue, which only a finished one has.</summary>
        /// <remarks>
        /// estimated_revenue is written when a trip completes, so summing every row trusts
        /// the column over the trip's state.
        /// </remarks>
        private static bool Earned(Trip t) => t.TripStatus == "Completed";

        /// <summary>
        /// The trip's status, with missed derived rather than stored: a trip past its shift
        /// window that never started or completed was missed. Matches the dispatch board.
        /// </summary>
        private static string DeriveStatus(Trip t)
        {
            if (string.Equals(t.TripStatus, "Completed", StringComparison.OrdinalIgnoreCase)) return "Completed";
            if (string.Equals(t.TripStatus, "Active", StringComparison.OrdinalIgnoreCase)) return "Active";
            if (ShiftEndAt(t) < PhClock.Now) return "Missed";
            return "Not Yet Started";
        }

        /// <summary>
        /// The end time alone, without a date or a next-day marker. The operational-day
        /// header already establishes the window the report covers.
        /// </summary>
        private static string ShiftEndLabel(Trip t) => ShiftEndAt(t).ToString("hh:mm tt");

        /// <summary>The full shift window, for example "10:00 PM - 06:00 AM".</summary>
        private static string ShiftRange(Trip t, string dash = "–") =>
            $"{ShiftStartAt(t):hh:mm tt} {dash} {ShiftEndLabel(t)}";

        /// <summary>
        /// The logged start or end time, or a placeholder when the trip never recorded one.
        /// </summary>
        /// <remarks>The stored value returns as a local-kind timestamp shifted eight hours
        /// ahead, so it is normalized back to recover the digits as written.</remarks>
        private static string FmtActual(DateTime? dt) =>
            dt.HasValue ? dt.Value.ToUniversalTime().ToString("hh:mm tt") : "—";

        /// <summary>The operational-day banner, naming both ends of the service window.</summary>
        private static string OpDayLabel(DateTime anchor) =>
            $"{anchor:MMMM d, yyyy}  •  6:00 AM – {anchor.AddDays(1):MMMM d}, 5:59 AM";

        // Report grouping model.
        private class ReportGroup
        {
            public string GroupName { get; set; } = "";
            public List<string[]> Rows { get; set; } = new();
        }
    }
}
