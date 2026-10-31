using FleetWise.Services;

namespace FleetWise.Models;

// What the Requests page works out from the requests themselves: how soon each waiting
// one starts, and who is out in a given week.
//
// Every "today" here is the service day (PhClock.OperationalDay), passed in so the rules
// can be checked against a fixed date.
public static class LeaveBoard
{
    // How soon a waiting request's leave starts, as its chip reads, and whether it is
    // marked urgent.
    //
    // Leave filed for a day already past is Retro. It is urgent as well: approval of
    // late leave closes after a review window (LeaveEntitlement.ApprovalProblem).
    public static (string Label, bool Urgent) Urgency(DateTime start, DateTime today)
    {
        var days = (int)(start.Date - today.Date).TotalDays;
        return days switch
        {
            < 0 => ("Retro", true),
            0 => ("Today", true),
            1 => ("Tomorrow", true),
            _ => ($"In {days} days", days <= 2),
        };
    }

    // Shifts still booked to the driver inside the leave that a dispatcher can act on.
    //
    // A trip already running or driven, or one whose shift has finished, is left out: the
    // planner will not rewrite either. A day already handed back is not leave, so a shift
    // on it blocks nothing.
    public static List<Trip> Blocking(LeaveRequest r, IEnumerable<Trip> trips, DateTime now) =>
        trips
            .Where(t => t.DriverId == r.UserId
                        && t.Date.Date >= r.StartDate.Date && t.Date.Date <= r.EndDate.Date)
            .Where(t => !TripStatus.Locked(t, now))
            .Where(t => !LeaveEntitlement.IsRevokedOn(r, t.Date))
            .OrderBy(t => t.Date).ThenBy(t => t.ShiftStartTime)
            .ToList();

    // The Monday of the week a day sits in.
    public static DateTime MondayOf(DateTime day) =>
        day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    // Who is off in the week starting on monday, one row per driver with any leave in it.
    //
    // Approved leave and leave still waiting are drawn; anything refused, withdrawn or
    // revoked is not, and nor is a single day handed back. Where a driver has both on one
    // day, the approved one is shown. Rows are ordered by the first day off, then by name.
    public static List<WeekRow> Week(
        IEnumerable<LeaveRequest> requests, DateTime monday, Func<int, string> name)
    {
        var rows = new Dictionary<int, WeekRow>();

        foreach (var r in requests)
        {
            var approved = string.Equals(r.Status, "Approved", StringComparison.OrdinalIgnoreCase);
            if (!approved && !LeaveEntitlement.IsOpen(r.Status)) continue;

            for (var i = 0; i < 7; i++)
            {
                var day = monday.Date.AddDays(i);
                if (day < r.StartDate.Date || day > r.EndDate.Date) continue;
                if (LeaveEntitlement.IsRevokedOn(r, day)) continue;

                if (!rows.TryGetValue(r.UserId, out var row))
                    rows[r.UserId] = row = new WeekRow { DriverId = r.UserId, Name = name(r.UserId) };

                var cell = row.Days[i];
                if (cell is null || (approved && !cell.Approved))
                    row.Days[i] = new WeekCell { Approved = approved, Type = r.LeaveType };
            }
        }

        return rows.Values
            .OrderBy(r => Array.FindIndex(r.Days, d => d is not null))
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Drivers off on each day of the week, waiting and approved together.
    public static int[] OutPerDay(IReadOnlyList<WeekRow> rows) =>
        Enumerable.Range(0, 7).Select(i => rows.Count(r => r.Days[i] is not null)).ToArray();

    // Items grouped by the month their leave starts in, newest month first. The order
    // inside each month is the order they came in.
    public static List<IGrouping<DateTime, T>> ByMonth<T>(IEnumerable<T> items, Func<T, DateTime> start) =>
        items
            .GroupBy(x => new DateTime(start(x).Year, start(x).Month, 1))
            .OrderByDescending(g => g.Key)
            .ToList();
}

// One driver's week on the Who's out board.
public sealed class WeekRow
{
    public int DriverId { get; set; }
    public string Name { get; set; } = "";

    // Monday to Sunday; null is a day not off.
    public WeekCell?[] Days { get; } = new WeekCell?[7];
}

public sealed class WeekCell
{
    // Granted, rather than still waiting on a decision.
    public bool Approved { get; set; }
    public string Type { get; set; } = "";
}
