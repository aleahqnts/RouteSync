#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

/// <summary>A month's roster: whether it is published, and the version a save is checked against.</summary>
/// <remarks>
/// Written only through save_roster_month, which bumps the version under a row lock. The
/// dashboard reads it here and never writes it directly.
/// </remarks>
[Table("roster_months")]
public class RosterMonth : BaseModel
{
    // A date column holding the first of the month; see UnzonedColumn.
    private DateTime _month;

    [PrimaryKey("month", false)]
    public DateTime Month { get => _month; set => _month = UnzonedColumn.Day(value); }

    /// <summary>Draft or Published.</summary>
    [Column("status")]
    public string Status { get; set; }

    [Column("version")]
    public int Version { get; set; }

    [Column("saved_at")]
    public DateTime? SavedAt { get; set; }

    [Column("saved_by")]
    public int? SavedBy { get; set; }

    [Column("published_at")]
    public DateTime? PublishedAt { get; set; }

    /// <summary>When the roster was carried forward from the month before.</summary>
    [Column("generated_at")]
    public DateTime? GeneratedAt { get; set; }

    /// <summary>Who carried it forward: a user id, or "system" for the monthly cycle.</summary>
    [Column("generated_by")]
    public string GeneratedBy { get; set; }
}

/// <summary>A driver's place in a month's roster: a crew seat on a bus shift, or a floater on a route.</summary>
[Table("roster_slots")]
public class RosterSlot : BaseModel
{
    private DateTime _month;

    [Column("month")]
    public DateTime Month { get => _month; set => _month = UnzonedColumn.Day(value); }

    [Column("driver_id")]
    public int DriverId { get; set; }

    /// <summary>Crew or Floater.</summary>
    [Column("kind")]
    public string Kind { get; set; }

    [Column("route_id")]
    public int RouteId { get; set; }

    /// <summary>The bus, for crew. Null for a floater.</summary>
    [Column("vehicle_id")]
    public string VehicleId { get; set; }

    /// <summary>Crew: the shift driven. Floater: the home shift.</summary>
    [Column("shift")]
    public string Shift { get; set; }

    /// <summary>ISO weekday, 1 Monday to 7 Sunday.</summary>
    [Column("rest_weekday")]
    public int RestWeekday { get; set; }
}
