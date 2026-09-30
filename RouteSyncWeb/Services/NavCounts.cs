using FleetWise.Models;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;

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
    public sealed record NavBadges(
        NavBadge Dispatch, NavBadge Requests, NavBadge Vehicles, NavBadge Roster, NavBadge Audit)
    {
        public static readonly NavBadges Empty =
            new(NavBadge.None, NavBadge.None, NavBadge.None, NavBadge.None, NavBadge.None);
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
        private readonly RosterPublisher _roster;

        private const string Key = "nav_badges";

        private const string ProjectedKey = "nav_roster_projected";

        /// <summary>
        /// How long the upcoming roster's projected gaps stand before the plan is run again.
        /// </summary>
        /// <remarks>
        /// Running a month's plan reads six weeks of trips, far more than a badge should cost
        /// every five seconds. It is only run between the draft day and the upcoming month
        /// being published, held this long, and dropped with the rest of the counts whenever
        /// something is written here, so a person who fixes the roster sees the badge follow
        /// at once. What changes elsewhere, a draft made on schedule or leave filed from a
        /// phone, waits at most this long, against a window of days.
        /// </remarks>
        private static readonly TimeSpan ProjectedFreshness = TimeSpan.FromMinutes(30);

        /// <summary>Slots a publish of the month as saved would leave empty.</summary>
        private sealed record Projected(DateTime Month, int Gaps);

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

        public NavCounts(
            Supabase.Client supabase, IMemoryCache cache, IConfiguration config, RosterPublisher roster)
        {
            _supabase = supabase;
            _cache = cache;
            _config = config;
            _roster = roster;
        }

        /// <summary>A security incident's severity, as nav_badge_inputs lists those needing review.</summary>
        private sealed class IncidentRow
        {
            [JsonProperty("severity")]
            public string? Severity { get; set; }
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
        public void Invalidate()
        {
            _cache.Remove(Key);
            _cache.Remove(ProjectedKey);
        }

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

            // Every row the badges are counted from, in one request. The rail is drawn on every
            // page and read again every few seconds, and the API logs each request it answers,
            // so the reads are made together rather than one at a time.
            //
            // Each list is what its own read used to fetch, and nav_badge_inputs says which
            // rows and columns: trips for today and tomorrow, since a shift further out cannot
            // be inside the urgent window; every bus and every driver's availability, the size
            // of the fleet and the roster; and only the open incidents and the leave that is
            // waiting, asked back, or taking somebody off today, since those two tables grow
            // with the age of the fleet. The roster's rows and the security incidents come in
            // sections of their own, read and failing apart from the rest.
            var response = await _supabase.Rpc("nav_badge_inputs", new Dictionary<string, object?>
            {
                ["p_today"] = today.ToString("yyyy-MM-dd"),
                ["p_this_month"] = RosterPublisher.FirstOf(today).ToString("yyyy-MM-dd"),
                ["p_next_month"] = RosterPublisher.FirstOf(today).AddMonths(1).ToString("yyyy-MM-dd"),
                ["p_open_statuses"] = LeaveEntitlement.OpenStatuses.ToList(),
            });
            var read = ReadBundle.Parse(response.Content);

            var trips = read.Rows<Trip>("trips");
            var vehicles = read.Rows<Vehicle>("vehicles");

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

            var cannotDrive = read.Rows<DriverAvailability>("availability")
                .Where(a => string.Equals(a.AvailabilityStatus, "Unavailable",
                                          StringComparison.OrdinalIgnoreCase))
                .Select(a => a.UserId)
                .ToHashSet();

            // Asked again of each row rather than left to the dates the query matched on,
            // because a day inside an approved span can have been handed back since.
            var offToday = read.Rows<LeaveRequest>("leave_today")
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

            var waiting = read.Rows<LeaveRequest>("leave_open");

            // Counted once each. A request can be waiting on an answer and carry an
            // unanswered asking at the same time, and it is one thing on the queue either
            // way.
            var openCount = waiting
                .Select(l => l.RequestId)
                .Concat(read.Rows<LeaveRequest>("leave_asked").Select(l => l.RequestId))
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
            var flagged = read.Rows<MaintenanceLog>("open_logs")
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
                await CountRosterAsync(today, read.Section("roster")),
                CountAudit(read));
        }

        /// <summary>Security incidents waiting for somebody to look at them.</summary>
        /// <remarks>
        /// Urgent only for a sign-in that succeeded after failed attempts: the one incident
        /// that says somebody may already be in rather than that somebody tried. Every
        /// other rule is a grey number, because a rail that shouts about every failed
        /// password soon stops being listened to.
        ///
        /// Counted on its own and failing on its own, like the roster, so a table that
        /// cannot be read takes this badge down and leaves the rest of the rail standing.
        /// </remarks>
        private static NavBadge CountAudit(ReadBundle read)
        {
            if (!read.Has("incidents")) return NavBadge.None;

            var waiting = read.Rows<IncidentRow>("incidents");
            var urgent = waiting.Any(r => r.Severity == "high");
            return new NavBadge(waiting.Count, urgent,
                urgent ? "A sign-in succeeded after failed attempts" : null);
        }

        /// <summary>
        /// Roster slots nobody is on for the days ahead, a month's roster waiting on a person
        /// once the draft day has come, and places on this month's published roster that no
        /// longer hold: a driver no longer active, or a bus retired or moved.
        /// </summary>
        /// <remarks>
        /// Counted on its own and failing on its own, so a roster table that cannot be read
        /// takes down the roster badge rather than every badge on the rail. The database
        /// sends the section as null when it could not read it.
        ///
        /// Urgent when a slot is empty within the next two days: that is a bus with nobody to
        /// drive it, and time is short to find somebody.
        ///
        /// A published month is never auto-filled by itself, since it is already running. Its
        /// broken places are counted instead, so the deactivation is noticed and dealt with on
        /// the Roster page, where Auto-fill and Publish changes move the trips and tell the
        /// drivers.
        /// </remarks>
        private async Task<NavBadge> CountRosterAsync(DateTime today, ReadBundle? roster)
        {
            if (roster is null) return NavBadge.None;

            try
            {
                var thisMonth = RosterPublisher.FirstOf(today);
                var nextMonth = thisMonth.AddMonths(1);

                // Slots nobody is on from tomorrow, less those a trip or a skip already fills.
                var gaps = roster.Rows<RosterGap>("gaps");
                var open = new List<RosterGap>();
                if (gaps.Count > 0)
                {
                    var taken = roster.Rows<Trip>("gap_trips")
                        .Select(t => (t.Date.Date, t.VehicleId.ToUpperInvariant(), t.ShiftType))
                        .Concat(roster.Rows<RosterSkip>("skips").Select(s => (s.Date.Date, s.VehicleId.ToUpperInvariant(), s.Shift)))
                        .ToHashSet();
                    open = gaps.Where(g => !taken.Contains((g.Date.Date, g.VehicleId.ToUpperInvariant(), g.Shift))).ToList();
                }

                var notes = new List<string>();
                if (open.Count > 0)
                    notes.Add(open.Count == 1 ? "1 roster slot has nobody on it" : $"{open.Count} roster slots have nobody on them");

                var slots = roster.Rows<RosterSlot>("slots");
                var months = roster.Rows<RosterMonth>("months");
                var current = months.FirstOrDefault(m => m.Month.Date == thisMonth);
                var broken = 0;
                if (current?.Status == "Published")
                {
                    var live = slots.Where(s => s.Month.Date == thisMonth).ToList();
                    broken = CountBrokenPlaces(live, roster);
                    if (broken > 0)
                        notes.Add($"The {thisMonth:MMMM} roster has {(broken == 1 ? "1 place" : $"{broken} places")} "
                            + "with a driver no longer active or a bus no longer running. Auto-fill it and publish the changes");
                }

                var next = months.FirstOrDefault(m => m.Month.Date == nextMonth);
                var waiting = today.Day >= RosterCycleService.DraftDay(_config) && next?.Status != "Published";
                var projected = 0;
                if (waiting)
                {
                    // A draft that would publish with slots nobody can cover says so, and says
                    // it while there is still time to move a rest day, rather than "ready for
                    // review" about a roster that will leave buses unstaffed on the day.
                    projected = next is null ? 0 : await ProjectedGapsAsync(nextMonth);

                    var marked = slots.Count(s => s.Month.Date == nextMonth && !string.IsNullOrWhiteSpace(s.Suggested));
                    notes.Add(next is null ? $"Build the {nextMonth:MMMM} roster"
                        : projected > 0 ? $"{nextMonth:MMMM} roster: {(projected == 1 ? "1 slot" : $"{projected} slots")} no floater can cover"
                        : marked > 0 ? $"{nextMonth:MMMM} roster ready for review: {(marked == 1 ? "1 place" : $"{marked} places")} auto-filled"
                        : $"{nextMonth:MMMM} roster ready for review");
                }

                return new NavBadge(
                    open.Count + broken + (waiting ? 1 : 0),
                    open.Any(g => g.Date.Date <= today.AddDays(2)) || projected > 0,
                    notes.Count == 0 ? null : string.Join(". ", notes));
            }
            catch
            {
                return NavBadge.None;
            }
        }

        /// <summary>
        /// Slots publishing the upcoming month as saved would leave empty, worked out by the
        /// same plan the publish runs.
        /// </summary>
        /// <remarks>
        /// A plan that cannot be run leaves the badge on its ordinary review note rather than
        /// inventing a number, and the failure is not held, so the next reading tries again.
        /// </remarks>
        private async Task<int> ProjectedGapsAsync(DateTime month)
        {
            if (_cache.TryGetValue<Projected>(ProjectedKey, out var held) && held is not null && held.Month == month)
                return held.Gaps;

            try
            {
                var plan = await _roster.PlanSavedAsync(month);
                if (plan is null) return 0;

                _cache.Set(ProjectedKey, new Projected(month, plan.Gaps.Count), ProjectedFreshness);
                return plan.Gaps.Count;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Places on a roster whose driver is no longer active, or whose bus is retired, unrouted or moved route.</summary>
        /// <param name="slots">This month's places.</param>
        /// <param name="roster">The roster section, holding the drivers and buses those places name.</param>
        private static int CountBrokenPlaces(IReadOnlyList<RosterSlot> slots, ReadBundle roster)
        {
            if (slots.Count == 0) return 0;

            var active = roster.Rows<UserModel>("slot_drivers")
                .Where(d => string.Equals(d.AccountStatus, "Activated", StringComparison.OrdinalIgnoreCase))
                .Select(d => d.UserId)
                .ToHashSet();
            var buses = roster.Rows<Vehicle>("slot_buses").ToDictionary(v => v.VehicleId, StringComparer.OrdinalIgnoreCase);

            return slots.Count(s =>
                (s.DriverId is int id && !active.Contains(id))
                || (s.VehicleId != null
                    && (!buses.TryGetValue(s.VehicleId, out var bus) || bus.RetiredAt != null || bus.RouteId != s.RouteId)));
        }
    }
}
