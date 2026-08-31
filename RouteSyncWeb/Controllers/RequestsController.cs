using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FleetWise.Models;
using FleetWise.Services;
using Postgrest;

namespace FleetWise.Controllers
{
    /// <summary>
    /// Leave filed by drivers, and the decisions made about it.
    /// </summary>
    /// <remarks>
    /// Gated on its own permission rather than riding on "users": deciding who is off is
    /// a different job from managing accounts, and a dispatcher who does the first has no
    /// business doing the second.
    ///
    /// BGC asks for three days' notice on a vacation and two hours on a sick call, and
    /// treats both as practice rather than a rule. Nothing here refuses a request for
    /// being late. The notice is recorded and shown, and the decision stays with the
    /// person making it.
    /// </remarks>
    [Authorize]
    [RequirePermission("requests")]
    public class RequestsController : Controller
    {
        private readonly Supabase.Client _supabase;
        private readonly AuditLog _audit;

        public RequestsController(Supabase.Client supabase, AuditLog audit)
        {
            _supabase = supabase;
            _audit = audit;
        }

        public async Task<IActionResult> Index(string? status)
        {
            // Pending first by default, because the queue is what this page is opened for.
            var wanted = string.IsNullOrWhiteSpace(status) ? "Pending" : status.Trim();

            var requestsTask = _supabase.From<LeaveRequest>().Get();
            var driversTask = _supabase.From<UserModel>()
                .Filter("role_id", Constants.Operator.Equals, "2")
                .Get();

            await Task.WhenAll(requestsTask, driversTask);

            var all = requestsTask.Result.Models;
            var names = driversTask.Result.Models
                .ToDictionary(u => u.UserId, u => $"{u.FirstName} {u.LastName}");

            var shown = string.Equals(wanted, "All", StringComparison.OrdinalIgnoreCase)
                ? all
                : all.Where(r => string.Equals(r.Status, wanted, StringComparison.OrdinalIgnoreCase)).ToList();

            var vm = new LeaveQueueViewModel
            {
                Status = wanted,
                PendingCount = all.Count(r => string.Equals(r.Status, "Pending", StringComparison.OrdinalIgnoreCase)),
                Rows = shown
                    // A queue is worked oldest first; history reads newest first. Filing
                    // order serves both better than the dates being asked for.
                    .OrderByDescending(r => r.FiledAt)
                    .Select(r => ToRow(r, names, all))
                    .ToList(),
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Decide(long requestId, string decision, string? note)
        {
            if (decision != "Approved" && decision != "Rejected")
                return BadRequest("That is not a decision.");

            var found = (await _supabase.From<LeaveRequest>()
                .Filter("request_id", Constants.Operator.Equals, requestId.ToString())
                .Get()).Models.FirstOrDefault();

            if (found is null) return NotFound();

            if (!string.Equals(found.Status, "Pending", StringComparison.OrdinalIgnoreCase))
                return BadRequest($"This request was already {found.Status.ToLowerInvariant()}.");

            var idStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var deciderId = int.TryParse(idStr, out var i) ? i : (int?)null;

            found.Status = decision;
            found.DecidedBy = deciderId;
            found.DecidedAt = PhClock.Now;
            found.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            await _supabase.From<LeaveRequest>().Update(found);

            await Notify(found, decision);

            await _audit.WriteAsync(
                decision == "Approved" ? "leave_approved" : "leave_rejected",
                $"{decision.ToLowerInvariant()} {found.LeaveType.ToLowerInvariant()} leave for driver "
                    + $"{found.UserId} covering {Span(found)}"
                    + (string.IsNullOrWhiteSpace(note) ? "" : $": {note.Trim()}"),
                "requests", requestId.ToString());

            return Ok();
        }

        /// <summary>
        /// Tells the driver what was decided, through the channel their app already reads.
        /// </summary>
        /// <remarks>
        /// The messages table drives the app's Notifications page, so a decision reaches
        /// the driver without a second notification path existing to be kept working.
        /// </remarks>
        private async Task Notify(LeaveRequest r, string decision)
        {
            var senderStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            await _supabase.From<Message>().Insert(new Message
            {
                SenderId = int.TryParse(senderStr, out var s) ? s : 0,
                TargetAudience = "driver",
                TargetId = r.UserId.ToString(),
                Subject = $"Leave {decision.ToLowerInvariant()}",
                Body = $"Your {r.LeaveType.ToLowerInvariant()} leave for {Span(r)} was "
                     + $"{decision.ToLowerInvariant()}."
                     + (string.IsNullOrWhiteSpace(r.DecisionNote) ? "" : $" {r.DecisionNote}"),
                Priority = "Normal",
                CreatedAt = PhClock.NowForDb,
            });
        }

        private static string Span(LeaveRequest r) =>
            r.StartDate.Date == r.EndDate.Date
                ? r.StartDate.ToString("MMM d, yyyy")
                : $"{r.StartDate:MMM d} to {r.EndDate:MMM d, yyyy}";

        private static LeaveRowViewModel ToRow(
            LeaveRequest r, IReadOnlyDictionary<int, string> names, List<LeaveRequest> all)
        {
            var mine = all.Where(x => x.UserId == r.UserId).ToList();

            // How much notice arrived with it. Shown as a fact, never as a pass or a fail:
            // the three days is advice, and reporting it as a violation would smuggle the
            // restriction back in through the wording.
            var notice = (r.StartDate.Date - r.FiledAt.Date).Days;

            return new LeaveRowViewModel
            {
                RequestId = r.RequestId,
                DriverId = r.UserId,
                DriverName = names.TryGetValue(r.UserId, out var n) ? n : $"Driver {r.UserId}",
                LeaveType = r.LeaveType,
                Span = Span(r),
                Days = LeaveEntitlement.Days(r),
                Reason = r.Reason,
                Status = r.Status,
                Filed = r.FiledAt.ToString("MMM d, yyyy h:mm tt"),
                NoticeDays = notice,
                RemainingOfType = LeaveEntitlement.Remaining(mine, r.LeaveType, r.StartDate.Year),
                EntitlementOfType = LeaveEntitlement.DaysPerYear.TryGetValue(r.LeaveType, out var e) ? e : 0,
                DecisionNote = r.DecisionNote,
            };
        }
    }
}
