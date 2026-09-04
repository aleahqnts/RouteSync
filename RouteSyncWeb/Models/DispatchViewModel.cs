namespace FleetWise.ViewModels
{
    public class DispatchViewModel
    {
        public DateTime ScheduleDate { get; set; }
        public string PrevDate { get; set; }
        public string NextDate { get; set; }
        public bool IsToday { get; set; }
        public int ActiveTrips { get; set; }
        public int TripsNotStarted { get; set; }

        /// <summary>
        /// Trips inside their window that should have departed and have not.
        /// </summary>
        /// <remarks>
        /// Counted apart from the ones awaiting departure rather than among them. A shift
        /// that has not begun and a shift that has begun without the bus moving are the
        /// same figure only until somebody has to act on one of them.
        /// </remarks>
        public int DelayedTrips { get; set; }

        public int UnassignedTrips { get; set; }
        public int FlaggedVehicles { get; set; }
        public int UnavailableDrivers { get; set; }

        public List<RouteDispatchGroup> Routes { get; set; } = new();
    }

    public class RouteDispatchGroup
    {
        public int RouteId { get; set; }
        public string RouteName { get; set; }
        public bool NeedsAssignment { get; set; }
        public List<ShiftGroup> Shifts { get; set; } = new();
    }

    public class ShiftGroup
    {
        public string ShiftType { get; set; }
        public string ShiftStartTime { get; set; }
        public string ShiftEndTime { get; set; }
        public bool IsOvernight { get; set; }
        public List<TripRow> Trips { get; set; } = new();
    }

    public class TripRow
    {
        public string TripId { get; set; }
        public string VehicleId { get; set; }
        public string PlateNumber { get; set; }
        public string VehicleStatus { get; set; }
        public string DriverName { get; set; }

        /// <summary>The driver's user id, shown beside the name.</summary>
        /// <remarks>
        /// The only identifier a person has in this system. It is printed here so the
        /// cell reads the way the bus cell beside it does, and so the board's search,
        /// which matches the text of a row, can be given one.
        /// </remarks>
        public int DriverId { get; set; }

        public string DriverStatus { get; set; }
        public string TripStatus { get; set; }

        // Roadworthiness, derived from THIS trip's own checklist (a failed inspection),
        // kept separate from the operational dot so a flagged bus that proceeds shows
        // both the trip state and the flag, so neither overwrites the other.
        public bool Flagged { get; set; }

        // A trip already running whose driver has reported they cannot drive. It stays
        // Active, because it is, but the driver has to be swapped out before the bus can
        // finish the route.
        public bool NeedsRelief { get; set; }

        // Why a trip is an Assignment Issue (out-of-service bus, a driver who cannot
        // drive), or why a running one needs relieving. Shown as the badge's tooltip.
        // Null when there is nothing wrong.
        public string AssignmentIssueReason { get; set; }

        /// <summary>How long a trip that should have departed has not, if it has not.</summary>
        public TimeSpan? LateBy { get; set; }

        /// <summary>How late, in the fewest words that still say it.</summary>
        /// <remarks>
        /// Rounded down to the unit above it once an hour has passed. A dispatcher reading
        /// a board decides from "over an hour", not from the minutes on top of it.
        /// </remarks>
        public string LateLabel =>
            LateBy is not TimeSpan by ? null
            : by.TotalHours >= 1 ? $"{(int)by.TotalHours}h late"
            : $"{(int)by.TotalMinutes}m late";
    }
}
