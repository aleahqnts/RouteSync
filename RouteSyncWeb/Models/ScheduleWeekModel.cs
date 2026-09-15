#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

/// <summary>A week the planner has saved.</summary>
/// <remarks>
/// Written whenever a week is saved, and read by the driver app to tell a rest day from
/// a week nobody has built yet. A driver may read only their own trips, so no rows for a
/// week says either "you are not on it" or "it does not exist yet", and those mean
/// opposite things to somebody deciding whether to book a doctor.
///
/// Carries no driver, route or vehicle, so every driver may read the whole table.
/// </remarks>
[Table("schedule_weeks")]
public class ScheduleWeek : BaseModel
{
    private DateTime _weekStart;

    /// <summary>The Monday the week runs from.</summary>
    /// <remarks>A date column, normalized on the way in; see <see cref="UnzonedColumn"/>.</remarks>
    [PrimaryKey("week_start", false)]
    public DateTime WeekStart { get => _weekStart; set => _weekStart = UnzonedColumn.Day(value); }

    [Column("saved_at")]
    public DateTime SavedAt { get; set; }

    [Column("saved_by")]
    public int? SavedBy { get; set; }

    /// <summary>Whether only a roster publish has written this week, never the planner.</summary>
    /// <remarks>
    /// Kept by a trigger, which clears it on every write that is not a publish, so it is
    /// never sent from here.
    /// </remarks>
    [Column("roster_only", ignoreOnInsert: true, ignoreOnUpdate: true)]
    public bool RosterOnly { get; set; }
}
