#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

// One line of the pre-trip inspection, as configured on the dashboard.
//
// The list is read at inspection time rather than compiled into the app, so
// changing the wording of an item, or whether it blocks a trip, does not need a
// new build pushed to every phone.
[Table("checklist_items")]
public class ChecklistItem : BaseModel
{
    [PrimaryKey("item_id")]
    public int ItemId { get; set; }

    // Names the bus_checklist json column this item's result is stored under.
    [Column("section_key")]
    public string SectionKey { get; set; }

    [Column("section_title")]
    public string SectionTitle { get; set; }

    [Column("label")]
    public string Label { get; set; }

    // Critical items are the ones the bus cannot be driven safely or legally
    // without. Failing one blocks the trip; failing anything else is a defect.
    [Column("is_critical")]
    public bool IsCritical { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("active")]
    public bool Active { get; set; }
}
