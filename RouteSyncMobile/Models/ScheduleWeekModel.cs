#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

// A week the planner has saved.
//
// Written by the dashboard whenever a week is saved, and read here to tell a rest day
// from a week nobody has built yet. Both look the same from the phone: a driver may
// read only their own trips, so no rows for a week says either "you are not on it" or
// "it does not exist yet", and those mean opposite things to somebody deciding whether
// to book a doctor.
//
// Carries no driver, route or vehicle, so every driver may read the whole table.
[Table("schedule_weeks")]
public class ScheduleWeek : BaseModel
{
    // The Monday the week runs from.
    [PrimaryKey("week_start", false)]
    public DateTime WeekStart { get; set; }

    [Column("saved_at")]
    public DateTime SavedAt { get; set; }

    [Column("saved_by")]
    public int? SavedBy { get; set; }

    // Whether only a roster publish has written this week, never the planner.
    //
    // A publish marks every week it puts trips in, including the days of those weeks that
    // fall in the month next door. Such a row says nothing about those days, so it does not
    // count as the week being built.
    [Column("roster_only")]
    public bool RosterOnly { get; set; }
}
