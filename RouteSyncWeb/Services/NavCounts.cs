using FleetWise.Models;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using static Postgrest.Constants;

namespace FleetWise.Services
{
    /// <summary>What one tab is carrying, and how loudly it should say so.</summary>
    /// <param name="Count">How many things are waiting. Nothing is drawn at zero.</param>
    /// <param name="Urgent">
    /// Whether any one of them cannot wait for somebody to open the tab.
    /// </param>
    /// <remarks>
    /// Severity belongs to the thing, not to the tab it lives under. A tab takes the
    /// highest severity of anything it holds, so one urgent request among five ordinary
    /// ones makes the Requests badge urgent and the other four do not quieten it.
    /// </remarks>
    /// <param name="Note">What the count is about, for a tab whose number alone does not say.</param>
    public sealed record NavBadge(int Count, bool Urgent, string? Note = null)
    {
        public static readonly NavBadge None = new(0, false);
    }

    /// <summary>Every badge the sidebar can draw.</summary>
    public sealed record NavBadges(NavBadge Dispatch, NavBadge Requests, NavBadge Vehicles, NavBadge Roster)
    {
        public static readonly NavBadges Empty =
            new(NavBadge.None, NavBadge.None, NavBadge.None, NavBadge.None);
    }

    /// <summary>
    /// What needs a dispatcher's attention, counted for the navigation rail.
    /// </summary>
    /// <remarks>
    /// The tabs already know these numbers; the point of counting them here is that a
    /// dispatcher on the dashboard should not have to open Requests to learn there is a
    /// sick call against tonight's shift.
    ///
    /// Counted for the fleet rather than per signed-in user, so one cache entry serves
    /// everybody. What each of them is allowed to see is decided where the badges are
    /// drawn, not here.
    ///
    /// Only what is actionable is counted. Users has nothing but activated and
    /// deactivated accounts, and the dashboard, the map, reports and the audit trail are
    /// all read.
    /// </remarks>
    /// <summary>Drops the standing count after anything is written.</summary>
    /// <remarks>
    /// Registered over every controller rather than at the handful of places that change a
    /// count today, because the list of those places is not stable and a badge that goes
    /// quietly stale is worse than one that is worked out more often than it needs to be.
    ///
    /// Only writes, and only writes that were accepted. A read cannot change a count, and a
    /// request that was refused did not change one either.
    ///
    /// The cost of being wrong is one recount on the next reading of the rail, which is a
    /// handful of queries at human pace. The cost of being right is a badge that answers for
    /// what has just been done.
    /// </remarks>
    public sealed class NavCountsFreshener : IActionFilter
    {
        private readonly NavCounts _counts;

        public NavCountsFreshener(NavCounts counts) => _counts = counts;

        public void OnActionExecuting(ActionExecutingContext context) { }

        public void OnActionExecuted(ActionExecutedContext context)
        {
            if (HttpMethods.IsGet(context.HttpContext.Request.Method)
                || HttpMethods.IsHead(context.HttpContext.Request.Method))
            {
                return;
            }

            if (context.Exception is not null) return;

            var status = (context.Result as IStatusCodeActionResult)?.StatusCode ?? 200;
            if (status >= 400) return;

            _counts.Invalidate();
        }
    }

    public sealed class NavCounts
    {
        private readonly Supabase.Client _supabase;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _config;

        private const string Key = "nav_badges";

        /// <summary>
        /// How long a count stands before it is worked out again.
        /// </summary>
        /// <remarks>
        /// Matched to the rail's own poll, so a badge is never older than a poll and a
        /// tick. A room of dispatchers still costs the database the same as one of them,
        /// because the count is worked out once and served to all of them: what sets the
        /// cost is how often it expires, not how many people ask.
        ///
        /// This is the only thing watching for work filed somewhere the dashboard cannot
        /// see. A driver's leave request arrives without any page here being told, so the
        /// rail is what notices, whichever page its reader happens to be on.
        /// </remarks>
        private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How close a shift has to be before a request against it is urgent.
        /// </summary>
        /// <remarks>
        /// Longer than the two hours a driver is asked to give, because two hours is the
        /// notice a driver owes, not the time a dispatcher needs to find somebody else,
        /// reach them and get them to the terminal.
        /// </remarks>
        public static readonly TimeSpan UrgentWithin = TimeSpan.FromHours(4);

