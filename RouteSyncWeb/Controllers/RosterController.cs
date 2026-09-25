using System.Globalization;
using FleetWise.Models;
using FleetWise.Services;
using FleetWise.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static Postgrest.Constants;

namespace FleetWise.Controllers
{
    /// <summary>
    /// The standing monthly roster: each bus's crew, each route's floaters, and every rest day.
    /// </summary>
    /// <remarks>
    /// <para>Built by hand, or by auto-fill for a person to review, and carried from month
    /// to month, rotating only the shift. It writes no trips. What it holds is expanded into
    /// the month's trips when the roster is published.</para>
    ///
    /// <para>Gated on its own permission. Deciding who drives which bus for a month is a
    /// different job from running the day's dispatch, which keeps the routes permission.</para>
    ///
    /// <para>Every judgement about the roster, from what blocks a save to how the rest
    /// days cover, is made by <see cref="RosterRules"/>. The page asks as it is edited and
    /// the save asks again, so the two cannot disagree.</para>
    /// </remarks>
    [Authorize]
    [RequirePermission("roster")]
    public partial class RosterController : Controller
    {
        private const int DriverRoleId = 2;

        private readonly Supabase.Client _supabase;
        private readonly AuditLog _audit;
        private readonly SchedulingData _scheduling;
        private readonly TripAssignments _assignments;
        private readonly RosterPublisher _publisher;

        public RosterController(
            Supabase.Client supabase, AuditLog audit, SchedulingData scheduling,
            TripAssignments assignments, RosterPublisher publisher)
        {
            _supabase = supabase;
            _audit = audit;
            _scheduling = scheduling;
            _assignments = assignments;
            _publisher = publisher;
        }

        public async Task<IActionResult> Index(string? month)
        {
            var thisMonth = FirstOf(PhClock.OperationalDay);

            DateTime shown;
            if (TryParseMonth(month, out var asked))
            {
                shown = asked;
            }
            else
            {
                // The month being planned, unless this one already has a roster to look at.
                var current = await _publisher.ReadMonthAsync(thisMonth);
                shown = current is null ? thisMonth.AddMonths(1) : thisMonth;
            }

            var key = shown.ToString("yyyy-MM-dd");
            var monthTask = _supabase.From<RosterMonth>().Filter("month", Operator.Equals, key).Get();
            var slotsTask = _supabase.From<RosterSlot>().Filter("month", Operator.Equals, key).Get();
            var routesTask = _supabase.From<BusRoute>().Get();
            var vehiclesTask = _supabase.From<Vehicle>().Get();
            var driversTask = _supabase.From<UserModel>().Filter("role_id", Operator.Equals, DriverRoleId.ToString()).Get();
            var holdsTask = _publisher.ReadHoldsAsync(shown);

            await Task.WhenAll(monthTask, slotsTask, routesTask, vehiclesTask, driversTask, holdsTask);

            var roster = monthTask.Result.Models.FirstOrDefault();
            var slots = slotsTask.Result.Models;

            // What auto-fill chose is marked until the roster is published: publishing is the
            // choices being accepted. Saved again after a publish, the new marks show again.
            var showSuggested = roster is null || roster.Status != "Published"
                || (roster.SavedAt is DateTime savedAt && roster.PublishedAt is DateTime publishedAt && savedAt > publishedAt);
            var vehicles = vehiclesTask.Result.Models;
            var drivers = driversTask.Result.Models;
            var routes = routesTask.Result.Models.OrderBy(r => r.RouteId).ToList();

            var vm = new RosterViewModel
            {
                Month = shown,
                MonthKey = shown.ToString("yyyy-MM"),
                PrevMonthKey = shown.AddMonths(-1).ToString("yyyy-MM"),
                NextMonthKey = shown.AddMonths(1).ToString("yyyy-MM"),
                Status = roster?.Status ?? "Not started",
                Version = roster?.Version ?? 0,
                ReadOnly = shown < thisMonth,
                SavedLine = roster?.SavedAt is DateTime at
                    // A true instant, read back in the machine's own zone.
                    ? "Saved " + PhClock.ToPh(new DateTimeOffset(at)).ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture)
                        + (roster.SavedBy is int by ? $" by {await NameOfAsync(by)}" : "")
                    : null,
                Seats = slots.Select(s => new RosterSeatInput
                {
                    DriverId = s.DriverId,
                    Kind = s.Kind,
                    RouteId = s.RouteId,
                    VehicleId = s.VehicleId,
                    Shift = s.Shift,
                    RestWeekday = s.RestWeekday,
                    Suggested = showSuggested && !string.IsNullOrWhiteSpace(s.Suggested) ? s.Suggested : null,
                }).ToList(),
                UnroutedBuses = vehicles
                    .Where(v => v.RetiredAt == null && v.RouteId is null)
                    .Select(v => v.VehicleId).OrderBy(v => v, StringComparer.Ordinal).ToList(),
                Held = holdsTask.Result.OrderBy(id => id).ToList(),
            };

