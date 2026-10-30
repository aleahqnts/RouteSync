#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

// A request to be off, and what was decided about it.
//
// Separate from availability, which says whether the driver can work right now. This
// says whether they are rostered off on a given day, and it is the dispatcher who
// answers it.
[Table("leave_requests")]
public class LeaveRequest : BaseModel
{
    [PrimaryKey("request_id", false)]
    public long RequestId { get; set; }

    [Column("user_id")]
    public int UserId { get; set; }

    // Vacation, Sick or Emergency.
    [Column("leave_type")]
    public string LeaveType { get; set; }

    [Column("start_date")]
    public DateTime StartDate { get; set; }

    // The same as StartDate for a single day.
    [Column("end_date")]
    public DateTime EndDate { get; set; }

    [Column("reason")]
    public string Reason { get; set; }

    // Pending, Approved, Rejected or Cancelled.
    [Column("status")]
    public string Status { get; set; }

    // Days inside the range that have been handed back, as yyyy-MM-dd.
    //
    // Held as text rather than as dates. The column is `date[]`, and Postgres casts
    // `["2026-09-09"]` into it cleanly, but a DateTime through this client can move start_date onto the day before: it serialises with a time and an offset, and a
    // value with no timezone on it gets one applied on the way out. Keeping the wire
    // format to the plain day removes the question.
    //
    // Empty on almost every request. A leave revoked outright carries the status
    // Revoked instead, so this holds only the days pulled out of one that still stands.
    [Column("revoked_dates")]
    public List<string> RevokedDates { get; set; } = new();

    // When leave already granted was taken back, and by whom.
    //
    // Kept apart from the decision fields rather than overwriting them. The request's
    // history is read off this row, so writing a revocation into decided_at and
    // decision_note would replace the approval with the revocation and leave no record
    // that the leave was ever granted.
    //
    // Only the latest revocation is held. A leave pulled back twice keeps the second
    // here; every one of them is in the audit trail.
    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    [Column("revoked_by")]
    public int? RevokedBy { get; set; }

    [Column("revoke_note")]
    public string RevokeNote { get; set; }

    // When the driver asked for granted leave to be cancelled, and why.
    //
    // A mark on the request rather than a status of its own. Leave waiting on a
    // withdrawal decision is still granted: it counts against the allowance, shows on
    // the calendar and refuses an assignment, exactly as it did before the driver asked.
    // A new status would have to be taught to every one of those readers.
    //
    // Kept either way once answered; WithdrawAnsweredAt says it is settled.
    [Column("withdraw_requested_at")]
    public DateTime? WithdrawRequestedAt { get; set; }

    [Column("withdraw_reason")]
    public string WithdrawReason { get; set; }

    // When the dispatcher answered the driver's asking, either way.
    //
    // The asking is not cleared when it is answered. Clearing it would take the request out of
    // the queue and out of its own history at the same time, so a driver who asked and was answered would have no record that either happened. This says the question is
    // settled; the asking itself stays on the row.
    //
    // Only the latest asking is kept. A leave declined and asked about again keeps the
    // second; the audit trail keeps both.
    [Column("withdraw_answered_at")]
    public DateTime? WithdrawAnsweredAt { get; set; }

    // Who answered the driver's asking, and what they wrote back.
    //
    // Kept apart from the decision fields for the same reason as the revocation.
    // Accepting cancels leave that was approved, and writing the acceptance into the
    // decision fields would leave no record that it ever was.
    //
    // Who answered is read only to tell these rows from older ones, which carry an
    // acceptance in the decision fields instead; see
    // LeaveEntitlement.DecisionIsAcceptance. The driver is never shown it.
    [Column("withdraw_answered_by")]
    public int? WithdrawAnsweredBy { get; set; }

    [Column("withdraw_answer_note")]
    public string WithdrawAnswerNote { get; set; }

    [Column("filed_at")]
    public DateTime FiledAt { get; set; }

    [Column("decided_at")]
    public DateTime? DecidedAt { get; set; }

    [Column("decision_note")]
    public string DecisionNote { get; set; }
}

