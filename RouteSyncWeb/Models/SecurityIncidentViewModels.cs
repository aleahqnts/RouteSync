using FleetWise.Services;

namespace FleetWise.Models;

/// <summary>One incident as it reads in the list.</summary>
/// <param name="Title">What the rule found, in plain words.</param>
/// <param name="Subject">Who or what it is about: an address, or a person by name.</param>
/// <param name="CanReview">
/// False when the incident is about the reader's own account. Rule 3 exists to catch an
/// account that was broken into, whose intruder is signed in as its owner, and whose first
/// move would otherwise be to mark the incident reviewed.
/// </param>
public sealed record SecurityIncidentRow(
    SecurityIncident Incident, string Title, string Subject, bool CanReview);

public sealed class SecurityIncidentsViewModel
{
    /// <summary>Null when the incidents could not be read, which is not the same as none.</summary>
    public List<SecurityIncidentRow>? Rows { get; set; }

    public int NeedsReview => Rows?.Count(r => r.Incident.NeedsReview) ?? 0;

    /// <summary>How far the detector has read, so the page can say how current it is.</summary>
    public DateTimeOffset? ScannedThrough { get; set; }

    /// <summary>What a scan on request found, shown once after it runs.</summary>
    public string? ScanMessage { get; set; }
}

public sealed class SecurityIncidentDetailViewModel
{
    public required SecurityIncidentRow Row { get; init; }

    /// <summary>The entries behind the incident, or null when they could not be read.</summary>
    public List<AuditEntryViewModel>? Entries { get; init; }

    /// <summary>
    /// A wider search of the audit log around the same subject and days. A search, not
    /// the incident's own entries, and labelled as such.
    /// </summary>
    public required string SearchUrl { get; init; }
}
