#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

// One line of the pre-trip inspection, as configured for the driver app.
//
// The dashboard reads these for their order and their weight. A submitted
// inspection is stored as jsonb, and Postgres orders jsonb keys by length rather
// than by how they were written, so the order a driver saw is only recoverable
// from this table.
[Table("checklist_items")]
public class ChecklistItem : BaseModel
{
    [PrimaryKey("item_id")]
    public int ItemId { get; set; }

    [Column("section_key")]
    public string SectionKey { get; set; }

    [Column("section_title")]
    public string SectionTitle { get; set; }

    [Column("label")]
    public string Label { get; set; }

    [Column("is_critical")]
    public bool IsCritical { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("active")]
    public bool Active { get; set; }
}
