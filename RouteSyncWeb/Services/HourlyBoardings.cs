using FleetWise.Models;

namespace FleetWise.Services
{
    /// <summary>A service day's boardings, hour by hour from 06:00 to 05:59.</summary>
    /// <remarks>
    /// <para>Built from the counter's boarding events, each placed in the hour it happened.
    /// A trip's count can run ahead of its events, since a driver can add passengers by hand
    /// during a camera outage and those carry no time. That remainder is placed in the hour
    /// the trip ended, or the current hour for a trip still running, so the hours add up to
    /// the trips' reported totals.</para>
    ///
    /// <para>Event times are true UTC and are moved to Philippine time here. Trip start and
    /// end times are stored as Philippine clock time already and are read as they are.</para>
    /// </remarks>
    public static class HourlyBoardings
    {
        public const int Hours = 24;

        private static readonly TimeSpan Ph = TimeSpan.FromHours(8);

        /// <summary>When the service day's first hour starts.</summary>
        public static DateTime CycleStart(DateTime day) => day.Date.Add(PhClock.DayStartTime);

        /// <summary>
        /// Passengers boarded in each hour of the day, or null for an hour that has not
        /// started yet.
        /// </summary>
        /// <param name="day">The operational day.</param>
        /// <param name="trips">The day's trips, already narrowed to the routes wanted.</param>
        /// <param name="events">Boarding events for those trips; others are ignored.</param>
        /// <param name="now">Philippine time now. A past day passes its end or later.</param>
        public static int?[] ByHour(DateTime day, IReadOnlyList<Trip> trips, IEnumerable<BoardingEvent> events, DateTime now)
        {
            var start = CycleStart(day);
            var counts = new int[Hours];

            int? Hour(DateTime at)
            {
                var h = (int)Math.Floor((at - start).TotalHours);
                return h is >= 0 and < Hours ? h : null;
            }

            var tripIds = trips.Select(t => t.TripId).ToHashSet();
            var inByTrip = new Dictionary<string, int>();

            foreach (var e in events)
            {
                if (!string.Equals(e.Direction, "in", StringComparison.OrdinalIgnoreCase) || !tripIds.Contains(e.TripId))
                    continue;

                inByTrip[e.TripId] = inByTrip.GetValueOrDefault(e.TripId) + 1;

                var at = e.DeviceTimestamp.ToOffset(Ph).DateTime;
                if (at <= now && Hour(at) is int h) counts[h]++;
            }

            foreach (var t in trips)
            {
                var remainder = t.TotalBoarded - inByTrip.GetValueOrDefault(t.TripId);
                if (remainder <= 0) continue;

                var end = t.ActualEndTime?.ToUniversalTime()
                    ?? (string.Equals(t.TripStatus, "Active", StringComparison.OrdinalIgnoreCase) ? now : TripStatus.ShiftEndAt(t));
                if (end > now) end = now;
                if (Hour(end) is int h) counts[h] += remainder;
            }

            return Enumerable.Range(0, Hours)
                .Select(h => start.AddHours(h) > now ? (int?)null : counts[h])
                .ToArray();
        }

        /// <summary>
        /// The average for each hour across past days, leaving out days nobody boarded on,
        /// which are days the service or the counting was not running rather than quiet ones.
        /// </summary>
        /// <returns>One figure per hour, or null when no past day had boardings.</returns>
        public static double[]? Usual(IEnumerable<int?[]> days)
        {
            var counted = days.Where(d => d.Any(v => v > 0)).ToList();
            if (counted.Count == 0) return null;

            return Enumerable.Range(0, Hours)
                .Select(h => Math.Round(counted.Average(d => d[h] ?? 0), 1))
                .ToArray();
        }
    }
}
