using FleetWise.Models;

namespace FleetWise.Services
{
    /// <summary>
    /// A bus shift nobody on the roster can cover, on the same weekday every week.
    /// </summary>
    /// <param name="Weekday">ISO weekday, 1 Monday to 7 Sunday.</param>
    /// <param name="Reason">Why the planner left it empty, as the unfilled list words it.</param>
    /// <param name="RestDay">
    /// The shift has a crew driver, so the gap is their rest day going uncovered. False for a
    /// shift nobody is rostered on and for a retired bus, which the page already names apart.
    /// </param>
    public sealed record WeeklyGap(int RouteId, int Weekday, string VehicleId, string Shift, string Reason, bool RestDay);

    /// <summary>
    /// What a roster's pattern of shifts and rest days can cover, asked of the planner that
    /// publishes it.
    /// </summary>
    /// <remarks>
    /// <para>There is one answer to whether a floater is free, and it is
    /// <see cref="RosterGenerator.Plan"/>. It applies the rest between shifts, the limit on
    /// days worked in a row, and every booking a floater has already taken. A weekday head
    /// count of crew resting against floaters working cannot see any of that: one floater
    /// covering an Evening rest day is not free for a Morning rest day the next morning, and
    /// a count says they are.</para>
    ///
    /// <para>The month planned here is clean. There is no leave, no trip already written and
    /// nothing kept empty on purpose, so the answer is about the pattern alone and does not
    /// move when somebody files leave. What a publish would actually leave, leave and all, is
    /// the real month's plan, asked for separately.</para>
    ///
    /// <para>The month is February 2027, which starts on a Monday and runs exactly four
    /// weeks, so every weekday falls four times and none is favoured.</para>
    /// </remarks>
    public static class RosterStructure
    {
        /// <summary>Four whole weeks, Monday to Sunday.</summary>
        public static readonly DateTime Month = new(2027, 2, 1);

        private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

        /// <summary>The planner's answer for the pattern, with nothing else in the world.</summary>
        public static PublishPlan CleanPlan(
            IReadOnlyList<RosterSeat> seats,
            IReadOnlyList<UserModel> drivers,
            IReadOnlyList<Vehicle> vehicles,
            IReadOnlyDictionary<int, string> routeNames) =>
            RosterGenerator.Plan(new RosterWorld
            {
                Month = Month,
                OperationalDay = Month.AddDays(-1),
                Now = Month.AddDays(-1),
                Seats = seats,
                Trips = Array.Empty<Trip>(),
                Marks = new Dictionary<string, TripRosterMark>(),
                Drivers = drivers,
                Vehicles = vehicles,
                Leave = Array.Empty<LeaveRequest>(),
                Skips = new HashSet<(DateTime, string, string)>(),
                RouteNames = routeNames,
            });

        /// <summary>
        /// Crew rest days left uncovered across the four weeks. What a repair of rest days works
        /// to bring down.
        /// </summary>
        /// <remarks>
        /// A shift with nobody rostered on it is left out. It is a gap every day whatever the
        /// rest days are, and the fix for it is a driver in the seat, which is auto-fill's work.
        /// It still takes up a floater in the plan, so its effect on the rest days is counted.
        /// </remarks>
        public static int RestDayGapCount(
            IReadOnlyList<RosterSeat> seats,
            IReadOnlyList<UserModel> drivers,
            IReadOnlyList<Vehicle> vehicles,
            IReadOnlyDictionary<int, string> routeNames)
        {
            var crewed = Crewed(seats);
            return CleanPlan(seats, drivers, vehicles, routeNames).Gaps
                .Count(g => crewed.Contains((g.RouteId, g.VehicleId.ToUpperInvariant(), g.Shift.ToUpperInvariant())));
        }

        /// <summary>The pattern's gaps, one per bus shift and weekday however many weeks it repeats.</summary>
        public static IReadOnlyList<WeeklyGap> WeeklyGaps(
            IReadOnlyList<RosterSeat> seats,
            IReadOnlyList<UserModel> drivers,
            IReadOnlyList<Vehicle> vehicles,
            IReadOnlyDictionary<int, string> routeNames)
        {
            var crewed = Crewed(seats);

            return CleanPlan(seats, drivers, vehicles, routeNames).Gaps
                .GroupBy(g => (g.RouteId, Weekday: IsoWeekday(g.Date), Bus: g.VehicleId.ToUpperInvariant(), Shift: g.Shift.ToUpperInvariant()))
                .Select(g => new WeeklyGap(
                    g.Key.RouteId, g.Key.Weekday, g.First().VehicleId, g.First().Shift, g.First().Reason,
                    crewed.Contains((g.Key.RouteId, g.Key.Bus, g.Key.Shift))))
                .OrderBy(g => g.RouteId)
                .ThenBy(g => g.Weekday)
                .ThenBy(g => ShiftOrder(g.Shift))
                .ThenBy(g => g.VehicleId, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Bus shifts with a crew driver on them, whose gaps are rest days.</summary>
        private static HashSet<(int RouteId, string Bus, string Shift)> Crewed(IReadOnlyList<RosterSeat> seats) =>
            seats.Where(s => s.Kind == RosterRules.Crew && s.DriverId is not null && s.VehicleId is not null)
                 .Select(s => (s.RouteId, s.VehicleId!.ToUpperInvariant(), s.Shift.ToUpperInvariant()))
                 .ToHashSet();

        private static int IsoWeekday(DateTime day) => day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek;

        private static int ShiftOrder(string shift)
        {
            for (var i = 0; i < RosterRules.Shifts.Count; i++)
                if (Ci.Equals(RosterRules.Shifts[i], shift)) return i;
            return RosterRules.Shifts.Count;
        }
    }
}
