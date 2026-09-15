#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

/// <summary>Which roster wrote a trip, and whether it has been changed by hand since.</summary>
/// <remarks>
/// Read apart from <see cref="Trip"/>, so the trip model never writes either column: both
/// belong to publish_roster_month and the trigger that marks hand edits.
/// </remarks>
[Table("trips")]
public class TripRosterState : BaseModel
{
    [PrimaryKey("trip_id", false)]
    public string TripId { get; set; }

    private DateTime? _rosterMonth;

    [Column("roster_month")]
    public DateTime? RosterMonth { get => _rosterMonth; set => _rosterMonth = UnzonedColumn.Day(value); }

    [Column("hand_edited")]
    public bool HandEdited { get; set; }

    private DateTime _date;

    [Column("date")]
    public DateTime Date { get => _date; set => _date = UnzonedColumn.Day(value); }
}

/// <summary>A roster slot that must stay empty: a roster trip somebody deleted, or a bus not running that day.</summary>
[Table("roster_skips")]
public class RosterSkip : BaseModel
{
    private DateTime _date;
    private DateTime _month;

    [PrimaryKey("date", true)]
    public DateTime Date { get => _date; set => _date = UnzonedColumn.Day(value); }

    [PrimaryKey("vehicle_id", true)]
    public string VehicleId { get; set; }

    [PrimaryKey("shift", true)]
    public string Shift { get; set; }

    [Column("month")]
    public DateTime Month { get => _month; set => _month = UnzonedColumn.Day(value); }

    /// <summary>Deleted or Not running.</summary>
    [Column("reason")]
    public string Reason { get; set; }

    [Column("created_by")]
    public string CreatedBy { get; set; }
}

/// <summary>A roster slot the last publish could not fill, and why.</summary>
[Table("roster_gaps")]
public class RosterGap : BaseModel
{
    private DateTime _date;
    private DateTime _month;

    [Column("date")]
    public DateTime Date { get => _date; set => _date = UnzonedColumn.Day(value); }

    [Column("vehicle_id")]
    public string VehicleId { get; set; }

    [Column("shift")]
    public string Shift { get; set; }

    [Column("month")]
    public DateTime Month { get => _month; set => _month = UnzonedColumn.Day(value); }

    [Column("route_id")]
    public int? RouteId { get; set; }

    [Column("reason")]
    public string Reason { get; set; }
}
