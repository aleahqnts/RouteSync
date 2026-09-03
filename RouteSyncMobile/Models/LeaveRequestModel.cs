#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

/// <summary>A request to be off, and what was decided about it.</summary>
/// <remarks>
/// Separate from availability, which says whether the driver can work right now. This
/// says whether they are rostered off on a given day, and it is the dispatcher who
/// answers it.
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

    [Column("filed_at")]
    public DateTime FiledAt { get; set; }

    [Column("decided_at")]
    public DateTime? DecidedAt { get; set; }

    [Column("decision_note")]
    public string DecisionNote { get; set; }
}

/// <summary>
/// What the company grants in a year, and the arithmetic over it.
/// </summary>
/// <remarks>
/// The same allowance for everyone, so a constant states it and the balance is worked
/// out from the requests rather than stored. Kept identical to the dashboard's copy:
/// the two must not be able to disagree about how many days a driver has left.
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
    /// Every reader of leave used to be able to assume that an approved request meant
    /// every day from its start to its end. Partial revocation ends that, so the count
    /// and the per-day question both go through here rather than being worked out again
    /// at each place that asks.
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
    /// Days of one type already spoken for in a year, counted apart for the ones granted
    /// and the ones still waiting.
    /// </summary>
    /// <remarks>
    /// Pending days count against what is left, or five more get filed while three sit
    /// unanswered against a balance that is already spent.
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
