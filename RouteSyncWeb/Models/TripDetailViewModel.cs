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

        /// <summary>What this inspection found wrong, one entry per failed item.</summary>
        /// <remarks>
        /// Read from the inspection itself rather than from the maintenance orders. An
        /// order records the inspection that opened it, and a fault reported on a bus
        /// already carrying an open order joins that order instead, so reading the faults
        /// back through the orders finds nothing for exactly the buses that were already
        /// in trouble.
        /// </remarks>
        public List<TripInspectionIssueViewModel> InspectionIssues { get; set; } = new();
    }

    /// <summary>One failed item from an inspection, with anything photographed of it.</summary>
    public class TripInspectionIssueViewModel
    {
        public string Label { get; set; }

        /// <summary>Orders the list, so what grounds the bus is read first.</summary>
        public bool IsCritical { get; set; }

        public List<TripInspectionPhotoViewModel> Photos { get; set; } = new();

        /// <summary>
        /// Photographed, and every photograph has since aged out. Said rather than left
        /// blank, since a blank reads as a fault nobody photographed.
        /// </summary>
        public bool PhotoExpired { get; set; }
    }

    /// <summary>
    /// One photograph, by its row. The stored path never reaches the browser, because the
    /// service key behind the image endpoint would fetch any path it was handed.
    /// </summary>
    public class TripInspectionPhotoViewModel
    {
        public long PhotoId { get; set; }
        public string TakenAt { get; set; }
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
