using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Caching.Memory;
using FleetWise.Models;
using FleetWise.Services;

namespace FleetWise.Controllers
{
    [Authorize]
    public class DashboardController : Controller
    {
        private readonly Supabase.Client _supabase;
        private readonly IMemoryCache _cache;

        public DashboardController(Supabase.Client supabase, IMemoryCache cache)
        {
            _supabase = supabase;
            _cache = cache;
        }

        /// <summary>How many past weeks the usual day is averaged over.</summary>
        private const int UsualWeeks = 4;

        /// <summary>
        /// How long the usual day is kept between refreshes. It is built from days already
        /// over, so it changes only when the day does, and the page asks every few seconds.
        /// </summary>
        private static readonly TimeSpan UsualFreshness = TimeSpan.FromMinutes(10);

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
                chartData = new { bars = vm.ChartData, usual = vm.ChartUsual, now = vm.ChartNowIndex },
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
            int todayPassengers = todayTrips.Sum(t => t.TotalBoarded);
            int yesterdayPassengers = yesterdayTrips.Sum(t => t.TotalBoarded);

            // Boardings hour by hour across the service day, 06:00 to 05:59, with the usual
            // day beside them: the same hours on the same weekday in the weeks before.
            var now = PhClock.Now;
            var cycleStart = HourlyBoardings.CycleStart(today);
            var hourly = HourlyBoardings.ByHour(today, todayTrips, await EventsForAsync(today), now);
            var usual = await UsualAsync(today, routeId);

            var labels = Enumerable.Range(0, HourlyBoardings.Hours)
                .Select(h => HourLabel(cycleStart.AddHours(h)))
                .ToList();

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
                ChartData = hourly.ToList(),
                ChartUsual = usual?.ToList(),
                ChartNowIndex = (int)Math.Floor((now - cycleStart).TotalHours),
                Routes = routes,
                SelectedRouteId = routeId,
                Today = today,
                ActiveTripBreakdown = tripBreakdown,
            };

            return vm;
        }

        private static string HourLabel(DateTime at) => at.Hour switch
        {
            0 => "12:00 AM",
            12 => "12:00 PM",
            < 12 => $"{at.Hour}:00 AM",
            _ => $"{at.Hour - 12}:00 PM",
        };

        /// <summary>The boarding events around one service day.</summary>
        /// <remarks>
        /// Read by time rather than by trip, since a day's trips can number more than a filter
        /// on their identifiers carries. The window runs on past the day's end so a trip that
        /// finished late still has all of its events counted against its total.
        /// </remarks>
        private async Task<List<BoardingEvent>> EventsForAsync(DateTime day)
        {
            var from = new DateTimeOffset(HourlyBoardings.CycleStart(day), TimeSpan.FromHours(8)).UtcDateTime;
            var to = from.AddHours(HourlyBoardings.Hours + 12);

            return await PagedRead.AllAsync(() => _supabase.From<BoardingEvent>()
                .Select("event_id,trip_id,direction,device_timestamp")
                .Filter("direction", Postgrest.Constants.Operator.Equals, "in")
                .Filter("device_timestamp", Postgrest.Constants.Operator.GreaterThanOrEqual, from.ToString("yyyy-MM-ddTHH:mm:ssZ"))
                .Filter("device_timestamp", Postgrest.Constants.Operator.LessThan, to.ToString("yyyy-MM-ddTHH:mm:ssZ"))
                .Order("event_id", Postgrest.Constants.Ordering.Ascending));
        }

        /// <summary>
        /// The average boardings for each hour on the same weekday over the past four weeks,
        /// or null when none of those days had any.
        /// </summary>
        private async Task<double[]?> UsualAsync(DateTime today, int? routeId)
        {
            var key = $"dashboard_usual:{today:yyyy-MM-dd}:{routeId?.ToString() ?? "all"}";
            if (_cache.TryGetValue<double[]?>(key, out var held)) return held;

            var days = Enumerable.Range(1, UsualWeeks).Select(w => today.AddDays(-7 * w)).ToList();

            var trips = await PagedRead.AllAsync(() => _supabase.From<Trip>()
                .Filter("date", Postgrest.Constants.Operator.In, days.Select(d => (object)d.ToString("yyyy-MM-dd")).ToList())
                .Order("trip_id", Postgrest.Constants.Ordering.Ascending));

            var perDay = new List<int?[]>();
            foreach (var day in days)
            {
                var dayTrips = trips
                    .Where(t => t.Date.Date == day && (!routeId.HasValue || t.RouteId == routeId.Value))
                    .ToList();
                var events = dayTrips.Count == 0 ? new List<BoardingEvent>() : await EventsForAsync(day);
                perDay.Add(HourlyBoardings.ByHour(day, dayTrips, events, HourlyBoardings.CycleStart(day).AddDays(2)));
            }

            var usual = HourlyBoardings.Usual(perDay);
            _cache.Set(key, usual, UsualFreshness);
            return usual;
        }
    }
}
