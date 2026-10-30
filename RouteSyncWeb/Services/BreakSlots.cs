using System.Globalization;
using FleetWise.Models;

namespace FleetWise.Services
{
    // The one hour break inside a shift: which hours it may take, which one a new trip
    // gets, and whether a trip is on it now.
    //
    // Three slots, starting three, four and five hours after the shift does. A route's
    // buses on one shift take different slots so they are not all parked at once. The
    // database holds every trip to the same three, measured from the trip's own start
    // time, so a slot chosen here can never be refused there.
    //
    // Every place that gives a trip a slot goes through LeastUsed, so
    // Add Trip, the planner and anything added later cannot drift apart.
    //
    // The break is a label. Nothing starts or ends it: a driver who forgot to press
    // End Break would read as parked for the rest of the shift.
    public static class BreakSlots
    {
        public static readonly TimeSpan Length = TimeSpan.FromHours(1);

        private static readonly int[] OffsetHours = { 3, 4, 5 };

        // The slots a shift starting at this time may take, earliest first.
        public static IReadOnlyList<TimeSpan> For(TimeSpan shiftStart) =>
            OffsetHours.Select(h => WrapDay(shiftStart + TimeSpan.FromHours(h))).ToList();

        // Whether a break start is one of the slots for a shift starting at this time.
        public static bool IsSlot(TimeSpan shiftStart, TimeSpan breakStart) =>
            For(shiftStart).Contains(breakStart);

        // The slot the fewest of these trips already take, the earliest of them on a tie.
        // taken:
        // The breaks of the other trips on the same route, shift and day. A null, a trip
        // from before breaks existed, takes nothing.
        //
        // Existing trips never move to make room. A new bus fits around the ones already
        // booked, so a driver told their break this morning is not given another one because
        // somebody was added at noon.
        public static TimeSpan LeastUsed(TimeSpan shiftStart, IEnumerable<TimeSpan?> taken)
        {
            var counts = taken.Where(t => t.HasValue).GroupBy(t => t!.Value).ToDictionary(g => g.Key, g => g.Count());
            return For(shiftStart)
                .Select((slot, i) => (slot, i, n: counts.GetValueOrDefault(slot)))
                .OrderBy(x => x.n)
                .ThenBy(x => x.i)
                .First().slot;
        }

        // When a trip's break begins and ends, or null when it has none.
        //
        // A slot earlier in the day than the shift's start falls after midnight, on the
        // calendar day after the trip's date. That is every Evening slot.
        public static (DateTime Start, DateTime End)? WindowOf(Trip trip)
        {
            if (trip.BreakStart is not TimeSpan b) return null;
            var start = trip.Date.Date.Add(b).AddDays(b < trip.ShiftStartTime ? 1 : 0);
            return (start, start + Length);
        }

        // Whether the clock is inside a trip's break.
        public static bool IsOnBreak(Trip trip, DateTime now) =>
            WindowOf(trip) is { } w && now >= w.Start && now < w.End;

        // A slot as a dispatcher and a driver read it: "1:00 AM to 2:00 AM".
        public static string Label(TimeSpan breakStart) =>
            $"{Clock(breakStart)} to {Clock(WrapDay(breakStart + Length))}";

        // A time of day as "1:00 AM".
        public static string Clock(TimeSpan time) =>
            DateTime.Today.Add(time).ToString("h:mm tt", CultureInfo.InvariantCulture);

        private static TimeSpan WrapDay(TimeSpan t) =>
            TimeSpan.FromTicks(((t.Ticks % TimeSpan.TicksPerDay) + TimeSpan.TicksPerDay) % TimeSpan.TicksPerDay);
    }
}
