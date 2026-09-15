using System.Text.Json.Serialization;

namespace FleetWise.ViewModels
{
    public class TripDetailViewModel
    {
        // Trip Info
        public string TripId { get; set; }
        public string TripStatus { get; set; }
        public string ShiftType { get; set; }
        public string ShiftStartTime { get; set; }
        public string ShiftEndTime { get; set; }
        public string RouteName { get; set; }

        // Break: the slot as "HH:mm", null on a trip written before breaks existed.
        public string BreakStart { get; set; }
        public string BreakLabel { get; set; }
        public bool OnBreak { get; set; }

        // Whether the break can still be moved: not once the trip is finished or its
        // shift is over.
        public bool BreakEditable { get; set; }
        public List<BreakOptionViewModel> BreakOptions { get; set; } = new();

        // Vehicle Details
        public string VehicleId { get; set; }
        public string VehicleType { get; set; }
        public string PlateNumber { get; set; }
        public string VehicleStatus { get; set; }

        // Driver Details
        public string DriverName { get; set; }
        public string DriverId { get; set; }
        public string DriverStatus { get; set; }

        // Trip Outcome (only meaningful once the trip is Completed)
        public bool IsCompleted { get; set; }
        public int? TotalBoarded { get; set; }
        public decimal? EstimatedRevenue { get; set; }
        public string ActualStartTime { get; set; }
        public string ActualEndTime { get; set; }

        // Inspection Log
        public TripChecklistViewModel Checklist { get; set; }
        public List<TripMaintenanceLogViewModel> MaintenanceLogs { get; set; } = new();
    }

    /// <summary>One break slot a trip could take.</summary>
    public class BreakOptionViewModel
    {
        public string Value { get; set; }
        public string Label { get; set; }

        /// <summary>Other buses on the same route, shift and day already breaking then.</summary>
        public int Others { get; set; }
    }

    public class TripChecklistViewModel
    {
        public int ChecklistId { get; set; }
        public DateTime SubmittedAt { get; set; }
        public string ChecklistStatus { get; set; }
        public string Notes { get; set; }

        // Each section is a flat dictionary of item → result
        public Dictionary<string, string> ExteriorInspection { get; set; }
        public Dictionary<string, string> EngineCompartment { get; set; }
        public Dictionary<string, string> InteriorInspection { get; set; }
        public Dictionary<string, string> BrakeSafety { get; set; }
        public Dictionary<string, string> PassengerSystems { get; set; }
    }

    public class TripMaintenanceLogViewModel
    {
        public int LogId { get; set; }
        public List<string> IssueDetails { get; set; } = new();

        // Whether this fault grounds the bus, so a dispatcher can tell a trip that
        // needs reassigning from one that merely needs looking at.
        public bool IsCritical { get; set; }
        public List<string> CriticalIssues { get; set; } = new();

        public string MaintenanceStatus { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public string Remarks { get; set; }
    }
}
