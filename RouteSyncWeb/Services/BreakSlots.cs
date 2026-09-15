using System.Globalization;
using FleetWise.Models;

namespace FleetWise.Services
{
    /// <summary>
    /// The one hour break inside a shift: which hours it may take, which one a new trip
    /// gets, and whether a trip is on it now.
    /// </summary>
    /// <remarks>
    /// <para>Three slots, starting three, four and five hours after the shift does. A route's
    /// buses on one shift take different slots so they are not all parked at once. The
    /// database holds every trip to the same three, measured from the trip's own start
    /// time, so a slot chosen here can never be refused there.</para>
    ///
    /// <para>Every place that gives a trip a slot goes through <see cref="LeastUsed"/>, so
    /// Add Trip, the planner and anything added later cannot drift apart.</para>
    ///
    /// <para>The break is a label. Nothing starts or ends it: a driver who forgot to press
    /// End Break would read as parked for the rest of the shift.</para>
    /// </remarks>
    public static class BreakSlots
    {
        public static readonly TimeSpan Length = TimeSpan.FromHours(1);

        private static readonly int[] OffsetHours = { 3, 4, 5 };

        /// <summary>The slots a shift starting at this time may take, earliest first.</summary>
        public static IReadOnlyList<TimeSpan> For(TimeSpan shiftStart) =>
            OffsetHours.Select(h => WrapDay(shiftStart + TimeSpan.FromHours(h))).ToList();

        /// <summary>Whether a break start is one of the slots for a shift starting at this time.</summary>
        public static bool IsSlot(TimeSpan shiftStart, TimeSpan breakStart) =>
            For(shiftStart).Contains(breakStart);

        /// <summary>
        /// The slot the fewest of these trips already take, the earliest of them on a tie.
        /// </summary>
        /// <param name="taken">
        /// The breaks of the other trips on the same route, shift and day. A null, a trip
        /// from before breaks existed, takes nothing.
        /// </param>
        /// <remarks>
        /// Existing trips never move to make room. A new bus fits around the ones already
        /// booked, so a driver told their break this morning is not given another one because
        /// somebody was added at noon.
        /// </remarks>
        public static TimeSpan LeastUsed(TimeSpan shiftStart, IEnumerable<TimeSpan?> taken)
        {
            var counts = taken.Where(t => t.HasValue).GroupBy(t => t!.Value).ToDictionary(g => g.Key, g => g.Count());
            return For(shiftStart)
                .Select((slot, i) => (slot, i, n: counts.GetValueOrDefault(slot)))
                .OrderBy(x => x.n)
                .ThenBy(x => x.i)
                .First().slot;
        }

        /// <summary>When a trip's break begins and ends, or null when it has none.</summary>
        /// <remarks>
        /// A slot earlier in the day than the shift's start falls after midnight, on the
        /// calendar day after the trip's date. That is every Evening slot.
        /// </remarks>
        public static (DateTime Start, DateTime End)? WindowOf(Trip trip)
        {
            if (trip.BreakStart is not TimeSpan b) return null;
            var start = trip.Date.Date.Add(b).AddDays(b < trip.ShiftStartTime ? 1 : 0);
            return (start, start + Length);
        }

        /// <summary>Whether the clock is inside a trip's break.</summary>
        public static bool IsOnBreak(Trip trip, DateTime now) =>
            WindowOf(trip) is { } w && now >= w.Start && now < w.End;

        /// <summary>A slot as a dispatcher and a driver read it: "1:00 AM to 2:00 AM".</summary>
        public static string Label(TimeSpan breakStart) =>
            $"{Clock(breakStart)} to {Clock(WrapDay(breakStart + Length))}";

        /// <summary>A time of day as "1:00 AM".</summary>
        public static string Clock(TimeSpan time) =>
            DateTime.Today.Add(time).ToString("h:mm tt", CultureInfo.InvariantCulture);

        private static TimeSpan WrapDay(TimeSpan t) =>
            TimeSpan.FromTicks(((t.Ticks % TimeSpan.TicksPerDay) + TimeSpan.TicksPerDay) % TimeSpan.TicksPerDay);
    }
}