        public NavCounts(Supabase.Client supabase, IMemoryCache cache, IConfiguration config)
        {
            _supabase = supabase;
            _cache = cache;
            _config = config;
        }

        /// <summary>Drops the standing count, so the next reading is worked out again.</summary>
        /// <remarks>
        /// Called whenever something is written that could change what is being counted.
        /// Without it the dispatcher who has just cleared an incident asks for the count,
        /// is served the reading taken before they cleared it, and watches the badge sit
        /// there saying the work is still waiting.
        ///
        /// Dropped rather than recomputed. Nothing is owed a count until somebody asks for
        /// one, and a write that changes nothing anybody is looking at should not pay for a
        /// round of queries.
        /// </remarks>
        public void Invalidate() => _cache.Remove(Key);

        public async Task<NavBadges> ReadAsync()
        {
            if (_cache.TryGetValue<NavBadges>(Key, out var cached) && cached is not null)
                return cached;

            NavBadges badges;
            try
            {
                badges = await CountAsync();
            }
            catch
            {
                // A rail that cannot count says nothing rather than saying zero. Zero is a
                // claim that there is nothing to do, and it would be a false one.
                return NavBadges.Empty;
            }

            _cache.Set(Key, badges, Freshness);
            return badges;
        }

        private async Task<NavBadges> CountAsync()
        {
            var today = PhClock.OperationalDay;
            var now = PhClock.Now;

            // Today and tomorrow. A shift more than a day out cannot be inside the urgent
            // window, and the board itself only ever covers one operational day.
            var tripsTask = _supabase.From<Trip>()
                .Select("trip_id,date,vehicle_id,driver_id,trip_status,shift_start_time,shift_end_time")
                .Filter("date", Operator.GreaterThanOrEqual, today.ToString("yyyy-MM-dd"))
                .Filter("date", Operator.LessThanOrEqual, today.AddDays(1).ToString("yyyy-MM-dd"))
                .Get();

            // Both of these are one row per bus and one per driver, so the whole of each is
            // the size of the fleet and the roster.
            //
            // Every query here names its columns. Counting reads a handful of fields and
            // throws the rest of each row away, and the rail is drawn on every page and
            // read again every few seconds, so whatever travels does so all day. Naming
            // them also keeps things off the wire that have no business on it: the whole
            // of a user row carries a password hash nothing here has any use for.
            var vehiclesTask = _supabase.From<Vehicle>()
                .Select("vehicle_id,out_of_service,retired_at")
                .Get();
            var availabilityTask = _supabase.From<DriverAvailability>()
                .Select("user_id,availability_status")
                .Get();

            // The two tables below are not like that. They keep every incident ever raised
            // and every request ever filed, so they grow with the age of the fleet, and
            // reading either of them whole to work out a number about today would cost more
            // every month it ran. Each is asked only for the rows it is counting.

            // A resolved incident is a record rather than a job.
            var maintTask = _supabase.From<MaintenanceLog>()
                .Select("log_id,vehicle_id")
                .Filter<object>("resolved_at", Operator.Is, null)
                .Get();

            // Requests still waiting on an answer. Not bounded by date: one needs answering
            // whatever days it names, and how many are waiting is the badge.
            var leaveOpenTask = _supabase.From<LeaveRequest>()
                .Select("request_id,user_id,status,leave_type,start_date,end_date,revoked_dates")
                .Filter("status", Operator.In, LeaveEntitlement.OpenStatuses.Cast<object>().ToList())
                .Get();

            // Granted leave a driver has asked to hand back and has not been answered on.
            // Asked for separately because the row itself is Approved: what is open about it
            // is the asking, and no filter on the status would find it. Leave revoked outright
            // is left out: nothing is left to cancel, so its asking needs no answer.
            var leaveAskedTask = _supabase.From<LeaveRequest>()
                .Select("request_id")
                .Filter<object>("withdraw_requested_at", Operator.Not, null)
                .Filter<object>("withdraw_answered_at", Operator.Is, null)
                .Filter("status", Operator.Equals, "Approved")
                .Get();

            // Leave that takes a driver off today, which is what makes one of today's trips
            // unrunnable. A day either side is not wanted: the board is one operational day.
            var leaveTodayTask = _supabase.From<LeaveRequest>()
                .Select("request_id,user_id,status,start_date,end_date,revoked_dates")
                .Filter("status", Operator.Equals, "Approved")
                .Filter("start_date", Operator.LessThanOrEqual, today.ToString("yyyy-MM-dd"))
                .Filter("end_date", Operator.GreaterThanOrEqual, today.ToString("yyyy-MM-dd"))
                .Get();

            await Task.WhenAll(tripsTask, vehiclesTask, availabilityTask, maintTask,
                               leaveOpenTask, leaveAskedTask, leaveTodayTask);

            var trips = tripsTask.Result.Models;
            var vehicles = vehiclesTask.Result.Models;

            var todayTrips = trips.Where(t => t.Date.Date == today).ToList();

            var retired = vehicles
                .Where(v => v.RetiredAt != null)
                .Select(v => v.VehicleId)
                .ToHashSet();

            // Not narrowed to buses still in the fleet, because the board is not. A trip
            // holding a bus that has been retired and grounded is an assignment issue on
            // the board and has to be one here, or the badge sends a dispatcher to a board
            // showing something the badge did not count.
            var grounded = vehicles
                .Where(v => v.OutOfService)
                .Select(v => v.VehicleId)
                .ToHashSet();

            var cannotDrive = availabilityTask.Result.Models
                .Where(a => string.Equals(a.AvailabilityStatus, "Unavailable",
                                          StringComparison.OrdinalIgnoreCase))
                .Select(a => a.UserId)
                .ToHashSet();

            // Asked again of each row rather than left to the dates the query matched on,
            // because a day inside an approved span can have been handed back since.
            var offToday = leaveTodayTask.Result.Models
                .Where(l => LeaveEntitlement.CoversDay(l, today))
                .Select(l => l.UserId)
                .ToHashSet();

            // The same two things the board draws in red: a trip that cannot run as
            // assigned, and one that should have gone and has not. Counted together
            // because both mean somebody has to be rung, and both are urgent by nature.
            int dispatch = todayTrips.Count(t =>
                (!TripStatus.Locked(t, now)
                 && (grounded.Contains(t.VehicleId)
                     || cannotDrive.Contains(t.DriverId)
                     || offToday.Contains(t.DriverId)))
                || TripStatus.LateBy(t, now) is not null);

            var waiting = leaveOpenTask.Result.Models;

            // Counted once each. A request can be waiting on an answer and carry an
            // unanswered asking at the same time, and it is one thing on the queue either
            // way.
            var openCount = waiting
                .Select(l => l.RequestId)
                .Concat(leaveAskedTask.Result.Models.Select(l => l.RequestId))
                .Distinct()
                .Count();

            // Measured against the shift rather than against the request. A leave filed
            // for a day already gone has nothing to scramble for; a leave that began last
            // week and covers tonight has a shift tonight, and the shift is what decides.
            //
            // An asking to cancel granted leave is counted and never urgent: it hands a
            // driver back, which is a decision to be made rather than a hole to be filled.
            bool urgent = waiting.Any(l =>
                trips.Any(t =>
                    t.DriverId == l.UserId
                    && t.Date.Date >= l.StartDate.Date
                    && t.Date.Date <= l.EndDate.Date
                    && !LeaveEntitlement.IsRevokedOn(l, t.Date)
                    && !TripStatus.Locked(t, now)
                    && TripStatus.StartOf(t) - now <= UrgentWithin));

            // A bus nobody can send. An open fault or a grounding is the same job either
            // way, and neither stops the day the way a trip that cannot run does.
            var flagged = maintTask.Result.Models
                .Where(l => l.VehicleId != null)
                .Select(l => l.VehicleId)
                .ToHashSet();

            flagged.UnionWith(grounded);

            // A bus that has left the fleet is not a job. It cannot be assigned and cannot
            // be returned to service, and whatever was open against it when it went is a
            // record rather than work. The vehicles page leaves it out of its own flagged
            // and under-repair counts for the same reason, and a badge that disagreed with
            // the page it points at is worse than no badge.
            flagged.ExceptWith(retired);

            return new NavBadges(
                new NavBadge(dispatch, dispatch > 0),
                new NavBadge(openCount, urgent),
                new NavBadge(flagged.Count, false),
                await CountRosterAsync(today));
        }

