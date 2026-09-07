#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

[Table("trips")]
public class Trip : BaseModel
{
    [PrimaryKey("trip_id")]
    public string TripId { get; set; }

    [Column("date")]
    public DateTime Date { get; set; }

    [Column("shift_type")]
    public string ShiftType { get; set; }

    [Column("shift_start_time")]
    public TimeSpan ShiftStartTime { get; set; }

    [Column("shift_end_time")]
    public TimeSpan ShiftEndTime { get; set; }

    [Column("route_id")]
    public int RouteId { get; set; }

    [Column("vehicle_id")]
    public string VehicleId { get; set; }

    [Column("driver_id")]
    public int DriverId { get; set; }

    [Column("trip_status")]
    public string TripStatus { get; set; }

    [Column("estimated_revenue")]
    public decimal EstimatedRevenue { get; set; }

    // --- Added for the driver app (manual passenger counting) ---
    [Column("total_boarded")]
    public int TotalBoarded { get; set; }

    [Column("actual_start_time")]
    public DateTime? ActualStartTime { get; set; }

    [Column("actual_end_time")]
    public DateTime? ActualEndTime { get; set; }

    // Web added this column (demo telemetry simulator). Mapped here so reads of a trip
    // row stay in sync with the schema.
    [Column("is_simulated")]
    public bool IsSimulated { get; set; }

    // CameraCount Mobile stamps this (true UTC) every 5s while it is counting.
    // Fresh (<12s) -> camera owns total_boarded and the driver's manual counter hides.
    [Column("count_heartbeat")]
    public DateTime? CountHeartbeat { get; set; }

    // The high-water mark of the counts claimed by this app and by the counter phone.
    // total_boarded above is the reported figure, which is this plus the correction
    // below, so comparing a local count against it would read a driver's own
    // subtraction as a number still to be caught up with.
    [Column("boarded_counted")]
    public int BoardedCounted { get; set; }

    // The driver's manual correction, negative where the count is too high. Held
    // apart from the count so that a later camera reading cannot erase it.
    [Column("boarded_adjustment")]
    public int BoardedAdjustment { get; set; }
}
