using SQLite;

namespace FleetWiseMobile.Models;

// Local SQLite row buffered on the device. Pushed to Supabase `telemetry_data`
// by TelemetryQueue.FlushAsync, then deleted on success (dead-zone safe).
public class PendingTelemetry
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public string TripId { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int TotalPassengers { get; set; }
    public double? Speed { get; set; }
    public double? Heading { get; set; }

    // Radius in metres Android places the fix within. The fleet map will not move a bus on
    // a reading less accurate than 50 m. Rows queued by an earlier build read back as null,
    // which the map treats as usable.
    public double? Accuracy { get; set; }

    public DateTime Timestamp { get; set; }
}