        /// <summary>
        /// Roster slots nobody is on for the days ahead, a month's roster waiting on a person
        /// once the draft day has come, and places on this month's published roster that no
        /// longer hold: a driver no longer active, or a bus retired or moved.
        /// </summary>
        /// <remarks>
        /// Counted on its own and failing on its own, so a roster table that cannot be read
        /// takes down the roster badge rather than every badge on the rail.
        ///
        /// Urgent when a slot is empty within the next two days: that is a bus with nobody to
        /// drive it, and time is short to find somebody.
        ///
        /// A published month is never auto-filled by itself, since it is already running. Its
        /// broken places are counted instead, so the deactivation is noticed and dealt with on
        /// the Roster page, where Auto-fill and Publish changes move the trips and tell the
        /// drivers.
        /// </remarks>
        private async Task<NavBadge> CountRosterAsync(DateTime today)
        {
            try
            {
                var thisMonth = RosterPublisher.FirstOf(today);
                var nextMonth = thisMonth.AddMonths(1);
                var from = today.AddDays(1).ToString("yyyy-MM-dd");

                var monthsTask = _supabase.From<RosterMonth>()
                    .Select("month,status")
                    .Filter("month", Operator.In, new List<object> { thisMonth.ToString("yyyy-MM-dd"), nextMonth.ToString("yyyy-MM-dd") })
                    .Get();
                var gapsTask = _supabase.From<RosterGap>()
                    .Select("date,vehicle_id,shift")
                    .Filter("date", Operator.GreaterThanOrEqual, from)
                    .Get();
                var skipsTask = _supabase.From<RosterSkip>()
                    .Select("date,vehicle_id,shift")
                    .Filter("date", Operator.GreaterThanOrEqual, from)
                    .Get();
                var slotsTask = _supabase.From<RosterSlot>()
                    .Select("month,driver_id,vehicle_id,route_id,shift,suggested")
                    .Filter("month", Operator.In, new List<object> { thisMonth.ToString("yyyy-MM-dd"), nextMonth.ToString("yyyy-MM-dd") })
                    .Get();

                await Task.WhenAll(monthsTask, gapsTask, skipsTask, slotsTask);

                var gaps = gapsTask.Result.Models;
                var open = new List<RosterGap>();
                if (gaps.Count > 0)
                {
                    var taken = (await _supabase.From<Trip>()
                        .Select("date,vehicle_id,shift_type")
                        .Filter("date", Operator.In, gaps.Select(g => (object)g.Date.ToString("yyyy-MM-dd")).Distinct().ToList())
                        .Get()).Models
                        .Select(t => (t.Date.Date, t.VehicleId.ToUpperInvariant(), t.ShiftType))
                        .Concat(skipsTask.Result.Models.Select(s => (s.Date.Date, s.VehicleId.ToUpperInvariant(), s.Shift)))
                        .ToHashSet();
                    open = gaps.Where(g => !taken.Contains((g.Date.Date, g.VehicleId.ToUpperInvariant(), g.Shift))).ToList();
                }

                var notes = new List<string>();
                if (open.Count > 0)
                    notes.Add(open.Count == 1 ? "1 roster slot has nobody on it" : $"{open.Count} roster slots have nobody on them");

                var slots = slotsTask.Result.Models;
                var current = monthsTask.Result.Models.FirstOrDefault(m => m.Month.Date == thisMonth);
                var broken = 0;
                if (current?.Status == "Published")
                {
                    var live = slots.Where(s => s.Month.Date == thisMonth).ToList();
                    broken = await CountBrokenPlacesAsync(live);
                    if (broken > 0)
                        notes.Add($"The {thisMonth:MMMM} roster has {(broken == 1 ? "1 place" : $"{broken} places")} "
                            + "with a driver no longer active or a bus no longer running. Auto-fill it and publish the changes");
                }

                var next = monthsTask.Result.Models.FirstOrDefault(m => m.Month.Date == nextMonth);
                var waiting = today.Day >= RosterCycleService.DraftDay(_config) && next?.Status != "Published";
                if (waiting)
                {
                    var marked = slots.Count(s => s.Month.Date == nextMonth && !string.IsNullOrWhiteSpace(s.Suggested));
                    notes.Add(next is null ? $"Build the {nextMonth:MMMM} roster"
                        : marked > 0 ? $"{nextMonth:MMMM} roster ready for review: {(marked == 1 ? "1 place" : $"{marked} places")} auto-filled"
                        : $"{nextMonth:MMMM} roster ready for review");
                }

                return new NavBadge(
                    open.Count + broken + (waiting ? 1 : 0),
                    open.Any(g => g.Date.Date <= today.AddDays(2)),
                    notes.Count == 0 ? null : string.Join(". ", notes));
            }
            catch
            {
                return NavBadge.None;
            }
        }