            foreach (var route in routes)
            {
                var view = new RosterRouteView { RouteId = route.RouteId, RouteName = route.RouteName };

                // The route's own buses, and any bus the roster still holds for it that has
                // since retired or moved, so a crew is never silently dropped from the page.
                var held = slots.Where(s => s.RouteId == route.RouteId && s.VehicleId != null)
                                .Select(s => s.VehicleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var bus in vehicles
                    .Where(v => (v.RouteId == route.RouteId && v.RetiredAt == null) || held.Contains(v.VehicleId))
                    .OrderBy(v => v.VehicleId, StringComparer.Ordinal))
                {
                    view.Buses.Add(new RosterBusView
                    {
                        VehicleId = bus.VehicleId,
                        PlateNumber = bus.PlateNumber ?? "",
                        Warning = bus.RetiredAt != null ? "Retired. Move this crew to another bus."
                                : bus.RouteId != route.RouteId ? "No longer based on this route. Move this crew."
                                : null,
                    });
                }

                vm.Routes.Add(view);
            }

            // Active drivers, and anyone the roster still names who no longer is, so the
            // stored choice shows and the page can say what is wrong with it.
            var named = slots.Select(s => s.DriverId).ToHashSet();
            var listed = drivers
                .Where(d => IsActive(d) || named.Contains(d.UserId))
                .OrderBy(d => $"{d.FirstName} {d.LastName}".Trim(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.UserId)
                .ToList();
            var shared = listed.GroupBy(d => $"{d.FirstName} {d.LastName}".Trim(), StringComparer.OrdinalIgnoreCase)
                               .Where(g => g.Count() > 1).Select(g => g.Key)
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);

            vm.Drivers = listed.Select(d =>
            {
                var name = $"{d.FirstName} {d.LastName}".Trim();
                if (name.Length == 0) name = $"Driver {d.UserId}";
                var label = shared.Contains(name) ? $"{name} ({d.UserId})" : name;
                return new RosterDriverOption
                {
                    DriverId = d.UserId,
                    Label = IsActive(d) ? label : $"{label}, inactive",
                    Active = IsActive(d),
                };
            }).ToList();

            await FillPublishStateAsync(vm, roster, routes);

            return View(vm);
        }

