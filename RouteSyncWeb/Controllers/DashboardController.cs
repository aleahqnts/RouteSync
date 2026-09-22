using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using FleetWise.Models;
using FleetWise.Services;

namespace FleetWise.Controllers
{
    [Authorize]
    public class DashboardController : Controller
    {
        private readonly Supabase.Client _supabase;

        public DashboardController(Supabase.Client supabase) => _supabase = supabase;

        public async Task<IActionResult> Index(int? routeId) => View(await BuildAsync(routeId));

        /// <summary>
        /// The figures the dashboard is watching, without the page around them.
        /// </summary>
        /// <remarks>
        /// The page refreshes itself often enough that re-rendering the whole of it each
        /// time was the most expensive thing the dashboard did: every poll ran the view,
        /// sent the markup for cards and a map that never change, and threw all but the
        /// numbers away. Only the figures travel here, so the refresh can be frequent
        /// enough to be worth having.
        /// </remarks>
        [HttpGet]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> Stats(int? routeId)
        {
            var vm = await BuildAsync(routeId);
            return Json(new
            {
                activeTrips = vm.ActiveTrips,
                flaggedVehicles = vm.FlaggedVehicles,
                totalPassengers = vm.TotalPassengers,
                passengerDelta = vm.PassengerDelta,
                totalRevenue = vm.TotalRevenue,
                revenueDelta = vm.RevenueDelta,
                chartData = vm.ChartData,
                breakdown = vm.ActiveTripBreakdown
                    .Where(r => r.Passengers > 0)
                    .Select(r => new
                    {
                        tripId = r.TripId,
                        routeName = r.RouteName,
                        vehicleId = r.VehicleId,
                        shiftType = r.ShiftType,
                        status = r.Status,
                        passengers = r.Passengers,
                    }),
            });
        }

