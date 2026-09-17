namespace FleetWise.Services
{
    /// <summary>The figures a candidate was ranked on, as every screen receives them.</summary>
    /// <remarks>
    /// One shape wherever a ranking is sent to the browser, so the list that shows it can be
    /// the same list on every screen. Each endpoint keeps its own names for the candidate
    /// itself and adds these as <c>facts</c>.
    /// </remarks>
    public static class CandidateJson
    {
        /// <summary>A driver as the gap and cover lists receive one.</summary>
        /// <remarks>
        /// <c>sentence</c> is the pick in one line, for a screen that shows a single pick
        /// rather than the list; see <see cref="SchedulingRules.PickSentence(DriverCandidate)"/>.
        /// </remarks>
        public static object Driver(DriverCandidate c) => new
        {
            driverId = c.DriverId,
            name = c.Name,
            rank = c.Rank,
            tier = c.Tier,
            reason = c.Reason,
            warning = c.Warning,
            sentence = SchedulingRules.PickSentence(c),
            facts = Facts(c.Facts),
        };

        public static object? Facts(DriverFacts? f) => f is null ? null : new
        {
            routeTrips = f.RouteTrips,
            routeName = f.RouteName,
            weekShifts = f.WeekShifts,
            otherShift = f.OtherShift,
            daysInRow = f.DaysInRow,
        };

        public static object? Facts(VehicleFacts? f) => f is null ? null : new
        {
            seats = f.Seats,
            fewerSeatsThan = f.FewerSeatsThan,
            homeRoute = f.HomeRoute,
            sameRoute = f.SameRoute,
            weekTrips = f.WeekTrips,
        };
    }
}
