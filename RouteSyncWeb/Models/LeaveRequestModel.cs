#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

/// <summary>A driver's request to be off, and what was decided about it.</summary>
/// <remarks>
/// Separate from <see cref="DriverAvailability"/> on purpose. That answers whether a
/// driver can work right now, which is what a sick call or a faulty bus produces. This
/// answers whether they are rostered off on a given day. Folding one into the other
/// would mean clearing an availability flag also erased a leave record.
/// </remarks>
[Table("leave_requests")]
public class LeaveRequest : BaseModel
{
    [PrimaryKey("request_id", false)]
    public long RequestId { get; set; }

    [Column("user_id")]
    public int UserId { get; set; }

    /// <summary>Vacation, Sick or Emergency.</summary>
    [Column("leave_type")]
    public string LeaveType { get; set; }

    [Column("start_date")]
    public DateTime StartDate { get; set; }

    /// <summary>The same as <see cref="StartDate"/> for a single day.</summary>
    [Column("end_date")]
    public DateTime EndDate { get; set; }

    [Column("reason")]
    public string Reason { get; set; }

    /// <summary>Pending, Approved, Rejected or Cancelled.</summary>
    [Column("status")]
    public string Status { get; set; }

    /// <summary>Days inside the range that have been handed back, as yyyy-MM-dd.</summary>
    /// <remarks>
    /// Held as text rather than as dates. The column is `date[]`, and Postgres casts
    /// `["2026-09-09"]` into it cleanly, but a DateTime through this client is what moved
    /// start_date onto the day before: it serialises with a time and an offset, and a
    /// value with no timezone on it gets one applied on the way out. Keeping the wire
    /// format to the plain day removes the question.
    ///
    /// Empty on almost every request. A leave revoked outright carries the status
    /// Revoked instead, so this holds only the days pulled out of one that still stands.
    /// </remarks>
    [Column("revoked_dates")]
    public List<string> RevokedDates { get; set; } = new();

    /// <summary>When leave already granted was taken back, and by whom.</summary>
    /// <remarks>
    /// Kept apart from the decision fields rather than overwriting them. The request's
    /// history is read off this row, so writing a revocation into decided_at and
    /// decision_note would replace the approval with the revocation and leave no record
    /// that the leave was ever granted.
    ///
    /// Only the latest revocation is held. A leave pulled back twice keeps the second
    /// here; every one of them is in the audit trail.
    /// </remarks>
    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    [Column("revoked_by")]
    public int? RevokedBy { get; set; }

    [Column("revoke_note")]
    public string RevokeNote { get; set; }

    /// <summary>When the driver asked for granted leave to be cancelled, and why.</summary>
    /// <remarks>
    /// A mark on the request rather than a status of its own. Leave waiting on a
    /// withdrawal decision is still granted: it counts against the allowance, shows on
    /// the calendar and refuses an assignment, exactly as it did before the driver asked.
    /// A new status would have had to be taught to every one of those readers.
    ///
    /// Cleared either way. Accepting cancels the request outright; declining leaves the
    /// leave standing with nothing to show it was ever questioned except the audit trail.
    /// </remarks>
    [Column("withdraw_requested_at")]
    public DateTime? WithdrawRequestedAt { get; set; }

    [Column("withdraw_reason")]
    public string WithdrawReason { get; set; }

    /// <summary>When the dispatcher answered the driver's asking, either way.</summary>
    /// <remarks>
    /// The asking is not cleared when it is answered. Clearing it took the request out of
    /// the queue and out of its own history at the same time, so a driver who asked and
    /// was answered had no record either of them ever happened. This says the question is
    /// settled; the asking itself stays on the row.
    ///
    /// Only the latest asking is kept. A leave declined and asked about again keeps the
    /// second; the audit trail keeps both.
    /// </remarks>
    [Column("withdraw_answered_at")]
    public DateTime? WithdrawAnsweredAt { get; set; }

    /// <summary>
    /// When notice arrived. BGC asks for three days on a vacation and two hours on a
    /// sick call, and treats both as practice rather than a gate, so this is recorded
    /// and never used to refuse a request.
    /// </summary>
    [Column("filed_at")]
    public DateTime FiledAt { get; set; }

    [Column("decided_by")]
    public int? DecidedBy { get; set; }

    [Column("decided_at")]
    public DateTime? DecidedAt { get; set; }

    [Column("decision_note")]
    public string DecisionNote { get; set; }
}

/// <summary>
/// What the company grants a driver in a year, and the arithmetic over it.
/// </summary>
/// <remarks>
/// Held here rather than in a table. BGC grants the same allowance to everyone, above
/// the DOLE minimum of five, so a constant states exactly what is true and a policy
/// change is one edit instead of a row per driver.
///
/// The balance is derived from the requests every time it is asked for. A stored
/// counter has one job and eventually fails at it: approve then cancel, reject after
/// approving, edit the dates, and the counter and the request list disagree with
/// nothing to say which is right.
/// </remarks>
public static class LeaveEntitlement
{
    public const string Vacation = "Vacation";
    public const string Sick = "Sick";
    public const string Emergency = "Emergency";

