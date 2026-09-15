#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

/// <summary>A month whose roster has been published.</summary>
/// <remarks>
/// A driver may read only published months, and only that they are published. Every day
/// of such a month has been answered, so a day in it with no trip is a day off rather than
/// a day nobody has planned yet.
/// </remarks>
[Table("roster_months")]
public class RosterMonth : BaseModel
{
    /// <summary>The first of the month.</summary>
    [PrimaryKey("month", false)]
    public DateTime Month { get; set; }

    [Column("status")]
    public string Status { get; set; }
}

/// <summary>The driver's own place in a published month's roster.</summary>
/// <remarks>
/// Readable only for the signed-in driver, and only once the month is published, so a
/// draft still being worked on never reaches a phone.
/// </remarks>
[Table("roster_slots")]
public class RosterSlot : BaseModel
{
    [Column("month")]
    public DateTime Month { get; set; }

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

    /// <summary>Crew: the shift driven. Floater: the shift most often covered.</summary>
    [Column("shift")]
    public string Shift { get; set; }

    /// <summary>ISO weekday, 1 Monday to 7 Sunday.</summary>
    [Column("rest_weekday")]
    public int RestWeekday { get; set; }

    public bool IsFloater => string.Equals(Kind, "Floater", StringComparison.OrdinalIgnoreCase);

    /// <summary>The rest day's name, such as "Tuesday".</summary>
    public string RestDayName =>
        RestWeekday is >= 1 and <= 7 ? ((DayOfWeek)(RestWeekday % 7)).ToString() : "";
}
