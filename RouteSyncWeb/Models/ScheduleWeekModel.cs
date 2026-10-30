#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

// A week the planner has saved.
//
// Written whenever a week is saved, and read by the driver app to tell a rest day from
// a week nobody has built yet. A driver may read only their own trips, so no rows for a
// week says either "you are not on it" or "it does not exist yet", and those mean
// opposite things to somebody deciding whether to book a doctor.
//
// Carries no driver, route or vehicle, so every driver may read the whole table.
[Table("schedule_weeks")]
public class ScheduleWeek : BaseModel
{
    private DateTime _weekStart;

    // The Monday the week runs from.
    //
    // A date column, normalized on the way in; see UnzonedColumn.
    [PrimaryKey("week_start", false)]
    public DateTime WeekStart { get => _weekStart; set => _weekStart = UnzonedColumn.Day(value); }

    [Column("saved_at")]
    public DateTime SavedAt { get; set; }

    [Column("saved_by")]
    public int? SavedBy { get; set; }

    // Whether only a roster publish has written this week, never the planner.
    //
    // Kept by a trigger, which clears it on every write that is not a publish, so it is
    // never sent from here.
    [Column("roster_only", ignoreOnInsert: true, ignoreOnUpdate: true)]
    public bool RosterOnly { get; set; }
}
