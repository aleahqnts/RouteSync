#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

[Table("vehicles")]
public class Vehicle : BaseModel
{
    // Marked for insert because, unlike the generated keys elsewhere, this identifier is a
    // user-entered varchar with no DB default, so it must be included on Insert.
    // (PrimaryKey defaults to shouldInsert:false, which silently sent null → 23502.)
    [PrimaryKey("vehicle_id", true)]
    public string VehicleId { get; set; }

    [Column("plate_number")]
    public string PlateNumber { get; set; }

    // Note: the vehicle_type column was dropped from the DB (every unit is a bus), so it's no
    // longer modeled here.

    [Column("route_id")]
    public int? RouteId { get; set; }

    [Column("capacity")]
    public int Capacity { get; set; }

    [Column("vehicle_status")]
    public string VehicleStatus { get; set; }

    // Admin-set road-safety gate, independent of the volatile vehicle_status. When true the
    // bus is grounded -> dispatch won't let it be assigned. Mobile never writes this.
    [Column("out_of_service")]
    public bool OutOfService { get; set; }

    // The counter phone bound to this bus, claimed by the camera app when it binds.
    [Column("counter_device_id")]
    public string CounterDeviceId { get; set; }

    // A bus that has left the fleet for good. Null means it is still in service.
    // Kept apart from vehicle_status, which the next shift overwrites, and from
    // out_of_service, which means grounded today and expected back.
    [Column("retired_at")]
    public DateTime? RetiredAt { get; set; }

    [Column("retired_reason")]
    public string RetiredReason { get; set; }

    // A date column, rewritten by every whole-row save of a vehicle. Normalized on the way
    // in so a save never moves it; see UnzonedColumn.
    private DateTime? _lastMaintenanceDate;

    [Column("last_maintenance_date")]
    public DateTime? LastMaintenanceDate
    {
        get => _lastMaintenanceDate;
        set => _lastMaintenanceDate = UnzonedColumn.Day(value);
    }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}