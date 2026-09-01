#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

[Table("driver_availability")]
public class DriverAvailability : BaseModel
{
    [PrimaryKey("user_id")]
    public int UserId { get; set; }

    [Column("availability_status")]
    public string AvailabilityStatus { get; set; }

    /// <summary>Why, in the driver's own words: sickness, an emergency, a fault with the bus.</summary>
    /// <remarks>
    /// The driver app has always asked for this and always written it. Reading it here is
    /// what lets the board say which of those it was, which is the difference between a
    /// dispatcher knowing to send a relief driver and knowing only that somebody stopped.
    /// </remarks>
    [Column("reason")]
    public string Reason { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
