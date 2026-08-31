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
            mine.Where(r => string.Equals(r.Status, "Approved", StringComparison.OrdinalIgnoreCase)).Sum(Days),
            mine.Where(r => string.Equals(r.Status, "Pending", StringComparison.OrdinalIgnoreCase)).Sum(Days));
    }

    /// <summary>What is left of one allowance, with pending days already taken off.</summary>
    public static int Remaining(IEnumerable<LeaveRequest> requests, string type, int year)
    {
        var (approved, pending) = Used(requests, type, year);
        return Math.Max(0, DaysPerYear[type] - approved - pending);
    }
}
