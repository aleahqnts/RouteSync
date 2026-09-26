#nullable disable

using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

[Table("telemetry_data")]
public class TelemetryData : BaseModel
{
    [PrimaryKey("telemetry_id")]
    public long TelemetryId { get; set; }

    [Column("trip_id")]
    public string TripId { get; set; }

    [Column("latitude")]
    public decimal Latitude { get; set; }

    [Column("longitude")]
    public decimal Longitude { get; set; }

    [Column("total_passengers")]
    public int TotalPassengers { get; set; }

    [Column("speed")]
    public decimal? Speed { get; set; }

    [Column("heading")]
    public float? Heading { get; set; }

    // Radius in metres the phone placed the fix within. Null from driver app builds that
    // do not send it.
    [Column("accuracy")]
    public float? Accuracy { get; set; }

    [Column("timestamp")]
    public DateTime Timestamp { get; set; }
}