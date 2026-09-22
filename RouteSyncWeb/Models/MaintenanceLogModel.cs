#nullable disable
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
    // Held as raw JSON rather than as a list of integers, because the Postgrest client
    // rewrites a list of integers into a Postgres array literal on the way out. Inside a
    // JSON column that arrives as the string "{}", which cannot be read back into a list,
    // and the failure takes down every page that loads maintenance rather than the one
    // row carrying it. A per-property converter does not help: the client's contract
    // resolver replaces it. Lists of strings are written as JSON arrays by the same
    // client, which is why only the id lists need this.
    [JsonProperty("item_ids")]
    public JToken RawItemIds { get; set; }

    [JsonProperty("critical_item_ids")]
    public JToken RawCriticalItemIds { get; set; }

    [JsonIgnore]
    public List<int> ItemIds => IdsFrom(RawItemIds);

    [JsonIgnore]
    public List<int> CriticalItemIds => IdsFrom(RawCriticalItemIds);

    /// <summary>The ids in a value that may be a JSON array or an array literal.</summary>
    /// <remarks>
    /// Both shapes are in the table: the array literal on orders opened by the dashboard
    /// before this was understood, and a plain array on those the inspection function
    /// wrote. Reading accepts either, so nothing has to be repaired in the database.
    /// </remarks>
    private static List<int> IdsFrom(JToken value)
    {
        var ids = new List<int>();
        if (value is null || value.Type == JTokenType.Null) return ids;

        if (value is JArray array)
        {
            foreach (var entry in array)
                if (int.TryParse(entry.ToString(), out var id)) ids.Add(id);
            return ids;
        }

        foreach (var part in (value.ToString() ?? "").Trim('{', '}').Split(','))
            if (int.TryParse(part.Trim(), out var id)) ids.Add(id);
        return ids;
    }

    /// <summary>Whether this fault is one that grounds the bus.</summary>
    public bool IsCritical =>
        string.Equals(Severity, "Critical", StringComparison.OrdinalIgnoreCase);

    /// <summary>The critical faults, or all of them when none were singled out.</summary>
    public string CriticalSummary =>
        CriticalIssues is { Count: > 0 } ? string.Join(", ", CriticalIssues) : string.Join(", ", Issues);
}
