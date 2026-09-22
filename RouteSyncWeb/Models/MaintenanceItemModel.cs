#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

/// <summary>
/// One fault being worked under a maintenance order.
/// </summary>
/// <remarks>
/// One row per fault however often it is reported, so each closes on its own and the list
/// describes the bus as it stands.
///
/// Criticality comes from checklist_items and decides whether the fault grounds the bus.
/// A hand-typed item carries none.
/// </remarks>
[Table("maintenance_items")]
public class MaintenanceItem : BaseModel
{
    [PrimaryKey("item_id")]
    public long ItemId { get; set; }

    [Column("log_id")]
    public int LogId { get; set; }

    [Column("label")]
    public string Label { get; set; }

    // The inspection item this line was raised by, when one was. Null for a line typed by
    // hand and for every line raised before it was recorded, both of which are recognised
    // by their label instead. The label stays authoritative for what the line says: it is
    // what somebody read on the day. This is what survives the wording changing.
    [Column("checklist_item_id")]
    public long? ChecklistItemId { get; set; }

    [Column("is_critical")]
    public bool IsCritical { get; set; }

    /// <summary>checklist when the fault names an inspected item, otherwise manual.</summary>
    [Column("source")]
    public string Source { get; set; }

    /// <summary>open, fixed, or dismissed.</summary>
    [Column("state")]
    public string State { get; set; }

    [Column("closed_at")]
    public DateTime? ClosedAt { get; set; }

    [Column("closed_by")]
    public string ClosedBy { get; set; }

    /// <summary>Required when dismissing, explaining why the fault was not real.</summary>
    [Column("note")]
    public string Note { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
