using System.Linq;

namespace FleetWise.Models
{
    // Read-only projection for the vehicle details modal: the profile, the most recent
    // driver inspection, and the maintenance history. Fetched per vehicle.
    public class VehicleDetailsViewModel
    {
        public string VehicleId { get; set; } = string.Empty;

        // Vehicle Profile.
        public string PlateNumber { get; set; } = string.Empty;
        public string RouteName { get; set; } = string.Empty;
        // The counter phone bound to this bus, or empty when none is bound.
        public string? CounterDeviceId { get; set; }

        // Inspection Log (latest bus_checklist).
        public bool HasInspection { get; set; }
        public string ReportedBy { get; set; } = string.Empty;
        public string TimeOfReport { get; set; } = string.Empty;
        // The flagged areas: checklist sections that did not pass.
        public string Issue { get; set; } = string.Empty;
        // Failed checklist items, rewritten and grouped by section.
        public List<InspectionSectionViewModel> InspectionSections { get; set; } = new();

        // Every item of the latest inspection, for the full checklist view.
        public List<InspectionResultSectionViewModel> InspectionChecklist { get; set; } = new();
        // The badge shown for the inspection: a failure reads as flagged.
        public string InspectionBadge { get; set; } = string.Empty;

        // Maintenance Log.
        public bool HasMaintenance { get; set; }
        // The maintenance badge: no issues, needs attention, or under repair.
        public string CurrentStatus { get; set; } = "No Issues";
        // The incident still open, if there is one. Resolved ones belong to history.
        public MaintenanceEntryViewModel? OpenIncident { get; set; }

        // The inspection items an administrator can book work against, by section.
        public List<InspectionResultSectionViewModel> Catalogue { get; set; } = new();

        // The faults being worked under the open order, still open ones first.
        public List<MaintenanceItemLineViewModel> OpenOrderItems { get; set; } = new();

        // How many of those are still open, which is what decides whether the order can close.
        public int OpenItemCount => OpenOrderItems.Count(i => i.IsOpen);

        // Orders already closed on this bus, most recently closed first.
        public List<PastOrderViewModel> PastOrders { get; set; } = new();

        // Everything that has happened to this bus, newest first.
        //
        // Built from the audit trail rather than from incident notes, because notes hang
        // off an incident and the things an operator most wants to look back on, retiring
        // a bus, grounding it, returning it, happen without one.
        public List<VehicleHistoryEntryViewModel> History { get; set; } = new();

        // Flag review / actions.
        // Whether the bus is grounded, which prevents dispatch assigning it.
        public bool OutOfService { get; set; }

        // Whether the bus has left the fleet, which no service action can undo.
        public bool Retired { get; set; }

        // Whether the bus is out on a trip right now, which no service action may
        // interrupt: grounding it would strand a driver mid-route with passengers
        // aboard, and dispatch would show a bus that cannot be assigned to the trip
        // it is already running.
        public bool OnTrip { get; set; }
        // The unresolved incident to act on, or null when none is open.
        public int? OpenLogId { get; set; }

        // Set when the open incident is one that grounds the bus, so the review panel
        // can say what happened rather than only that the bus cannot be assigned.
        public bool OpenIncidentCritical { get; set; }
        public string OpenIncidentSummary { get; set; } = "";
        // History grouped by incident, so each maintenance lifecycle forms one block.
        // Newest incident first, and newest note first within each.
        public List<VehicleIncidentThreadViewModel> IncidentThreads { get; set; } = new();
    }

    // One entry in the maintenance timeline.
    public class MaintenanceEntryViewModel
    {
        public string Date { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;   // the issue, in plain words
        public string Status { get; set; } = string.Empty;     // Resolved / Under Repair / …
        public bool IsResolved { get; set; }
    }

    // One incident's thread: every note against the same log, covering a single lifecycle
    // from flagged through to resolved, rendered as one block.
    public class VehicleIncidentThreadViewModel
    {
        public int LogId { get; set; }
        public List<VehicleNoteViewModel> Notes { get; set; } = new();
    }

    // One inspection section and the items in it that failed.
    public class InspectionSectionViewModel
    {
        public string Section { get; set; } = string.Empty;
        public List<string> Items { get; set; } = new();
    }

    // One inspection section with every item and how it was marked.
    public class InspectionResultSectionViewModel
    {
        public string Section { get; set; } = string.Empty;
        public List<InspectionResultViewModel> Items { get; set; } = new();
    }

    // One inspected item and whether it passed.
    public sealed record InspectionResultViewModel(string Item, bool Passed, bool IsCritical);

    // One fault on the open order, as it reads in the panel.
    //
    // Photographs default to none so a line built anywhere that does not gather them
    // reads as a fault nobody photographed, which is what it is.
    public sealed record MaintenanceItemLineViewModel(
        long ItemId,
        string Label,
        bool IsCritical,
        bool IsOpen,
        string Outcome,
        string ClosedBy,
        string Note,
        IReadOnlyList<InspectionPhotoLineViewModel>? Photos = null);

    // One photograph behind a fault, as the viewer offers it.
    //
    // The identifier is the row's, not the object's. The dashboard asks for a photograph
    // by the row it can already see, so the only images reachable are ones the database
    // ties to a vehicle. Handing the storage path to the browser would let anyone signed
    // in fetch any object in the bucket, because the key on the server would go and get
    // whatever it was asked for.
    public sealed record InspectionPhotoLineViewModel(long PhotoId, string TakenAt, bool Swept);

    // One thing that happened to a bus, as recorded in the audit trail.
    public sealed record VehicleHistoryEntryViewModel(string When, string Who, string What, bool Refused);

    // One closed maintenance order: what was on it and what was said while it was open.
    public class PastOrderViewModel
    {
        public int LogId { get; set; }
        public string Opened { get; set; } = string.Empty;
        public string Closed { get; set; } = string.Empty;
        // The faults it carried, for the collapsed row.
        public string Summary { get; set; } = string.Empty;
        public List<MaintenanceItemLineViewModel> Items { get; set; } = new();
        public List<VehicleNoteViewModel> Notes { get; set; } = new();
    }

    // One entry in an incident's thread.
    public class VehicleNoteViewModel
    {
        public string Action { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
        public string AuthorName { get; set; } = string.Empty;
        public string When { get; set; } = string.Empty;
    }
}