        /// <summary>
        /// What is wrong with the roster as it stands on the page, how each route's rest
        /// days cover, what publishing it now would leave empty, and who has no place.
        /// </summary>
        /// <remarks>
        /// Two questions, answered by the same planner a publish uses. Whether the pattern of
        /// shifts and rest days works at all is asked of a clean month, so it names what moving
        /// a rest day would fix and does not move when somebody files leave. What a publish
        /// would actually leave is asked of the real month, leave and all, so the page never
        /// says fine about a roster the publish then leaves short.
        /// </remarks>
        [HttpPost]
        public async Task<IActionResult> Check([FromBody] RosterCheckInput req)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState.FirstError());

            var (routes, vehicles, drivers) = await _publisher.ReadFleetAsync();
            var seats = ToSeats(req.Seats);
            var held = RosterPublisher.LiveHolds(req.Held, seats);
            var weekly = RosterStructure.WeeklyGaps(seats, drivers, vehicles, routes);

            // A month already over is a record, and there is nothing left to publish into it.
            IReadOnlyList<PlannedGap> wouldLeave = Array.Empty<PlannedGap>();
            if (TryParseMonth(req.Month, out var month) && month >= FirstOf(PhClock.OperationalDay))
                wouldLeave = (await _publisher.PlanDraftAsync(month, seats, routes, vehicles, drivers)).Gaps;

            return Json(new
            {
                problems = RosterRules.Problems(seats, vehicles, drivers, routes),
                routes = routes.Keys.OrderBy(id => id).Select(id =>
                {
                    var shortfall = RosterRules.Shortfall(id, seats, weekly);
                    return new
                    {
                        routeId = id,
                        strip = RosterRules.Capacity(id, seats, weekly).Select(d => new
                        {
                            day = RosterRules.DayName(d.Weekday)[..3],
                            dayName = RosterRules.DayName(d.Weekday),
                            resting = d.Resting,
                            covering = d.Covering,
                            @short = d.Short,
                        }),
                        crewSeats = shortfall.CrewSeats,
                        unfilledSeats = shortfall.UnfilledSeats,
                        floaters = shortfall.Floaters,
                        floatersNeeded = shortfall.FloatersNeeded,
                        floatersMissing = shortfall.FloatersMissing,
                        driversShort = shortfall.DriversShort,
                        gapsPerWeek = shortfall.GapsPerWeek,
                        restDaysCollide = shortfall.RestDaysCollide,
                        uncovered = weekly.Where(g => g.RouteId == id && g.RestDay).Select(g => new
                        {
                            dayName = RosterRules.DayName(g.Weekday),
                            vehicleId = g.VehicleId,
                            shift = g.Shift,
                        }),
                    };
                }),
                // One line per bus shift and reason, with the dates it falls on, so a rest day
                // colliding every week reads apart from a run of leave.
                wouldLeave = wouldLeave
                    .GroupBy(g => (Bus: g.VehicleId.ToUpperInvariant(), g.Shift, g.Reason))
                    .OrderBy(g => g.Min(x => x.Date))
                    .ThenBy(g => g.Key.Bus, StringComparer.Ordinal)
                    .Select(g => new
                    {
                        vehicleId = g.First().VehicleId,
                        shift = g.Key.Shift,
                        routeName = routes.GetValueOrDefault(g.First().RouteId) ?? "",
                        reason = g.Key.Reason,
                        dates = g.OrderBy(x => x.Date).Select(x => x.Date.ToString("MMM d", CultureInfo.InvariantCulture)),
                    }),
                unplaced = RosterRules.Unplaced(seats, drivers).Select(d => new
                {
                    driverId = d.UserId,
                    name = $"{d.FirstName} {d.LastName}".Trim(),
                    held = held.Contains(d.UserId),
                }),
            });
        }

        /// <summary>
        /// The roster on the page with drivers and buses that can no longer be on it cleared,
        /// and its empty places filled by rule. Nothing is saved.
        /// </summary>
        /// <remarks>See <see cref="RosterRules.AutoFill"/> for the rules and their order.</remarks>
        [HttpPost]
        public async Task<IActionResult> AutoFill([FromBody] RosterCheckInput req)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState.FirstError());

            var seats = ToSeats(req.Seats);
            var fill = await _publisher.AutoFillAsync(seats, RosterPublisher.LiveHolds(req.Held, seats));

            return Json(new
            {
                seats = fill.Seats.Select(s => new
                {
                    driverId = s.DriverId,
                    kind = s.Kind,
                    routeId = s.RouteId,
                    vehicleId = s.VehicleId,
                    shift = s.Shift,
                    restWeekday = s.RestWeekday,
                    suggested = s.Suggested,
                    aboutDriver = RosterRules.MarkIsAboutDriver(s.Suggested),
                }),
                notes = fill.Notes,
                unmarked = fill.Unmarked,
                changed = fill.Changed,
            });
        }

        /// <summary>
        /// The roster with its rest days arranged so the floaters can cover them, disturbing as
        /// few people as possible. Nothing is saved.
        /// </summary>
        /// <remarks>See <see cref="RosterRules.SuggestRestDays"/> for the order fixes are tried in.</remarks>
        [HttpPost]
        public async Task<IActionResult> Suggest([FromBody] RosterCheckInput req)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState.FirstError());

            var (routes, vehicles, drivers) = await _publisher.ReadFleetAsync();
            var seats = ToSeats(req.Seats);
            var fix = RosterRules.SuggestRestDays(seats, vehicles, drivers, routes, RosterPublisher.LiveHolds(req.Held, seats));

            return Json(new
            {
                seats = fix.Seats.Select(s => new
                {
                    driverId = s.DriverId,
                    kind = s.Kind,
                    routeId = s.RouteId,
                    vehicleId = s.VehicleId,
                    shift = s.Shift,
                    restWeekday = s.RestWeekday,
                    suggested = s.Suggested,
                    aboutDriver = RosterRules.MarkIsAboutDriver(s.Suggested),
                }),
                notes = fix.Notes,
                unmarked = fix.Unmarked,
                changed = fix.Changed,
                moved = fix.Moved,
                added = fix.Added,
                gapsLeft = fix.GapsLeft,
            });
        }

        /// <summary>Saves the month's roster whole, if nothing blocks it and nobody saved it first.</summary>
        /// <remarks>
        /// The version the page was built on goes to the database with the roster, and the
        /// check and the write happen under one lock there. A page left open while someone
        /// else saved is refused rather than written over their work, the same way the
        /// planner refuses a stale week.
        /// </remarks>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save([FromBody] RosterSaveInput req)
        {
            if (!ModelState.IsValid) return BadRequest(new { problems = new[] { ModelState.FirstError() } });

            if (!TryParseMonth(req.Month, out var month))
                return BadRequest(new { problems = new[] { "That is not a month." } });

            if (month < FirstOf(PhClock.OperationalDay))
                return BadRequest(new { problems = new[] { "That month is over, so its roster can no longer be changed." } });

            var (routes, vehicles, drivers) = await _publisher.ReadFleetAsync();
            var seats = ToSeats(req.Seats);

            var problems = RosterRules.Problems(seats, vehicles, drivers, routes);
            if (problems.Count > 0) return BadRequest(new { problems });

            var savedBy = SenderId();
            var held = RosterPublisher.LiveHolds(req.Held, seats);
            var saved = await _publisher.SaveAsync(month, req.Version, seats, held, savedBy, "roster_saved");
            if (saved.Step != RosterStep.Done) return Answer(saved, _ => Ok());
            var version = saved.Version;

            var crew = seats.Count(s => s.Kind == RosterRules.Crew);
            var floaters = seats.Count(s => s.Kind == RosterRules.Floater);

            await _audit.WriteAsync("roster_saved",
                $"saved the {month:MMMM yyyy} roster: {crew} crew {(crew == 1 ? "seat" : "seats")}, "
                    + $"{floaters} {(floaters == 1 ? "floater" : "floaters")}"
                    + (held.Count > 0 ? $", {held.Count} {(held.Count == 1 ? "driver" : "drivers")} held back" : ""),
                "roster_months", month.ToString("yyyy-MM-dd"));

            return Ok(new
            {
                version,
                savedLine = "Saved " + PhClock.Now.ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture) + (savedBy is int by ? $" by {await NameOfAsync(by)}" : ""),
            });
        }

        private async Task<string> NameOfAsync(int userId)
        {
            var user = (await _supabase.From<UserModel>()
                .Filter("user_id", Operator.Equals, userId.ToString())
                .Get()).Models.FirstOrDefault();
            var name = user is null ? "" : $"{user.FirstName} {user.LastName}".Trim();
            return name.Length == 0 ? $"user {userId}" : name;
        }

        private static List<RosterSeat> ToSeats(IEnumerable<RosterSeatInput> seats) =>
            seats.Select(s => new RosterSeat(
                s.DriverId, s.Kind, s.RouteId,
                string.IsNullOrWhiteSpace(s.VehicleId) || s.Kind == RosterRules.Floater ? null : s.VehicleId.Trim(),
                s.Shift,
                // A place with nobody on it has no rest day, whatever the page still had selected.
                s.DriverId is null ? null : s.RestWeekday,
                string.IsNullOrWhiteSpace(s.Suggested) ? null : s.Suggested.Trim())).ToList();

        private int? SenderId() =>
            int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

        private static bool IsActive(UserModel d) =>
            string.Equals(d.AccountStatus, "Activated", StringComparison.OrdinalIgnoreCase);

        private static DateTime FirstOf(DateTime day) => RosterPublisher.FirstOf(day);

        private static bool TryParseMonth(string? value, out DateTime month) =>
            DateTime.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out month);
    }
}
