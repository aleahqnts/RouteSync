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
            // Everyone, not only drivers: the history names whoever decided a request, and
            // that is an operator.
            var usersTask = _supabase.From<UserModel>().Get();

            await Task.WhenAll(requestsTask, usersTask);

            var all = requestsTask.Result.Models;
            var names = usersTask.Result.Models
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

            // Only the four columns a decision touches, named one at a time.
            //
            // Handing the whole model back rewrote start_date and end_date along with
            // everything else, and those are date columns holding a value the driver's app
            // wrote as plain text, so they come back with no timezone on them. Sent again
            // they were read as local and converted, which moved a request filed for the
            // second onto the first. The notice said the right day because it was built
            // from the model still in memory; only the stored row had moved.
            var write = _supabase.From<LeaveRequest>()
                .Filter("request_id", Constants.Operator.Equals, requestId.ToString())
                .Set(x => x.Status, found.Status)
                .Set(x => x.DecidedAt, found.DecidedAt!.Value);

            // Left out rather than written as null. Only a request still waiting is decided
            // here, so both of these start empty, and there is nothing to clear.
            if (found.DecidedBy.HasValue)
                write = write.Set(x => x.DecidedBy, found.DecidedBy.Value);
            if (found.DecisionNote is not null)
                write = write.Set(x => x.DecisionNote, found.DecisionNote);

            await write.Update();

            // Best effort, and after the decision is already stored. Telling the driver is
            // not the decision: a notice that fails must not report the whole thing failed
            // and leave the operator looking at a request the database has already settled.
            try { await Notify(found, decision); }
            catch (Exception ex)
            {
                await _audit.WriteAsync("leave_notice_failed",
                    $"could not notify driver {found.UserId} of the decision on request {requestId}: {ex.Message}",
                    "requests", requestId.ToString(), outcome: "failed");
            }

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
                // Capitalised: target_audience_enum is 'All', 'Route' or 'Driver', and
                // anything else is refused by the database rather than stored wrong.
                TargetAudience = "Driver",
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

            var used = LeaveEntitlement.Used(mine, r.LeaveType, r.StartDate.Year);
            var entitlement = LeaveEntitlement.DaysPerYear.TryGetValue(r.LeaveType, out var e) ? e : 0;

            return new LeaveRowViewModel
            {
                RequestId = r.RequestId,
                DriverId = r.UserId,
                DriverName = names.TryGetValue(r.UserId, out var n) ? n : $"Driver {r.UserId}",
                LeaveType = r.LeaveType,
                Span = Span(r),
                Start = r.StartDate.ToString("MMM d, yyyy"),
                End = r.EndDate.ToString("MMM d, yyyy"),
                Days = LeaveEntitlement.Days(r),
                Reason = r.Reason,
                Status = r.Status,
                Filed = r.FiledAt.ToString("MMM d, yyyy h:mm tt"),
                // What the driver has actually been granted, not what is left once this
                // request is taken off. Deducting a request from the balance shown beside
                // it makes the number move because of the very thing being decided, and
                // reads as though the days are already spent.
                //
                // The driver's own screen does deduct pending days, and should: there the
                // question is how much more can be asked for. Here it is what has been
                // agreed so far, with anything else outstanding named separately.
                RemainingOfType = Math.Max(0, entitlement - used.Approved),
                EntitlementOfType = entitlement,
                OtherPendingDays = Math.Max(0, used.Pending - LeaveEntitlement.Days(r)),
                DecisionNote = r.DecisionNote,
                History = HistoryOf(r, names),
            };
        }

        /// <summary>
        /// What happened to a request, read off the row rather than kept in a table of its
        /// own: a request is filed once and settled once, and both moments are stored.
        /// </summary>
        private static List<LeaveEventViewModel> HistoryOf(
            LeaveRequest r, IReadOnlyDictionary<int, string> names)
        {
            string Who(int? id) =>
                id is int i && names.TryGetValue(i, out var n) ? n : "Unknown";

            var events = new List<LeaveEventViewModel>
            {
                new()
                {
                    Action = "Filed",
                    When = r.FiledAt.ToString("MMM d, yyyy h:mm tt"),
                    By = Who(r.UserId),
                    Note = r.Reason,
                },
            };

            if (r.DecidedAt is DateTime decided
                && !string.Equals(r.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                events.Add(new LeaveEventViewModel
                {
                    Action = r.Status,
                    When = decided.ToString("MMM d, yyyy h:mm tt"),
                    // A withdrawal is the driver's own doing and carries no decider.
                    By = r.DecidedBy is null ? Who(r.UserId) : Who(r.DecidedBy),
                    Note = r.DecisionNote,
                });
            }

            return events;
        }
    }
}
