#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

/// <summary>
/// A photograph of a failed inspection item.
/// </summary>
/// <remarks>
/// Evidence for a fault, never the decision about one. Nothing here is read when deciding
/// whether a bus is grounded.
///
/// It belongs to the inspection that produced it rather than to the maintenance line it
/// raised, because a fault reported again reopens the line it already has: one line
/// gathers sightings across its life, and a photograph held on the line would be
/// overwritten by each new one. What a mechanic wants from a fault that has come back is
/// whether it looks worse than last time.
/// </remarks>
[Table("inspection_photos")]
public class InspectionPhoto : BaseModel
{
    [PrimaryKey("photo_id")]
    public long PhotoId { get; set; }

    [Column("checklist_id")]
    public int ChecklistId { get; set; }

    /// <summary>The inspection item that failed, which is what a work order line matches on.</summary>
    [Column("checklist_item_id")]
    public long ChecklistItemId { get; set; }

    /// <summary>
    /// The order the fault was raised onto, carried so reading and ageing out need no walk
    /// through the vehicle.
    /// </summary>
    [Column("log_id")]
    public int? LogId { get; set; }

    /// <summary>
    /// Path within the private bucket. Null once the photograph has been swept, which is
    /// why a swept row survives: a fault nobody photographed and one whose photograph aged
    /// out are different facts, and a missing row could not tell them apart.
    /// </summary>
    [Column("object_key")]
    public string ObjectKey { get; set; }

    [Column("taken_at")]
    public DateTime TakenAt { get; set; }

    [Column("uploaded_at")]
    public DateTime UploadedAt { get; set; }

    [Column("swept_at")]
    public DateTime? SweptAt { get; set; }
}
