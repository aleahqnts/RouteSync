using SQLite;

namespace FleetWiseMobile.Models;

// Durable "trip ended" record. EndTrip enqueues this so the final boarded count
// + revenue reach Supabase even if the driver was offline when ending. Fixes
// trips.total_boarded drifting below the telemetry last-log (audit accuracy).
public class PendingTripFinalize
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public string TripId { get; set; } = "";
    public int TotalBoarded { get; set; }

    // The driver's manual correction at the moment the trip ended. Carried here
    // because a trip that ran offline may never have flushed it while it was running,
    // and this row is then the only record of it.
    public int BoardedAdjustment { get; set; }
    public decimal Revenue { get; set; }
    public DateTime EndTime { get; set; }

    // Needed so the finalize can also release the bus (vehicle_status On Trip ->
    // Ready to Deploy). Empty on rows queued before this field existed -> skipped.
    public string VehicleId { get; set; } = "";
}