        /// <summary>Places on a roster whose driver is no longer active, or whose bus is retired, unrouted or moved route.</summary>
        private async Task<int> CountBrokenPlacesAsync(IReadOnlyList<RosterSlot> slots)
        {
            if (slots.Count == 0) return 0;

            var driverIds = slots.Where(s => s.DriverId is not null).Select(s => (object)s.DriverId!.Value.ToString()).Distinct().ToList();
            var busIds = slots.Where(s => s.VehicleId != null).Select(s => (object)s.VehicleId).Distinct().ToList();

            var driversTask = driverIds.Count == 0
                ? Task.FromResult(new List<UserModel>())
                : _supabase.From<UserModel>()
                    .Select("user_id,account_status")
                    .Filter("user_id", Operator.In, driverIds).Get().ContinueWith(t => t.Result.Models);
            var busesTask = busIds.Count == 0
                ? Task.FromResult(new List<Vehicle>())
                : _supabase.From<Vehicle>()
                    .Select("vehicle_id,retired_at,route_id")
                    .Filter("vehicle_id", Operator.In, busIds).Get().ContinueWith(t => t.Result.Models);

            await Task.WhenAll(driversTask, busesTask);

            var active = driversTask.Result
                .Where(d => string.Equals(d.AccountStatus, "Activated", StringComparison.OrdinalIgnoreCase))
                .Select(d => d.UserId)
                .ToHashSet();
            var buses = busesTask.Result.ToDictionary(v => v.VehicleId, StringComparer.OrdinalIgnoreCase);

            return slots.Count(s =>
                (s.DriverId is int id && !active.Contains(id))
                || (s.VehicleId != null
                    && (!buses.TryGetValue(s.VehicleId, out var bus) || bus.RetiredAt != null || bus.RouteId != s.RouteId)));
        }
    }
}
