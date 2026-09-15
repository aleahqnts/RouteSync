#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

[Table("trips")]
public class Trip : BaseModel
{
    [PrimaryKey("trip_id", shouldInsert: false)]
    public string TripId { get; set; }

    // A date column. Normalized on the way in so it is written as the same calendar day
    // whatever time zone the server runs in; see UnzonedColumn.
    private DateTime _date;

    [Column("date")]
    public DateTime Date { get => _date; set => _date = UnzonedColumn.Day(value); }

    [Column("shift_type")]
    public string ShiftType { get; set; }

    [Column("shift_start_time")]
    public TimeSpan ShiftStartTime { get; set; }

    [Column("shift_end_time")]
    public TimeSpan ShiftEndTime { get; set; }

    // Start of the one hour break, one of three slots inside the shift; see BreakSlots.
    // Null on trips written before breaks existed.
    [Column("break_start")]
    public TimeSpan? BreakStart { get; set; }

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

    // Cumulative passengers that boarded this trip (only ever grows). Drives the revenue
    // estimate so it never drops when passengers alight.
    [Column("total_boarded")]
    public int TotalBoarded { get; set; }

    // Set by the driver app when a trip starts. A null value on an active trip is what
    // marks it as one no phone ever drove.
    [Column("actual_start_time")]
    public DateTime? ActualStartTime { get; set; }

    [Column("actual_end_time")]
    public DateTime? ActualEndTime { get; set; }

    // Marks trips created by the retired telemetry simulator. The column stays because
    // TripReaperService still excludes such rows, so any left in the database are not
    // mistaken for the ghost trips it removes.
    [Column("is_simulated")]
    public bool IsSimulated { get; set; }
}