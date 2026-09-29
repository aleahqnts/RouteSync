#nullable disable

using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

/// <summary>One doorway crossing the counter phone detected.</summary>
/// <remarks>
/// Evidence behind a trip's count, not the count itself: trips.total_boarded stays the
/// reported figure, and a driver's manual additions appear there and not here.
/// </remarks>
[Table("boarding_events")]
public class BoardingEvent : BaseModel
{
    [PrimaryKey("event_id")]
    public string EventId { get; set; }

    [Column("trip_id")]
    public string TripId { get; set; }

    /// <summary>"in" for a boarding, "out" for a crossing the counter excluded.</summary>
    [Column("direction")]
    public string Direction { get; set; }

    /// <summary>When the crossing happened, by the device clock, in true UTC.</summary>
    [Column("device_timestamp")]
    public DateTimeOffset DeviceTimestamp { get; set; }
}
