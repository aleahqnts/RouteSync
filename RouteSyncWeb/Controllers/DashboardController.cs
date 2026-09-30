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

        /// <summary>
        /// The trip columns the dashboard reads. The page asks every few seconds, so each
        /// read carries only what the figures and the chart are worked out from.
        /// dashboard_figures reads the same columns for the cards.
        /// </summary>
        private const string TripColumns =
            "trip_id,date,route_id,vehicle_id,shift_type,shift_start_time,shift_end_time," +
            "trip_status,estimated_revenue,total_boarded,actual_end_time";

        /// <summary>
        /// How long the figures are shared between everyone watching the same route.
        /// </summary>
        /// <remarks>
        /// Just under the page's five-second refresh. A single viewer finds the last answer
        /// expired every time and is read for afresh, exactly as without sharing, while a
        /// room of viewers costs one read per refresh between them. Nobody is shown figures
        /// older than a refresh and a few seconds.
        /// </remarks>
        private static readonly TimeSpan FiguresShared = TimeSpan.FromSeconds(4);

        /// <summary>
        /// How long the hourly chart is shared, just under the minute the page waits between
        /// asking for it, on the same terms as <see cref="FiguresShared"/>.
        /// </summary>
        private static readonly TimeSpan ChartShared = TimeSpan.FromSeconds(55);

        /// <summary>The dashboard's figures at one moment, for everyone watching one route.</summary>
        private sealed record Figures(
            DateTime Today,
            IReadOnlyList<Trip> TodayTrips,
            int ActiveTrips,
            int FlaggedVehicles,
            int TodayPassengers,
            int YesterdayPassengers,
            decimal TodayRevenue,
            decimal YesterdayRevenue,
            IReadOnlyList<BusRoute> Routes,
            IReadOnlyList<ActiveTripRow> Breakdown);

        /// <summary>The hourly chart at one moment: today's bars, the average, and the hour now.</summary>
        private sealed record HourlyChart(int?[] Bars, double[]? Usual, int NowIndex);

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
        ///
        /// The hourly chart is the largest part of the answer and moves the least, so the page
        /// asks for it on only some refreshes. Without it, chartData is null and none of the
        /// boardings behind it are read.
        /// </remarks>
        /// <param name="routeId">The route the page is narrowed to, if any.</param>
        /// <param name="chart">Whether to include the hourly chart.</param>
        /// <param name="fresh">
        /// Read now rather than share what another viewer was just given, for a refresh asked
        /// for by hand.
        /// </param>
        [HttpGet]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> Stats(int? routeId, bool chart = true, bool fresh = false)
        {
            var vm = await BuildAsync(routeId, chart, fresh);
            return Json(new
            {
                activeTrips = vm.ActiveTrips,
                flaggedVehicles = vm.FlaggedVehicles,
                totalPassengers = vm.TotalPassengers,
                passengerDelta = vm.PassengerDelta,
                totalRevenue = vm.TotalRevenue,
                revenueDelta = vm.RevenueDelta,
                chartData = chart ? new { bars = vm.ChartData, usual = vm.ChartUsual, now = vm.ChartNowIndex } : null,
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

        /// <summary>The dashboard for one route, or all of them, from answers shared between viewers.</summary>
        private async Task<DashboardViewModel> BuildAsync(int? routeId, bool withChart = true, bool fresh = false)
        {
            var scope = routeId?.ToString() ?? "all";
            var figures = await SharedRead.GetAsync(_cache, $"dashboard:figures:{scope}", FiguresShared,
                () => ReadFiguresAsync(routeId), fresh);
            var chart = withChart
                ? await SharedRead.GetAsync(_cache, $"dashboard:chart:{figures.Today:yyyy-MM-dd}:{scope}", ChartShared,
                    () => ReadChartAsync(figures, routeId), fresh)
                : null;

            var cycleStart = HourlyBoardings.CycleStart(figures.Today);
            return new DashboardViewModel
            {
                ActiveTrips = figures.ActiveTrips,
                FlaggedVehicles = figures.FlaggedVehicles,
                TotalPassengers = figures.TodayPassengers,
                PassengerDelta = figures.TodayPassengers - figures.YesterdayPassengers,
                TotalRevenue = figures.TodayRevenue,
                RevenueDelta = figures.TodayRevenue - figures.YesterdayRevenue,
                ChartLabels = Enumerable.Range(0, HourlyBoardings.Hours)
                    .Select(h => HourLabel(cycleStart.AddHours(h)))
                    .ToList(),
                ChartData = chart?.Bars.ToList() ?? new List<int?>(),
                ChartUsual = chart?.Usual?.ToList(),
                ChartNowIndex = chart?.NowIndex ?? (int)Math.Floor((PhClock.Now - cycleStart).TotalHours),
                Routes = figures.Routes
                    .Select(r => new SelectListItem
                    {
                        Value = r.RouteId.ToString(),
                        Text = r.RouteName,
                        Selected = routeId.HasValue && r.RouteId == routeId.Value
                    })
                    .ToList(),
                SelectedRouteId = routeId,
                Today = figures.Today,
                ActiveTripBreakdown = figures.Breakdown.ToList(),
            };
        }

        /// <summary>The cards' figures and the trips behind them, read from the database.</summary>
        private async Task<Figures> ReadFiguresAsync(int? routeId)
        {
            // The service day is the current operational cycle, 06:00 to 05:59 the next
            // morning, rather than the calendar day. A trip is dated by the day it starts,
            // so a cycle's trips are exactly those dated today.
            var today = PhClock.OperationalDay;

            // Every row the cards are worked out from, in one request, since the API logs each
            // request it answers and the page asks every few seconds. dashboard_figures holds
            // the open maintenance logs, the trip columns the figures use for today and
            // yesterday, and each route's name without its path for the map.
            var response = await _supabase.Rpc("dashboard_figures", new Dictionary<string, object?>
            {
                ["p_today"] = today.ToString("yyyy-MM-dd"),
            });
            var read = ReadBundle.Parse(response.Content);

            // Flagged vehicles, which the page filters do not affect, are buses with an
            // unresolved maintenance log. Counting the vehicle_status column instead reads
            // zero, because the next shift overwrites it. This matches how the dispatch
            // board and the vehicle registry define the same figure. Only open logs are read,
            // since the table keeps every log ever raised and grows with the fleet's age.
            int flaggedVehicles = read.Rows<MaintenanceLog>("open_logs")
                .Where(l => l.VehicleId != null)
                .Select(l => l.VehicleId)
                .Distinct()
                .Count();

            var todayTripRows = read.Rows<Trip>("today_trips");
            var yesterdayTripRows = read.Rows<Trip>("yesterday_trips");

            // Trips dated today already cover the whole cycle, since a night shift carries
            // its start day's date. Any trip dated yesterday that is still active is folded
            // in as well, so an overnight run that has not been ended does not disappear
            // when the cycle rolls over.
            var todayTrips = todayTripRows
                .Concat(yesterdayTripRows.Where(t => t.TripStatus == "Active"))
                .Where(t => !routeId.HasValue || t.RouteId == routeId.Value)
                .GroupBy(t => t.TripId).Select(g => g.First())   // de-dupe
                .ToList();

            var yesterdayTrips = yesterdayTripRows
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

            // Routes dropdown, in name order.
            var routeRows = read.Rows<BusRoute>("routes");

            // Passenger breakdown across every trip this cycle, for the totals modal.
            var routeNames = routeRows.ToDictionary(r => r.RouteId, r => r.RouteName);
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

            return new Figures(today, todayTrips, activeTrips, flaggedVehicles,
                todayPassengers, yesterdayPassengers, todayRevenue, yesterdayRevenue,
                routeRows, tripBreakdown);
        }

        /// <summary>
        /// Boardings hour by hour across the service day, 06:00 to 05:59, with the usual day
        /// beside them: the same hours on the same weekday in the weeks before.
        /// </summary>
        private async Task<HourlyChart> ReadChartAsync(Figures figures, int? routeId)
        {
            var now = PhClock.Now;
            var hourly = HourlyBoardings.ByHour(figures.Today, figures.TodayTrips,
                await BoardedHoursAsync(figures.TodayTrips), now);
            var usual = await UsualAsync(figures.Today, routeId);
            var nowIndex = (int)Math.Floor((now - HourlyBoardings.CycleStart(figures.Today)).TotalHours);
            return new HourlyChart(hourly, usual, nowIndex);
        }

        private static string HourLabel(DateTime at) => at.Hour switch
        {
            0 => "12:00 AM",
            12 => "12:00 PM",
            < 12 => $"{at.Hour}:00 AM",
            _ => $"{at.Hour - 12}:00 PM",
        };

        /// <summary>The boardings of one day's trips, counted by trip and hour.</summary>
        /// <remarks>
        /// <para>Counted in the database, which answers a full day with a few hundred small rows
        /// however many passengers rode, where reading the events themselves meant one row per
        /// passenger on every refresh. The trip identifiers travel in the request body, so a
        /// day's worth is never too long to send.</para>
        ///
        /// <para>The answer is one read, which the API caps at a thousand rows. A day's trips
        /// board in eight or nine hours each, so a day would need more than a hundred trips to
        /// reach it; a single day is asked for at a time to keep it that way.</para>
        /// </remarks>
        private async Task<List<BoardedHour>> BoardedHoursAsync(IEnumerable<Trip> trips)
        {
            var ids = trips.Select(t => t.TripId).Distinct().ToList();
            if (ids.Count == 0) return new List<BoardedHour>();

            var response = await _supabase.Rpc("boardings_by_hour", new Dictionary<string, object?>
            {
                ["p_trip_ids"] = ids,
            });
            return HourlyBoardings.ParseHours(response.Content);
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
                .Select(TripColumns)
                .Filter("date", Postgrest.Constants.Operator.In, days.Select(d => (object)d.ToString("yyyy-MM-dd")).ToList())
                .Order("trip_id", Postgrest.Constants.Ordering.Ascending));

            var perDay = new List<int?[]>();
            foreach (var day in days)
            {
                var dayTrips = trips
                    .Where(t => t.Date.Date == day && (!routeId.HasValue || t.RouteId == routeId.Value))
                    .ToList();
                var boarded = await BoardedHoursAsync(dayTrips);
                perDay.Add(HourlyBoardings.ByHour(day, dayTrips, boarded, HourlyBoardings.CycleStart(day).AddDays(2)));
            }

            var usual = HourlyBoardings.Usual(perDay);
            _cache.Set(key, usual, UsualFreshness);
            return usual;
        }
    }
}
