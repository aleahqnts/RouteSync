#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

// A month whose roster has been published.
//
// A driver may read only published months, and only that they are published. Every day
// of such a month has been answered, so a day in it with no trip is a day off rather than
// a day nobody has planned yet.
[Table("roster_months")]
public class RosterMonth : BaseModel
{
    // The first of the month.
    [PrimaryKey("month", false)]
    public DateTime Month { get; set; }

    [Column("status")]
    public string Status { get; set; }
}

// The driver's own place in a published month's roster.
//
// Readable only for the signed-in driver, and only once the month is published, so a
// draft still being worked on never reaches a phone.
[Table("roster_slots")]
public class RosterSlot : BaseModel
{
    [Column("month")]
    public DateTime Month { get; set; }

    [Column("driver_id")]
    public int DriverId { get; set; }

    // Crew or Floater.
    [Column("kind")]
    public string Kind { get; set; }

    [Column("route_id")]
    public int RouteId { get; set; }

    // The bus, for crew. Null for a floater.
    [Column("vehicle_id")]
    public string VehicleId { get; set; }

    // Crew: the shift driven. Floater: the shift most often covered.
    [Column("shift")]
    public string Shift { get; set; }

    // ISO weekday, 1 Monday to 7 Sunday.
    [Column("rest_weekday")]
    public int RestWeekday { get; set; }

    public bool IsFloater => string.Equals(Kind, "Floater", StringComparison.OrdinalIgnoreCase);

    // The rest day's name, such as "Tuesday".
    public string RestDayName =>
        RestWeekday is >= 1 and <= 7 ? ((DayOfWeek)(RestWeekday % 7)).ToString() : "";
}
