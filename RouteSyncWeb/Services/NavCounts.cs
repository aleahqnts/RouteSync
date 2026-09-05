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
    public sealed record NavBadge(int Count, bool Urgent)
    {
        public static readonly NavBadge None = new(0, false);
    }

    /// <summary>Every badge the sidebar can draw.</summary>
    public sealed record NavBadges(NavBadge Dispatch, NavBadge Requests, NavBadge Vehicles)
    {
        public static readonly NavBadges Empty =
            new(NavBadge.None, NavBadge.None, NavBadge.None);
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

        private const string Key = "nav_badges";

        /// <summary>
        /// How long a count stands before it is worked out again.
        /// </summary>
        /// <remarks>
        /// Shorter than the rail's own poll, so a badge is never older than a poll and a
        /// tick, and long enough that a room of dispatchers costs the database the same as
        /// one of them.
        /// </remarks>
        private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(15);

        /// <summary>
        /// How close a shift has to be before a request against it is urgent.
        /// </summary>
        /// <remarks>
        /// Longer than the two hours a driver is asked to give, because two hours is the
        /// notice a driver owes, not the time a dispatcher needs to find somebody else,
        /// reach them and get them to the terminal.
        /// </remarks>
        public static readonly TimeSpan UrgentWithin = TimeSpan.FromHours(4);

        public NavCounts(Supabase.Client supabase, IMemoryCache cache)
        {
            _supabase = supabase;
            _cache = cache;
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
                .Filter("date", Operator.GreaterThanOrEqual, today.ToString("yyyy-MM-dd"))
                .Filter("date", Operator.LessThanOrEqual, today.AddDays(1).ToString("yyyy-MM-dd"))
                .Get();

            // Both of these are one row per bus and one per driver, so the whole of each is
            // the size of the fleet and the roster.
            var vehiclesTask = _supabase.From<Vehicle>().Get();
            var availabilityTask = _supabase.From<DriverAvailability>().Get();

            // The two tables below are not like that. They keep every incident ever raised
            // and every request ever filed, so they grow with the age of the fleet, and
            // reading either of them whole to work out a number about today would cost more
            // every month it ran. Each is asked only for the rows it is counting.

            // A resolved incident is a record rather than a job.
            var maintTask = _supabase.From<MaintenanceLog>()
                .Filter<object>("resolved_at", Operator.Is, null)
                .Get();

            // Requests still waiting on an answer. Not bounded by date: one needs answering
            // whatever days it names, and how many are waiting is the badge.
            var leaveOpenTask = _supabase.From<LeaveRequest>()
                .Filter("status", Operator.In, LeaveEntitlement.OpenStatuses.Cast<object>().ToList())
                .Get();

            // Granted leave a driver has asked to hand back and has not been answered on.
            // Asked for separately because the row itself is Approved: what is open about it
            // is the asking, and no filter on the status would find it.
            var leaveAskedTask = _supabase.From<LeaveRequest>()
                .Filter<object>("withdraw_requested_at", Operator.Not, null)
                .Filter<object>("withdraw_answered_at", Operator.Is, null)
                .Get();

            // Leave that takes a driver off today, which is what makes one of today's trips
            // unrunnable. A day either side is not wanted: the board is one operational day.
            var leaveTodayTask = _supabase.From<LeaveRequest>()
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
                new NavBadge(flagged.Count, false));
        }
    }
}
