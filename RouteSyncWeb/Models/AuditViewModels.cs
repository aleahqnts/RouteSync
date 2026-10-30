namespace FleetWise.Models
{
    // One entry of the audit trail, as shown on the audit log page.
    public class AuditEntryViewModel
    {
        public long Id { get; set; }
        public DateTimeOffset OccurredAt { get; set; }   // stored UTC, displayed as PH time
        public string ActorType { get; set; } = "";      // user | device | admin | system | anon
        public string? ActorId { get; set; }
        public string? ActorRole { get; set; }
        public string Action { get; set; } = "";
        public string? TargetTable { get; set; }
        public string? TargetId { get; set; }
        public string Source { get; set; } = "";         // db | edge | web
        public string Outcome { get; set; } = "";        // ok | denied | error
        public string? Summary { get; set; }
        public string? Ip { get; set; }

        // Whether the entry recorded a row edit at all.
        public bool HasChanges { get; set; }

        // The row edit read field by field, empty when it cannot be expressed that way.
        public List<AuditFieldChange> FieldChanges { get; set; } = new();
    }

    // One column of a row edit. From is null on an insert, To is null on a delete.
    public sealed record AuditFieldChange(string Field, string? From, string? To);

    public class AuditIndexViewModel
    {
        public List<AuditEntryViewModel> Entries { get; set; } = new();

        // Null means the read itself failed, which is different from "nothing matched".
        // The page says so rather than pretending the trail is empty.
        public bool LoadFailed { get; set; }

        public int Page { get; set; } = 1;
        public int PageSize { get; set; }
        public int Total { get; set; }
        public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));

        // Filter state, echoed back into the form and the paging links.
        public string? Type { get; set; }
        public string? Source { get; set; }
        public string? Outcome { get; set; }
        public string? Query { get; set; }
        public string? From { get; set; }
        public string? To { get; set; }

        public bool HasFilters =>
            !string.IsNullOrWhiteSpace(Type) || !string.IsNullOrWhiteSpace(Source)
            || !string.IsNullOrWhiteSpace(Outcome) || !string.IsNullOrWhiteSpace(Query)
            || !string.IsNullOrWhiteSpace(From) || !string.IsNullOrWhiteSpace(To);
    }
}