// What the company grants in a year, and the arithmetic over it.
//
// The same allowance for everyone, so a constant states it and the balance is worked
// out from the requests rather than stored. Kept identical to the dashboard's copy:
// the two must not be able to disagree about how many days a driver has left.
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

    // How far back leave may be filed, for the types that allow it.
    //
    // Long enough to cover a fortnight off followed by filing on the day of return, and
    // short enough that a reporting period is not reopened indefinitely.
    public const int BackdatingDays = 14;

    // Whether a type may be filed for a day already past.
    //
    // Sickness and emergencies are not known in advance, and a driver dealing with one
    // is not filing a form first, so both are filed afterwards as a matter of course.
    //
    // Vacation is planned by definition. Filed after the fact it is either a correction
    // to the record, which is a dispatcher's job through their own tools, or an
    // allowance being spent before it lapses.
    public static bool AllowsBackdating(string type) =>
        string.Equals(type, Sick, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, Emergency, StringComparison.OrdinalIgnoreCase);

    // Why this leave cannot be filed for the days it names, or null when it can.
    //
    // Only the first day is bounded. A leave running from last week into this one is one
    // absence, and where it ends says nothing about whether it could be foreseen.
    //
    // Asked of the operational day rather than the calendar one, so leave filed at two in
    // the morning is still filed on the service day that is running.
    //
    // The driver's app asks this before sending and the dashboard asks it again before
    // granting. The app posts its own rows, so the form is a courtesy and the decision is
    // the gate.
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

    // Whether a request is still waiting on an answer.
    //
    // AwaitingChange is an approval that has been begun and not finished: the dispatcher
    // pressed Approve, the driver turned out to be assigned on those days, and the
    // schedule has to be cleared before the approval can complete. Nothing has been
    // granted, so it counts against the allowance exactly as Pending does, and the driver
    // is shown the word Pending because from where they stand nothing has changed.
    public static bool IsOpen(string status) =>
        string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "AwaitingChange", StringComparison.OrdinalIgnoreCase);

    // Whether a request was cancelled by agreeing to the driver's asking.
    //
    // Granted leave the driver asked to hand back, and the dispatcher accepted. A request
    // the driver withdrew while it was still waiting is Cancelled too, but was never
    // asked about.
    public static bool AskAccepted(LeaveRequest r) =>
        string.Equals(r.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)
        && r.WithdrawRequestedAt is not null
        && r.WithdrawAnsweredAt is not null;

    // Whether the decision fields hold an accepted cancellation rather than the approval.
    //
    // An acceptance is written to the answer fields and leaves the approval where it is.
    // Rows accepted before the answer fields existed carry the acceptance over the
    // approval instead, and name no answerer. An approval always comes before the asking
    // it allowed, so a decision stamped at or after the asking is the acceptance.
    public static bool DecisionIsAcceptance(LeaveRequest r) =>
        AskAccepted(r)
        && r.WithdrawAnsweredBy is null
        && r.DecidedAt is DateTime decided
        && r.WithdrawRequestedAt is DateTime asked
        && decided >= asked;

    // Whether a day inside a request has been handed back.
    public static bool IsRevokedOn(LeaveRequest r, DateTime day) =>
        r.RevokedDates is { Count: > 0 }
        && r.RevokedDates.Contains(day.ToString("yyyy-MM-dd"));

    // Days a request actually covers, with any handed back taken off.
    //
    // An approved request does not mean every day from its start to its end: single days
    // can be handed back. The count and the per-day question both go through here rather
    // than being worked out again at each place that asks.
    public static int EffectiveDays(LeaveRequest r) =>
        Math.Max(0, Days(r) - (r.RevokedDates?.Count ?? 0));

    // Whether a request grants a driver the day off.
    //
    // Approved, covering the day, and that day not handed back. Anything else is not
    // leave for that day, whatever the range says.
    public static bool CoversDay(LeaveRequest r, DateTime day) =>
        string.Equals(r.Status, "Approved", StringComparison.OrdinalIgnoreCase)
        && r.StartDate.Date <= day.Date
        && r.EndDate.Date >= day.Date
        && !IsRevokedOn(r, day);

    // Days a request covers. A single day is a range of one.
    public static int Days(LeaveRequest r) =>
        (int)(r.EndDate.Date - r.StartDate.Date).TotalDays + 1;

    // Days of one type already spoken for in a year, counted apart for the ones granted
    // and the ones still waiting.
    //
    // Pending days count against what is left, or five more get filed while three sit
    // unanswered against a balance that is already spent.
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

    // What is left of one allowance, with pending days already taken off.
    public static int Remaining(IEnumerable<LeaveRequest> requests, string type, int year)
    {
        var (approved, pending) = Used(requests, type, year);
        return Math.Max(0, DaysPerYear[type] - approved - pending);
    }
}
