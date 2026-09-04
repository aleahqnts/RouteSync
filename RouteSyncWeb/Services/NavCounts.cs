using FleetWise.Models;
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

            var vehiclesTask = _supabase.From<Vehicle>().Get();
            var availabilityTask = _supabase.From<DriverAvailability>().Get();
            var maintTask = _supabase.From<MaintenanceLog>().Get();
            var leaveTask = _supabase.From<LeaveRequest>().Get();

            await Task.WhenAll(tripsTask, vehiclesTask, availabilityTask, maintTask, leaveTask);

            var trips = tripsTask.Result.Models;
            var vehicles = vehiclesTask.Result.Models;
            var leave = leaveTask.Result.Models;

            var todayTrips = trips.Where(t => t.Date.Date == today).ToList();

            var grounded = vehicles
                .Where(v => v.OutOfService)
                .Select(v => v.VehicleId)
                .ToHashSet();

            var cannotDrive = availabilityTask.Result.Models
                .Where(a => string.Equals(a.AvailabilityStatus, "Unavailable",
                                          StringComparison.OrdinalIgnoreCase))
                .Select(a => a.UserId)
                .ToHashSet();

            var offToday = leave
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

            var open = leave
                .Where(l => LeaveEntitlement.IsOpen(l.Status)
                            || (l.WithdrawRequestedAt is not null && l.WithdrawAnsweredAt is null))
                .ToList();

            // Measured against the shift rather than against the request. A leave filed
            // for a day already gone has nothing to scramble for; a leave that began last
            // week and covers tonight has a shift tonight, and the shift is what decides.
            //
            // An asking to cancel granted leave is counted and never urgent: it hands a
            // driver back, which is a decision to be made rather than a hole to be filled.
            bool urgent = open.Any(l =>
                LeaveEntitlement.IsOpen(l.Status)
                && trips.Any(t =>
                    t.DriverId == l.UserId
                    && t.Date.Date >= l.StartDate.Date
                    && t.Date.Date <= l.EndDate.Date
                    && !LeaveEntitlement.IsRevokedOn(l, t.Date)
                    && !TripStatus.Locked(t, now)
                    && TripStatus.StartOf(t) - now <= UrgentWithin));

            // A bus nobody can send. An open fault or a grounding is the same job either
            // way, and neither stops the day the way a trip that cannot run does.
            var flagged = maintTask.Result.Models
                .Where(l => l.ResolvedAt == null && l.VehicleId != null)
                .Select(l => l.VehicleId)
                .ToHashSet();

            flagged.UnionWith(grounded);

            return new NavBadges(
                new NavBadge(dispatch, dispatch > 0),
                new NavBadge(open.Count, urgent),
                new NavBadge(flagged.Count, false));
        }
    }
}