        private async Task<DashboardViewModel> BuildAsync(int? routeId)
        {
            // The service day is the current operational cycle, 06:00 to 05:59 the next
            // morning, rather than the calendar day. A trip is dated by the day it starts,
            // so a cycle's trips are exactly those dated today.
            var today = PhClock.OperationalDay;
            var yesterday = today.AddDays(-1);

            // Flagged vehicles, which the page filters do not affect, are buses with an
            // unresolved maintenance log. Counting the vehicle_status column instead reads
            // zero, because the next shift overwrites it. This matches how the dispatch
            // board and the vehicle registry define the same figure.
            var maintResponse = await _supabase.From<MaintenanceLog>().Get();
            int flaggedVehicles = maintResponse.Models
                .Where(l => l.ResolvedAt == null && l.VehicleId != null)
                .Select(l => l.VehicleId)
                .Distinct()
                .Count();

            // Base queries for today's and yesterday's trips.
            var todayTripsResponse = await _supabase
                .From<Trip>()
                .Filter("date", Postgrest.Constants.Operator.Equals, today.ToString("yyyy-MM-dd"))
                .Get();

            var yesterdayTripsResponse = await _supabase
                .From<Trip>()
                .Filter("date", Postgrest.Constants.Operator.Equals, yesterday.ToString("yyyy-MM-dd"))
                .Get();

            // Trips dated today already cover the whole cycle, since a night shift carries
            // its start day's date. Any trip dated yesterday that is still active is folded
            // in as well, so an overnight run that has not been ended does not disappear
            // when the cycle rolls over.
            var todayTrips = todayTripsResponse.Models
                .Concat(yesterdayTripsResponse.Models.Where(t => t.TripStatus == "Active"))
                .Where(t => !routeId.HasValue || t.RouteId == routeId.Value)
                .GroupBy(t => t.TripId).Select(g => g.First())   // de-dupe
                .ToList();

            var yesterdayTrips = yesterdayTripsResponse.Models
                .Where(t => !routeId.HasValue || t.RouteId == routeId.Value)
                .ToList();

            // Active Trips.
            int activeTrips = todayTrips.Count(t => t.TripStatus == "Active");

            static bool Earned(Trip t) => t.TripStatus == "Completed";

            // Revenue, from finished trips only. The column is written when a trip
            // completes, so counting every row trusts the value over the trip's state.
            decimal todayRevenue = todayTrips.Where(Earned).Sum(t => t.EstimatedRevenue);
            decimal yesterdayRevenue = yesterdayTrips.Where(Earned).Sum(t => t.EstimatedRevenue);

            // Passenger Count (from trips.total_boarded).
            var todayTripIds = todayTrips.Select(t => t.TripId).ToHashSet();
            var yesterdayTripIds = yesterdayTrips.Select(t => t.TripId).ToHashSet();

            int todayPassengers = todayTrips.Sum(t => t.TotalBoarded);
            int yesterdayPassengers = yesterdayTrips.Sum(t => t.TotalBoarded);

            // Telemetry feeds the hourly chart only. The stored trip total has no
            // breakdown within the day, so the chart still needs the raw readings.
            //
            // The window is this service cycle: 06:00 on the operational day inclusive, to
            // 06:00 the next morning exclusive.
            var cycleStart = today.Add(PhClock.DayStartTime);
            var cycleEnd = today.AddDays(1).Add(PhClock.DayStartTime);
            var telemetryResponse = await _supabase
                .From<TelemetryData>()
                .Filter("timestamp", Postgrest.Constants.Operator.GreaterThanOrEqual,
                        cycleStart.ToString("yyyy-MM-dd HH:mm:ss"))
                .Filter("timestamp", Postgrest.Constants.Operator.LessThan,
                        cycleEnd.ToString("yyyy-MM-dd HH:mm:ss"))
                .Get();

            // Hour marks across the full cycle: 25 points from 06:00 to 06:00, with both
            // ends shown.
            var markTimes = Enumerable.Range(0, 25).Select(i => cycleStart.AddHours(i)).ToList();

            var labels = markTimes
                .Select(dt => dt.Hour switch
                {
                    0 => "12:00 AM",
                    12 => "12:00 PM",
                    < 12 => $"{dt.Hour}:00 AM",
                    _ => $"{dt.Hour - 12}:00 PM",
                })
                .ToList();

            // The stored digits are already Philippine wall-clock time, so no offset is
            // added here. Doing so shifts them a second time.
            var todayTelemetry = telemetryResponse.Models
                .Where(t => todayTripIds.Contains(t.TripId))
                .ToList();
            var now = PhClock.Now;

            // Only boardings are recorded, never alightings, so occupancy cannot be known.
            // The chart shows passengers boarded cumulatively by hour: a figure that only
            // rises and finishes exactly at the trip's stored total.
            //
            // Each trip's window uses the actual start and end when the driver app recorded
            // them, and the scheduled shift otherwise, rolling forward when overnight.
            static DateTime FloorHour(DateTime d) => new(d.Year, d.Month, d.Day, d.Hour, 0, 0);
            var tripWindows = todayTrips.Select(t =>
            {
                var schedStart = t.Date.Date + t.ShiftStartTime;
                var schedEnd = t.Date.Date + t.ShiftEndTime
                    + (t.ShiftEndTime <= t.ShiftStartTime ? TimeSpan.FromDays(1) : TimeSpan.Zero);
                // Postgrest returns a local-kind timestamp, shifting the stored digits
                // eight hours ahead. Normalizing back aligns them with the hour marks.
                // Without it an overnight trip falls outside the cycle window and
                // disappears from the chart.
                var start = t.ActualStartTime?.ToUniversalTime() ?? schedStart;
                var end = t.ActualEndTime?.ToUniversalTime() ?? (t.TripStatus == "Active" ? now : schedEnd);
                // Guards against clock skew: a phone can record a start slightly ahead of
                // server time. The window never begins after the current time, is floored
                // to the hour so the first bucket is included, and never ends in the future.
                if (start > now) start = now;
                start = FloorHour(start);
                if (end > now) end = now;
                if (end < start) end = start;
                return new { Trip = t, Start = start, End = end };
            }).ToList();

            // Each hour mark sums every trip's boardings so far. Marks after the current
            // time return no value, so the line stops at now.
            var data = markTimes.Select(mark =>
            {
                if (mark > now) return (int?)null;
                int sum = 0;
                foreach (var w in tripWindows)
                {
                    if (mark < w.Start) continue;                       // trip hasn't started yet
                    if (mark > w.End) { sum += w.Trip.TotalBoarded; continue; } // ended earlier today -> count persists
                    // Boardings up to this hour: the highest telemetry reading seen so far,
                    // which only rises, capped at the trip's total. The current hour is
                    // anchored to that total so the chart agrees with the headline figure.
                    int boarded = todayTelemetry
                        .Where(x => x.TripId == w.Trip.TripId && x.Timestamp.ToUniversalTime() <= mark)
                        .Select(x => x.TotalPassengers)
                        .DefaultIfEmpty(0)
                        .Max();
                    boarded = Math.Min(boarded, w.Trip.TotalBoarded);
                    if (mark.AddHours(1) > w.End) boarded = w.Trip.TotalBoarded; // last/current hour = truth
                    sum += boarded;
                }
                return (int?)sum;
            }).ToList();

            var maxVal = data.Where(d => d.HasValue).Select(d => d!.Value).DefaultIfEmpty(0).Max();
            int yMax = maxVal > 0 ? (int)(Math.Ceiling((maxVal + 50) / 100.0) * 100) : 400;
            int yStep = yMax / 4;

            // Routes dropdown.
            var routesResponse = await _supabase
                .From<BusRoute>()
                .Order("route_name", Postgrest.Constants.Ordering.Ascending)
                .Get();

            var routes = routesResponse.Models
                .Select(r => new SelectListItem
                {
                    Value = r.RouteId.ToString(),
                    Text = r.RouteName,
                    Selected = routeId.HasValue && r.RouteId == routeId.Value
                })
                .ToList();

            // Passenger breakdown across every trip this cycle, for the totals modal.
            var routeNames = routesResponse.Models.ToDictionary(r => r.RouteId, r => r.RouteName);
            var tripBreakdown = todayTrips
                .OrderByDescending(t => t.TotalBoarded)
                .Select(t => new ActiveTripRow
                {
                    TripId = t.TripId,
                    RouteName = routeNames.TryGetValue(t.RouteId, out var rn) ? rn : $"Route {t.RouteId}",
                    VehicleId = t.VehicleId,
                    ShiftType = t.ShiftType,
                    Status = t.TripStatus,
                    Passengers = t.TotalBoarded,
                })
                .ToList();

            // Assemble the view model.
            var vm = new DashboardViewModel
            {
                ActiveTrips = activeTrips,
                FlaggedVehicles = flaggedVehicles,
                TotalPassengers = todayPassengers,
                PassengerDelta = todayPassengers - yesterdayPassengers,
                TotalRevenue = todayRevenue,
                RevenueDelta = todayRevenue - yesterdayRevenue,
                ChartLabels = labels,
                ChartData = data,
                ChartYMax = yMax,
                ChartYStep = yStep,
                Routes = routes,
                SelectedRouteId = routeId,
                Today = today,
                ActiveTripBreakdown = tripBreakdown,
            };

            return vm;
        }
    }
}
