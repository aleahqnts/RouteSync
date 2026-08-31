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
