using System.Linq;

namespace FleetWise.Models
{
    /// <summary>
    /// Read-only projection for the vehicle details modal: the profile, the most recent
    /// driver inspection, and the maintenance history. Fetched per vehicle.
    /// </summary>
    public class VehicleDetailsViewModel
    {
        public string VehicleId { get; set; } = string.Empty;

        // Vehicle Profile.
        public string PlateNumber { get; set; } = string.Empty;
        public string RouteName { get; set; } = string.Empty;
        /// <summary>The counter phone bound to this bus, or empty when none is bound.</summary>
        public string? CounterDeviceId { get; set; }

        // Inspection Log (latest bus_checklist).
        public bool HasInspection { get; set; }
        public string ReportedBy { get; set; } = string.Empty;
        public string TimeOfReport { get; set; } = string.Empty;
        /// <summary>The flagged areas: checklist sections that did not pass.</summary>
        public string Issue { get; set; } = string.Empty;
        /// <summary>Failed checklist items, rewritten and grouped by section.</summary>
        public List<InspectionSectionViewModel> InspectionSections { get; set; } = new();

        /// <summary>Every item of the latest inspection, for the full checklist view.</summary>
        public List<InspectionResultSectionViewModel> InspectionChecklist { get; set; } = new();
        /// <summary>The badge shown for the inspection: a failure reads as flagged.</summary>
        public string InspectionBadge { get; set; } = string.Empty;

        // Maintenance Log.
        public bool HasMaintenance { get; set; }
        /// <summary>The maintenance badge: no issues, needs attention, or under repair.</summary>
        public string CurrentStatus { get; set; } = "No Issues";
        /// <summary>The incident still open, if there is one. Resolved ones belong to history.</summary>
        public MaintenanceEntryViewModel? OpenIncident { get; set; }

        /// <summary>The inspection items an administrator can book work against, by section.</summary>
        public List<InspectionResultSectionViewModel> Catalogue { get; set; } = new();

        /// <summary>The faults being worked under the open order, still open ones first.</summary>
        public List<MaintenanceItemLineViewModel> OpenOrderItems { get; set; } = new();

        /// <summary>How many of those are still open, which is what decides whether the order can close.</summary>
        public int OpenItemCount => OpenOrderItems.Count(i => i.IsOpen);

        /// <summary>Orders already closed on this bus, most recently closed first.</summary>
        public List<PastOrderViewModel> PastOrders { get; set; } = new();

        /// <summary>
        /// Everything that has happened to this bus, newest first.
        /// </summary>
        /// <remarks>
        /// Built from the audit trail rather than from incident notes, because notes hang
        /// off an incident and the things an operator most wants to look back on, retiring
        /// a bus, grounding it, returning it, happen without one.
        /// </remarks>
        public List<VehicleHistoryEntryViewModel> History { get; set; } = new();

        // Flag review / actions.
        /// <summary>Whether the bus is grounded, which prevents dispatch assigning it.</summary>
        public bool OutOfService { get; set; }

        /// <summary>Whether the bus has left the fleet, which no service action can undo.</summary>
        public bool Retired { get; set; }

        /// <summary>
        /// Whether the bus is out on a trip right now, which no service action may
        /// interrupt: grounding it would strand a driver mid-route with passengers
        /// aboard, and dispatch would show a bus that cannot be assigned to the trip
        /// it is already running.
        /// </summary>
        public bool OnTrip { get; set; }
        /// <summary>The unresolved incident to act on, or null when none is open.</summary>
        public int? OpenLogId { get; set; }

        // Set when the open incident is one that grounds the bus, so the review panel
        // can say what happened rather than only that the bus cannot be assigned.
        public bool OpenIncidentCritical { get; set; }
        public string OpenIncidentSummary { get; set; } = "";
        /// <summary>
        /// History grouped by incident, so each maintenance lifecycle forms one block.
        /// Newest incident first, and newest note first within each.
        /// </summary>
        public List<VehicleIncidentThreadViewModel> IncidentThreads { get; set; } = new();
    }

    /// <summary>One entry in the maintenance timeline.</summary>
    public class MaintenanceEntryViewModel
    {
        public string Date { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;   // the issue, in plain words
        public string Status { get; set; } = string.Empty;     // Resolved / Under Repair / …
        public bool IsResolved { get; set; }
    }

    /// <summary>
    /// One incident's thread: every note against the same log, covering a single lifecycle
    /// from flagged through to resolved, rendered as one block.
    /// </summary>
    public class VehicleIncidentThreadViewModel
    {
        public int LogId { get; set; }
        public List<VehicleNoteViewModel> Notes { get; set; } = new();
    }

    /// <summary>One inspection section and the items in it that failed.</summary>
    public class InspectionSectionViewModel
    {
        public string Section { get; set; } = string.Empty;
        public List<string> Items { get; set; } = new();
    }

    /// <summary>One inspection section with every item and how it was marked.</summary>
    public class InspectionResultSectionViewModel
    {
        public string Section { get; set; } = string.Empty;
        public List<InspectionResultViewModel> Items { get; set; } = new();
    }

    /// <summary>One inspected item and whether it passed.</summary>
    public sealed record InspectionResultViewModel(string Item, bool Passed, bool IsCritical);

    /// <summary>One fault on the open order, as it reads in the panel.</summary>
    /// <remarks>
    /// Photographs default to none so a line built anywhere that does not gather them
    /// reads as a fault nobody photographed, which is what it is.
    /// </remarks>
    public sealed record MaintenanceItemLineViewModel(
        long ItemId,
        string Label,
        bool IsCritical,
        bool IsOpen,
        string Outcome,
        string ClosedBy,
        string Note,
        IReadOnlyList<InspectionPhotoLineViewModel>? Photos = null);

    /// <summary>
    /// One photograph behind a fault, as the viewer offers it.
    /// </summary>
    /// <remarks>
    /// The identifier is the row's, not the object's. The dashboard asks for a photograph
    /// by the row it can already see, so the only images reachable are ones the database
    /// ties to a vehicle. Handing the storage path to the browser would let anyone signed
    /// in fetch any object in the bucket, because the key on the server would go and get
    /// whatever it was asked for.
    /// </remarks>
    public sealed record InspectionPhotoLineViewModel(long PhotoId, string TakenAt, bool Swept);

    /// <summary>One thing that happened to a bus, as recorded in the audit trail.</summary>
    public sealed record VehicleHistoryEntryViewModel(string When, string Who, string What, bool Refused);

    /// <summary>
    /// One closed maintenance order: what was on it and what was said while it was open.
    /// </summary>
    public class PastOrderViewModel
    {
        public int LogId { get; set; }
        public string Opened { get; set; } = string.Empty;
        public string Closed { get; set; } = string.Empty;
        /// <summary>The faults it carried, for the collapsed row.</summary>
        public string Summary { get; set; } = string.Empty;
        public List<MaintenanceItemLineViewModel> Items { get; set; } = new();
        public List<VehicleNoteViewModel> Notes { get; set; } = new();
    }

    /// <summary>One entry in an incident's thread.</summary>
    public class VehicleNoteViewModel
    {
        public string Action { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
        public string AuthorName { get; set; } = string.Empty;
        public string When { get; set; } = string.Empty;
    }
}
