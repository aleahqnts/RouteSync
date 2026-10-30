using FleetWise.Services;

namespace FleetWise.Models;

// One incident as it reads in the list.
// Title: What the rule found, in plain words.
// Subject: Who or what it is about: an address, or a person by name.
// CanReview:
// False when the incident is about the reader's own account. Rule 3 exists to catch an
// account that was broken into, whose intruder is signed in as its owner, and whose first
// move would otherwise be to mark the incident reviewed.
public sealed record SecurityIncidentRow(
    SecurityIncident Incident, string Title, string Subject, bool CanReview);

public sealed class SecurityIncidentsViewModel
{
    // Null when the incidents could not be read, which is not the same as none.
    public List<SecurityIncidentRow>? Rows { get; set; }

    public int NeedsReview => Rows?.Count(r => r.Incident.NeedsReview) ?? 0;

    // How far the detector has read, so the page can say how current it is.
    public DateTimeOffset? ScannedThrough { get; set; }

    // What a scan on request found, shown once after it runs.
    public string? ScanMessage { get; set; }
}

public sealed class SecurityIncidentDetailViewModel
{
    public required SecurityIncidentRow Row { get; init; }

    // The entries behind the incident, or null when they could not be read.
    public List<AuditEntryViewModel>? Entries { get; init; }

    // A wider search of the audit log around the same subject and days. A search, not
    // the incident's own entries, and labelled as such.
    public required string SearchUrl { get; init; }
}
