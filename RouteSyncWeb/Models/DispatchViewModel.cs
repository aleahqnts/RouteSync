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
    }
}
