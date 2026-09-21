#nullable disable
using System.Collections.Generic;
using Newtonsoft.Json;
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

[Table("maintenance_logs")]
public class MaintenanceLog : BaseModel
{
    [PrimaryKey("log_id")]
    public int LogId { get; set; }

    // Nullable: a maintenance log can be opened without an originating bus_checklist
    // (the DB column is nullable), so a non-nullable int throws on deserialize.
    [Column("checklist_id")]
    public int? ChecklistId { get; set; }

    [Column("vehicle_id")]
    public string VehicleId { get; set; }

    [Column("trip_id")]
    public string TripId { get; set; }

    // issue_details is a JSON object column in Postgres (not text), so it maps to a
    // dictionary, the same way role permissions are handled. Declaring it as a string makes
    // Postgrest's deserializer throw on the leading '{'.
    [Column("issue_details")]
    public MaintenanceIssueDetails IssueDetails { get; set; }

    [Column("maintenance_status")]
    public string MaintenanceStatus { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("resolved_at")]
    public DateTime? ResolvedAt { get; set; }

    [Column("remarks")]
    public string Remarks { get; set; }

    // Backs the Edit Vehicle modal's "Verified by" field.
    [Column("verified_by")]
    public string VerifiedBy { get; set; }
}

// Shape of the `issue_details` jsonb column:
//   { "issues": [...], "severity": "Critical" | "Minor", "critical_issues": [...] }
// Newtonsoft ignores any other/unexpected keys in the jsonb by default, so this stays
// resilient even if more fields get added to issue_details later.
public class MaintenanceIssueDetails
{
    [JsonProperty("issues")]
    public List<string> Issues { get; set; } = new();

    // Absent on every incident raised before inspections told critical items apart.
    // Those were all defects, which is what IsCritical reports for them.
    [JsonProperty("severity")]
    public string Severity { get; set; }

    [JsonProperty("critical_issues")]
    public List<string> CriticalIssues { get; set; } = new();

    // The checklist items the labels above came from, in the same order. Empty on every
    // incident raised before they were recorded, so a reader that needs to match faults
    // across orders has to fall back to the label for those. A label is what a person
    // reads and is kept as it read on the day; an id is what survives it being reworded.
    [JsonProperty("item_ids")]
    public List<int> ItemIds { get; set; } = new();

    [JsonProperty("critical_item_ids")]
    public List<int> CriticalItemIds { get; set; } = new();

    /// <summary>Whether this fault is one that grounds the bus.</summary>
    public bool IsCritical =>
        string.Equals(Severity, "Critical", StringComparison.OrdinalIgnoreCase);

    /// <summary>The critical faults, or all of them when none were singled out.</summary>
    public string CriticalSummary =>
        CriticalIssues is { Count: > 0 } ? string.Join(", ", CriticalIssues) : string.Join(", ", Issues);
}