    public static readonly IReadOnlyDictionary<string, int> DaysPerYear =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [Vacation] = 12,
            [Sick] = 12,
            [Emergency] = 3,
        };

    public static readonly string[] Types = { Vacation, Sick, Emergency };

    public static bool IsType(string type) => DaysPerYear.ContainsKey(type ?? "");

    /// <summary>How far back leave may be filed, for the types that allow it.</summary>
    /// <remarks>
    /// Long enough to cover a fortnight off followed by filing on the day of return, and
    /// short enough that a reporting period is not reopened indefinitely.
    /// </remarks>
    public const int BackdatingDays = 14;

    /// <summary>Whether a type may be filed for a day already past.</summary>
    /// <remarks>
    /// Sickness and emergencies are not known in advance, and a driver dealing with one
    /// is not filing a form first, so both are filed afterwards as a matter of course.
    ///
    /// Vacation is planned by definition. Filed after the fact it is either a correction
    /// to the record, which is a dispatcher's job through their own tools, or an
    /// allowance being spent before it lapses.
    /// </remarks>
    public static bool AllowsBackdating(string type) =>
        string.Equals(type, Sick, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, Emergency, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why this leave cannot be filed for the days it names, or null when it can.
    /// </summary>
    /// <remarks>
    /// Only the first day is bounded. A leave running from last week into this one is one
    /// absence, and where it ends says nothing about whether it could be foreseen.
    ///
    /// Asked of the operational day rather than the calendar one, so leave filed at two in
    /// the morning is still filed on the service day that is running.
    ///
    /// The driver's app asks this before sending and the dashboard asks it again before
    /// granting. The app posts its own rows, so the form is a courtesy and the decision is
    /// the gate.
    /// </remarks>
    public static string BackdatingProblem(string type, DateTime start, DateTime today)
    {
        var days = (int)(today.Date - start.Date).TotalDays;
        if (days <= 0) return null;

        if (!AllowsBackdating(type))
            return $"{type} leave cannot be filed for a day that has passed.";

        if (days > BackdatingDays)
            return $"{type} leave can be filed up to {BackdatingDays} days late, and that is "
                 + $"{days} days ago.";

        return null;
    }

    /// <summary>
    /// Whether a request is still waiting on an answer.
    /// </summary>
    /// <remarks>
    /// AwaitingChange is an approval that has been begun and not finished: the dispatcher
    /// pressed Approve, the driver turned out to be assigned on those days, and the
    /// schedule has to be cleared before the approval can complete. Nothing has been
    /// granted, so it counts against the allowance exactly as Pending does, and the driver
    /// is shown the word Pending because from where they stand nothing has changed.
    /// </remarks>
    public static bool IsOpen(string status) =>
        string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "AwaitingChange", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a day inside a request has been handed back.</summary>
    public static bool IsRevokedOn(LeaveRequest r, DateTime day) =>
        r.RevokedDates is { Count: > 0 }
        && r.RevokedDates.Contains(day.ToString("yyyy-MM-dd"));

    /// <summary>
    /// Days a request actually covers, with any handed back taken off.
    /// </summary>
    /// <remarks>
    /// An approved request does not mean every day from its start to its end: single days
    /// can be handed back. The count and the per-day question both go through here rather
    /// than being worked out again at each place that asks.
    /// </remarks>
    public static int EffectiveDays(LeaveRequest r) =>
        Math.Max(0, Days(r) - (r.RevokedDates?.Count ?? 0));

    /// <summary>
    /// Whether a request grants a driver the day off.
    /// </summary>
    /// <remarks>
    /// Approved, covering the day, and that day not handed back. Anything else is not
    /// leave for that day, whatever the range says.
    /// </remarks>
    public static bool CoversDay(LeaveRequest r, DateTime day) =>
        string.Equals(r.Status, "Approved", StringComparison.OrdinalIgnoreCase)
        && r.StartDate.Date <= day.Date
        && r.EndDate.Date >= day.Date
        && !IsRevokedOn(r, day);

    /// <summary>Days a request covers. A single day is a range of one.</summary>
    public static int Days(LeaveRequest r) =>
        (int)(r.EndDate.Date - r.StartDate.Date).TotalDays + 1;

    /// <summary>
    /// Days of one type already spoken for in a year, counted separately for the ones
    /// granted and the ones still waiting.
    /// </summary>
    /// <remarks>
    /// Pending days count against what is left, or a driver files five more while three
    /// sit unanswered and reads a balance that is already spent. They are reported apart
    /// from approved days so the two can be told from each other.
    /// </remarks>
    public static (int Approved, int Pending) Used(
        IEnumerable<LeaveRequest> requests, string type, int year)
    {
        var mine = requests.Where(r =>
            string.Equals(r.LeaveType, type, StringComparison.OrdinalIgnoreCase)
            && r.StartDate.Year == year);

        return (
            mine.Where(r => string.Equals(r.Status, "Approved", StringComparison.OrdinalIgnoreCase)).Sum(EffectiveDays),
            mine.Where(r => IsOpen(r.Status)).Sum(Days));
    }

    /// <summary>What is left of one allowance, with pending days already taken off.</summary>
    public static int Remaining(IEnumerable<LeaveRequest> requests, string type, int year)
    {
        var (approved, pending) = Used(requests, type, year);
        return Math.Max(0, DaysPerYear[type] - approved - pending);
    }
}
