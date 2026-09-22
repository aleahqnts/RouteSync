using System.ComponentModel.DataAnnotations;
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
        private readonly SchedulingData _scheduling;
        private readonly TripAssignments _assignments;

        public RequestsController(
            Supabase.Client supabase, AuditLog audit, SchedulingData scheduling, TripAssignments assignments)
        {
            _supabase = supabase;
            _audit = audit;
            _scheduling = scheduling;
            _assignments = assignments;
        }

        /// <summary>A driver's asking that nobody has answered yet.</summary>
        /// <remarks>
        /// Only on leave that still stands. Leave revoked outright has nothing left to hand
        /// back, so an asking on it is settled whether or not it was answered, and holding it
        /// open would keep a request in the queue that neither answer can be given to.
        /// </remarks>
        private static bool IsAskOutstanding(LeaveRequest r) =>
            r.WithdrawRequestedAt is not null && r.WithdrawAnsweredAt is null
            && string.Equals(r.Status, "Approved", StringComparison.OrdinalIgnoreCase);

        /// <summary>A short summary of everything the queue would show differently.</summary>
        /// <remarks>
        /// Rebuilding the page re-renders every row and the counts above them, which is
        /// more than a queue that changes a handful of times a day needs on a timer. This
        /// reads the requests alone, without the names and trips the rows are dressed
        /// with, and returns a fingerprint, so the page can be watched closely and rebuilt
        /// only when it would actually differ.
        ///
        /// Every field a row shows or a decision writes is included, so filing, deciding,
        /// withdrawing and revoking all move it. The status tab is not: it selects which
        /// requests are on screen, and a request leaving the current tab is a change to
        /// the fingerprint anyway.
        /// </remarks>
        [HttpGet]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> Pulse()
        {
            var requests = await _supabase.From<LeaveRequest>()
                .Select("request_id,user_id,status,leave_type,start_date,end_date," +
                        "revoked_at,revoked_dates,withdraw_requested_at")
                .Get();

            // Ordered before it is joined: PostgREST makes no promise about the order rows
            // come back in, and an unordered fingerprint would change on its own.
            var parts = requests.Models
                .OrderBy(r => r.RequestId)
                .Select(r => string.Join("|",
                    r.RequestId, r.UserId, r.Status, r.LeaveType,
                    r.StartDate.ToString("yyyy-MM-dd"), r.EndDate.ToString("yyyy-MM-dd"),
                    r.RevokedAt?.Ticks ?? 0,
                    string.Join(",", r.RevokedDates ?? new()),
                    r.WithdrawRequestedAt?.Ticks ?? 0));

            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", parts)));
            return Json(new { pulse = Convert.ToHexString(hash) });
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

            // An approval part way through still belongs in the queue. It is work
            // somebody has started and not finished, and the one place it must not be
            // filed under is Approved, which it is not.
            // An approval part way through still belongs in the queue, and so does granted
            // leave the driver has asked to hand back: both are work somebody has to answer.
            // The one place either must not be filed is Approved, which the first is not and
            // the second only still is because nobody has decided yet.
            var shown = string.Equals(wanted, "All", StringComparison.OrdinalIgnoreCase)
                ? all
                : string.Equals(wanted, "Pending", StringComparison.OrdinalIgnoreCase)
                    ? all.Where(r => LeaveEntitlement.IsOpen(r.Status) || IsAskOutstanding(r)).ToList()
                    : all.Where(r => string.Equals(r.Status, wanted, StringComparison.OrdinalIgnoreCase)).ToList();

            var vm = new LeaveQueueViewModel
            {
                Status = wanted,
                PendingCount = all.Count(r => LeaveEntitlement.IsOpen(r.Status) || IsAskOutstanding(r)),
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

            var found = await FindAsync(requestId);
            if (found is null) return NotFound();

            if (!LeaveEntitlement.IsOpen(found.Status))
                return BadRequest($"This request was already {found.Status.ToLowerInvariant()}.");

            // Granting leave over days the driver is still booked to drive is what puts a
            // bus on the road with nobody in it: approving does not touch the schedule, so
            // the assignment survives the approval and the driver, told they are off, stays
            // home.
            //
            // So an approval is two steps. The first reports what is in the way and holds
            // the request. The dispatcher clears those shifts, either handing them to cover
            // drivers from here (CoverAndApprove) or in the planner, where a driver on leave
            // already highlights. The second runs this check again against the schedule as
            // it stands by then, and only a clean answer grants the leave.
            //
            // The check at the second step is what carries the safety, not the holding.
            // Nothing is believed from the first.
            if (decision == "Approved")
            {
                var late = await LateApprovalAsync(found);
                if (late is not null) return BadRequest(late);

                var blocking = await HoldIfBlockedAsync(found);
                if (blocking.Count > 0)
                    return Conflict(new { message = BlockingMessage(blocking.Count), blocking });
            }

            await RecordDecisionAsync(found, decision, note);
            return Ok();
        }

        /// <summary>
        /// The shifts holding an approval, each with the drivers who could take it over.
        /// </summary>
        /// <remarks>
        /// Gated on routes as well as requests, because what it leads to is a change to the
        /// schedule. A role that decides leave without running dispatch still sees the
        /// shifts in the way, and leaves clearing them to someone who does.
        ///
        /// A driver the dispatcher has already chosen for a shift is sent back as a pin,
        /// "TRIP000123:45", and kept: the shifts after it are ranked around the choice, and a
        /// choice that no longer fits says why rather than being replaced. A shift left
        /// uncovered for now is pinned to driver 0, "TRIP000123:0".
        /// </remarks>
        /// <param name="pin">Chosen drivers, each "tripId:driverId". Pins for trips not in the way are ignored.</param>
        [HttpGet]
        [RequirePermission("routes")]
        public async Task<IActionResult> CoverOptions(long requestId, [FromQuery] string[]? pin)
        {
            var found = await FindAsync(requestId);
            if (found is null) return NotFound();

            if (!LeaveEntitlement.IsOpen(found.Status))
                return BadRequest($"This request was already {found.Status.ToLowerInvariant()}.");

            var trips = await BlockingTripRowsAsync(found);
            if (trips.Count == 0) return Json(new { shifts = Array.Empty<object>() });

            var snapshot = SchedulingRules.AsIfGranted(
                await _scheduling.LoadAsync(trips.Min(t => t.Date), trips.Max(t => t.Date)), found);

            var pinned = new Dictionary<string, int>();
            foreach (var p in pin ?? Array.Empty<string>())
            {
                var parts = (p ?? "").Split(':');
                if (parts.Length == 2
                    && System.Text.RegularExpressions.Regex.IsMatch(parts[0], "^[A-Za-z0-9_-]{1,64}$")
                    && int.TryParse(parts[1], out var driverId) && driverId >= SchedulingRules.LeftForNow
                    && trips.Any(t => t.TripId == parts[0]))
                {
                    pinned[parts[0]] = driverId;
                }
            }

            return Json(new
            {
                shifts = SchedulingRules.SuggestCovers(trips, snapshot, pinned).Select(c =>
                {
                    var (start, end) = TripAssignments.ShiftWindow(c.Trip);
                    return new
                    {
                        tripId = c.Trip.TripId,
                        date = c.Trip.Date.ToString("ddd, MMM d"),
                        shift = c.Trip.ShiftType,
                        window = $"{start} to {end}",
                        route = snapshot.RouteNames.GetValueOrDefault(c.Trip.RouteId) ?? $"Route {c.Trip.RouteId}",
                        vehicleId = c.Trip.VehicleId,
                        week = MondayOf(c.Trip.Date),
                        suggested = c.Suggested?.DriverId,
                        pinned = c.PinnedDriverId,
                        pinnedProblem = c.PinnedProblem,
                        shortfall = c.Shortfall,
                        candidates = c.Candidates.Select(CandidateJson.Driver),
                    };
                }),
            });
        }

        /// <summary>A driver chosen to take over one shift.</summary>
        public sealed class CoverPickInput
        {
            [Required, RegularExpression(@"^[A-Za-z0-9_-]{1,64}$", ErrorMessage = "That is not a trip ID.")]
            public string TripId { get; set; } = "";

            [Range(1, int.MaxValue, ErrorMessage = "That is not a driver.")]
            public int DriverId { get; set; }
        }

        public sealed class CoverAndApproveInput
        {
            public long RequestId { get; set; }

            public List<CoverPickInput> Covers { get; set; } = new();

            [StringLength(300)]
            public string? Note { get; set; }
        }

        /// <summary>
        /// Hands the shifts in the way to the drivers chosen for them, then approves the
        /// leave if nothing is left in the way.
        /// </summary>
        /// <remarks>
        /// <para>Each cover goes through the same reassignment the dispatch board makes,
        /// with its checks, its conflict gate and its audit row. The approval is then the
        /// same check a plain approval runs, against the schedule as it stands once the
        /// covers are saved. Nothing about the first step is believed by the second.</para>
        ///
        /// <para>Only a cover at no cost worth a confirm is saved here, re-ranked on the
        /// server whatever the page sent. A cover that fails, or a shift left without one,
        /// leaves the request held with the shifts still in the way listed. Covers that
        /// did save stay saved: each is a sound reassignment on its own, and the leave is
        /// never granted while a shift is still uncovered.</para>
        /// </remarks>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequirePermission("routes")]
        public async Task<IActionResult> CoverAndApprove([FromBody] CoverAndApproveInput req)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState.FirstError());
            if (req is null) return BadRequest("Nothing was sent.");

            var found = await FindAsync(req.RequestId);
            if (found is null) return NotFound();

            if (!LeaveEntitlement.IsOpen(found.Status))
                return BadRequest($"This request was already {found.Status.ToLowerInvariant()}.");

            // Before any shift is touched. Leave that cannot be granted must not move
            // another driver onto a shift first.
            var late = await LateApprovalAsync(found);
            if (late is not null) return BadRequest(late);

            var trips = await BlockingTripRowsAsync(found);
            var failures = new List<string>();
            var covered = 0;

            if (trips.Count > 0)
            {
                var snapshot = SchedulingRules.AsIfGranted(
                    await _scheduling.LoadAsync(trips.Min(t => t.Date), trips.Max(t => t.Date)), found);
                var senderId = SenderId() ?? 0;

                foreach (var trip in trips)
                {
                    // Only a shift actually in the way. This is not a route for moving any
                    // trip at all under cover of approving leave.
                    var pick = req.Covers.FirstOrDefault(c => c.TripId == trip.TripId);
                    if (pick is null) continue;

                    var refusal = SchedulingRules.CoverRefusal(trip, pick.DriverId, snapshot);
                    if (refusal is not null)
                    {
                        failures.Add(refusal);
                        continue;
                    }

                    var result = await _assignments.ReassignAsync(
                        new ReassignChange(trip.TripId, pick.DriverId, null, null, Override: false),
                        senderId,
                        snapshot,
                        purpose: $"covering leave request {found.RequestId}",
                        syncStatuses: false,
                        screen: PickScreen.Cover);

                    if (result.Outcome != ReassignOutcome.Done)
                    {
                        failures.Add($"{trip.Date:MMM d}, {trip.ShiftType} shift: {result.Message}");
                        continue;
                    }

                    covered++;
                    snapshot = SchedulingRules.WithDriver(snapshot, trip.TripId, pick.DriverId);

                    // Best effort, and after the cover is saved, like every other notice.
                    try { await _assignments.NotifyNewShiftAsync(result.Trip!, senderId); }
                    catch (Exception ex)
                    {
                        await _audit.WriteAsync("trip_reassigned",
                            $"could not tell driver {pick.DriverId} they are covering trip {trip.TripId}: {ex.Message}",
                            "trips", trip.TripId, outcome: "failed");
                    }
                }

                if (covered > 0) await _assignments.SyncTripStatusesAsync();
            }

            var blocking = await HoldIfBlockedAsync(found);
            if (blocking.Count > 0)
                return Conflict(new { message = BlockingMessage(blocking.Count), blocking, failures, covered });

            await RecordDecisionAsync(found, "Approved", req.Note);
            return Ok(new { covered });
        }

        private async Task<LeaveRequest?> FindAsync(long requestId) =>
            (await _supabase.From<LeaveRequest>()
                .Filter("request_id", Constants.Operator.Equals, requestId.ToString())
                .Get()).Models.FirstOrDefault();

        private int? SenderId() =>
            int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id)
                ? id : null;

        private static string BlockingMessage(int count) =>
            count == 1
                ? "This driver is still assigned to a shift during this leave."
                : $"This driver is still assigned to {count} shifts during this leave.";

        /// <summary>Why this leave is filed too late to be granted, or null.</summary>
        /// <remarks>
        /// How late leave may be filed is decided here as well as in the driver's app. The
        /// app inserts its own rows under the driver's own token, so the form it fills in
        /// is a courtesy; granting the days is what spends the allowance, and this is the
        /// point that can refuse.
        ///
        /// Only approval is gated. A request that should never have been filed can still be
        /// rejected, which is how it leaves the queue.
        /// </remarks>
        private async Task<string?> LateApprovalAsync(LeaveRequest found)
        {
            var late = LeaveEntitlement.BackdatingProblem(
                found.LeaveType, found.StartDate, PhClock.OperationalDay);

            if (late is not null)
            {
                await _audit.WriteAsync("leave_decided",
                    $"could not approve {found.LeaveType.ToLowerInvariant()} leave for driver "
                        + $"{found.UserId} covering {Span(found)}: {late}",
                    "requests", found.RequestId.ToString(), outcome: "failed");
            }

            return late;
        }

        /// <summary>
        /// The shifts standing in the way of approving this leave, holding the request when
        /// there are any.
        /// </summary>
        private async Task<List<BlockingShift>> HoldIfBlockedAsync(LeaveRequest found)
        {
            var blocking = (await BlockingTripRowsAsync(found)).Select(ToBlockingShift).ToList();
            if (blocking.Count == 0) return blocking;

            if (!string.Equals(found.Status, "AwaitingChange", StringComparison.OrdinalIgnoreCase))
            {
                await _supabase.From<LeaveRequest>()
                    .Filter("request_id", Constants.Operator.Equals, found.RequestId.ToString())
                    .Set(x => x.Status, "AwaitingChange")
                    .Update();

                await _audit.WriteAsync("leave_approval_held",
                    $"began approving {found.LeaveType.ToLowerInvariant()} leave for driver "
                        + $"{found.UserId} covering {Span(found)}, held because they are still "
                        + $"assigned on {blocking.Count} {(blocking.Count == 1 ? "shift" : "shifts")}",
                    "requests", found.RequestId.ToString());

                found.Status = "AwaitingChange";
            }

            return blocking;
        }

        /// <summary>Stores a decision on a request still waiting, and tells the driver.</summary>
        private async Task RecordDecisionAsync(LeaveRequest found, string decision, string? note)
        {
            var requestId = found.RequestId;
            var deciderId = SenderId();

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
        }

        /// <summary>
        /// Takes back leave already granted, whole or a day at a time.
        /// </summary>
        /// <remarks>
        /// The planner refuses to book a driver on approved leave, which without this
        /// would leave no way to put them back on a day they are off for. A driver who
        /// agrees to come in could not be assigned at all, and the only move left would be
        /// editing the row by hand.
        ///
        /// The dispatcher's to make and not the driver's to refuse. The driver is told,
        /// and the days return to their allowance on their own, because the balance is
        /// derived from what is granted rather than counted into a column.
        ///
        /// Days already past are not offered: a day off that has been taken cannot be
        /// handed back.
        /// </remarks>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Revoke(long requestId, string? dates, string? note)
        {
            if (string.IsNullOrWhiteSpace(note))
                return BadRequest("Say why this leave was revoked. The driver is told the reason.");

            var found = (await _supabase.From<LeaveRequest>()
                .Filter("request_id", Constants.Operator.Equals, requestId.ToString())
                .Get()).Models.FirstOrDefault();

            if (found is null) return NotFound();

            if (!string.Equals(found.Status, "Approved", StringComparison.OrdinalIgnoreCase))
                return BadRequest($"Only approved leave can be revoked. This request is {found.Status.ToLowerInvariant()}.");

            // Every day the request still grants, which is what may be taken back.
            var open = new List<DateTime>();
            for (var d = found.StartDate.Date; d <= found.EndDate.Date; d = d.AddDays(1))
                if (!LeaveEntitlement.IsRevokedOn(found, d) && d >= PhClock.OperationalDay.Date)
                    open.Add(d);

            if (open.Count == 0)
                return BadRequest("There is nothing left to revoke on this request.");

            // No days named means the whole of what is left.
            var asked = (dates ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => DateTime.TryParse(s, out var d) ? d.Date : (DateTime?)null)
                .Where(d => d.HasValue).Select(d => d!.Value)
                .Distinct()
                .ToList();

            var taking = asked.Count == 0 ? open : asked.Where(open.Contains).ToList();

            if (taking.Count == 0)
                return BadRequest("Those days are not part of this leave, or have already been revoked.");

            var whole = taking.Count == open.Count;
            var idStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var by = int.TryParse(idStr, out var i) ? i : (int?)null;

            var write = _supabase.From<LeaveRequest>()
                .Filter("request_id", Constants.Operator.Equals, requestId.ToString())
                .Set(x => x.RevokedAt, PhClock.Now)
                .Set(x => x.RevokeNote, note.Trim());

            if (by.HasValue) write = write.Set(x => x.RevokedBy, by.Value);

            if (whole)
            {
                // Nothing of it stands, so the request itself is the thing revoked and the
                // day list has nothing left to say.
                write = write.Set(x => x.Status, "Revoked");

                // A cancellation the driver asked for is settled with it, since there is no
                // leave left to cancel. Stamped so the driver's app stops showing it waiting.
                if (IsAskOutstanding(found))
                {
                    write = write.Set(x => x.WithdrawAnsweredAt, PhClock.Now);
                    if (by.HasValue) write = write.Set(x => x.WithdrawAnsweredBy, by.Value);
                }
            }
            else
            {
                var kept = (found.RevokedDates ?? new List<string>())
                    .Concat(taking.Select(d => d.ToString("yyyy-MM-dd")))
                    .Distinct()
                    .OrderBy(s => s)
                    .ToList();

                write = write.Set(x => x.RevokedDates, kept);
            }

            await write.Update();

            try { await NotifyRevoked(found, taking, whole, note.Trim()); }
            catch (Exception ex)
            {
                await _audit.WriteAsync("leave_notice_failed",
                    $"could not notify driver {found.UserId} that leave on request {requestId} was revoked: {ex.Message}",
                    "requests", requestId.ToString(), outcome: "failed");
            }

            await _audit.WriteAsync("leave_revoked",
                $"revoked {(whole ? "all" : taking.Count.ToString())} "
                    + $"{(taking.Count == 1 ? "day" : "days")} of {found.LeaveType.ToLowerInvariant()} leave for driver "
                    + $"{found.UserId} covering {Span(found)}: "
                    + string.Join(", ", taking.OrderBy(d => d).Select(d => d.ToString("MMM d")))
                    + $". {note.Trim()}",
                "requests", requestId.ToString());

            return Ok();
        }

        /// <summary>Tells the driver which days were taken back, and why.</summary>
        private async Task NotifyRevoked(
            LeaveRequest r, List<DateTime> taken, bool whole, string note)
        {
            var senderStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            var which = whole
                ? $"your {r.LeaveType.ToLowerInvariant()} leave for {Span(r)}"
                : $"{taken.Count} {(taken.Count == 1 ? "day" : "days")} of your "
                  + $"{r.LeaveType.ToLowerInvariant()} leave: "
                  + string.Join(", ", taken.OrderBy(d => d).Select(d => d.ToString("MMM d")));

            await _supabase.From<Message>().Insert(new Message
            {
                SenderId = int.TryParse(senderStr, out var s) ? s : 0,
                TargetAudience = "Driver",
                TargetId = r.UserId.ToString(),
                Subject = "Leave revoked",
                Body = WithNote($"Dispatch has revoked {which}. Check your schedule.", note),
                Priority = "High",
                CreatedAt = PhClock.NowForDb,
            });
        }

        /// <summary>
        /// Answers a driver asking for granted leave back.
        /// </summary>
        /// <remarks>
        /// Accepting cancels the leave outright, which frees the days and staffs nothing:
        /// the week was planned around the absence, so the answer carries a reminder that
        /// those days now need a driver. It cannot make anybody do it, and it should not
        /// pretend the days filled themselves.
        ///
        /// Declining leaves the leave exactly as it was. Either way the driver is told, the
        /// row keeps who answered and what they wrote, and the audit trail keeps both the
        /// asking and the answer.
        ///
        /// Accepting changes the status and nothing of the approval. The approval stays in
        /// the decision fields, so the history reads approved, asked about, then cancelled.
        ///
        /// Whole only. Handing part of a leave back is Revoke, which is the dispatcher's.
        /// </remarks>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AnswerWithdrawal(long requestId, bool accept, string? note)
        {
            var found = (await _supabase.From<LeaveRequest>()
                .Filter("request_id", Constants.Operator.Equals, requestId.ToString())
                .Get()).Models.FirstOrDefault();

            if (found is null) return NotFound();

            if (!IsAskOutstanding(found))
                return BadRequest("No cancellation is waiting to be answered on this leave.");

            if (!string.Equals(found.Status, "Approved", StringComparison.OrdinalIgnoreCase))
                return BadRequest($"This request is {found.Status.ToLowerInvariant()}, so there is nothing to cancel.");

            var idStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var by = int.TryParse(idStr, out var i) ? i : (int?)null;

            var write = _supabase.From<LeaveRequest>()
                .Filter("request_id", Constants.Operator.Equals, requestId.ToString())
                // Stamped, not cleared. Clearing the asking took the request out of the
                // queue and out of its own history together, so neither the driver nor
                // anybody else could see it had been asked about at all.
                .Set(x => x.WithdrawAnsweredAt, PhClock.Now);

            if (by.HasValue) write = write.Set(x => x.WithdrawAnsweredBy, by.Value);
            if (!string.IsNullOrWhiteSpace(note)) write = write.Set(x => x.WithdrawAnswerNote, note.Trim());

            if (accept) write = write.Set(x => x.Status, "Cancelled");

            await write.Update();

            try { await NotifyWithdrawal(found, accept, note); }
            catch (Exception ex)
            {
                await _audit.WriteAsync("leave_notice_failed",
                    $"could not tell driver {found.UserId} what was decided about cancelling request {requestId}: {ex.Message}",
                    "requests", requestId.ToString(), outcome: "failed");
            }

            await _audit.WriteAsync(
                accept ? "leave_withdrawal_accepted" : "leave_withdrawal_declined",
                $"{(accept ? "accepted" : "declined")} the driver's request to cancel "
                    + $"{found.LeaveType.ToLowerInvariant()} leave for driver {found.UserId} "
                    + $"covering {Span(found)}"
                    + (string.IsNullOrWhiteSpace(found.WithdrawReason) ? "" : $", asked because: {found.WithdrawReason}")
                    + (string.IsNullOrWhiteSpace(note) ? "" : $". {note.Trim()}")
                    + (accept ? ". Those days now have no driver assigned." : ""),
                "requests", requestId.ToString());

            return Ok(new
            {
                staffing = accept
                    ? $"{Span(found)} now has no driver. Check the schedule for those days."
                    : null,
            });
        }

        /// <summary>Tells the driver what was decided about their cancellation.</summary>
        private async Task NotifyWithdrawal(LeaveRequest r, bool accept, string? note)
        {
            var senderStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            var body = accept
                ? $"Your {r.LeaveType.ToLowerInvariant()} leave for {Span(r)} is cancelled. Check your schedule."
                : $"Your request to cancel your {r.LeaveType.ToLowerInvariant()} leave for {Span(r)} was "
                  + "declined. The leave stands.";

            await _supabase.From<Message>().Insert(new Message
            {
                SenderId = int.TryParse(senderStr, out var s) ? s : 0,
                TargetAudience = "Driver",
                TargetId = r.UserId.ToString(),
                Subject = accept ? "Leave cancelled" : "Leave cancellation declined",
                Body = WithNote(body, note),
                Priority = "Normal",
                CreatedAt = PhClock.NowForDb,
            });
        }

        /// <summary>One shift standing in the way of an approval.</summary>
        public sealed class BlockingShift
        {
            public string TripId { get; set; } = "";
            public string Date { get; set; } = "";
            public string Shift { get; set; } = "";

            /// <summary>The Monday of the week it sits in, for the link into the planner.</summary>
            public string Week { get; set; } = "";
        }

        /// <summary>
        /// Shifts the driver is still booked for inside the leave, which an approval must
        /// clear first.
        /// </summary>
        /// <remarks>
        /// Only shifts a dispatcher can still act on. A trip already running or driven is
        /// left out, and so is one whose shift has finished: the planner will not rewrite
        /// either, so holding an approval for them would demand a change that cannot be
        /// made and leave the request unanswerable in both directions.
        ///
        /// This is what lets leave be filed for a day already past. The shift on it has
        /// finished, so it asks nothing of the schedule and blocks nothing.
        /// </remarks>
        private async Task<List<Trip>> BlockingTripRowsAsync(LeaveRequest r)
        {
            var trips = await _supabase.From<Trip>()
                .Filter("driver_id", Constants.Operator.Equals, r.UserId.ToString())
                .Filter("date", Constants.Operator.GreaterThanOrEqual, r.StartDate.ToString("yyyy-MM-dd"))
                .Filter("date", Constants.Operator.LessThanOrEqual, r.EndDate.ToString("yyyy-MM-dd"))
                .Get();

            var now = PhClock.Now;

            return trips.Models
                .Where(t => !TripStatus.Locked(t, now))
                // A day already handed back is not leave, so a shift on it blocks nothing.
                .Where(t => !LeaveEntitlement.IsRevokedOn(r, t.Date))
                .OrderBy(t => t.Date).ThenBy(t => t.ShiftStartTime)
                .ToList();
        }

        private static BlockingShift ToBlockingShift(Trip t) => new()
        {
            TripId = t.TripId,
            Date = t.Date.ToString("MMM d"),
            Shift = t.ShiftType,
            Week = MondayOf(t.Date),
        };

        private static string MondayOf(DateTime day) =>
            day.AddDays(-(((int)day.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd");

        /// <summary>
        /// Puts a held approval back in the queue.
        /// </summary>
        /// <remarks>
        /// An approval begun and abandoned would otherwise sit in AwaitingChange for ever,
        /// with the driver reading Pending and nobody looking at it. This is the way out
        /// that does not require deciding it.
        /// </remarks>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReleaseHold(long requestId)
        {
            var found = (await _supabase.From<LeaveRequest>()
                .Filter("request_id", Constants.Operator.Equals, requestId.ToString())
                .Get()).Models.FirstOrDefault();

            if (found is null) return NotFound();

            if (!string.Equals(found.Status, "AwaitingChange", StringComparison.OrdinalIgnoreCase))
                return BadRequest("This request is not waiting on a schedule change.");

            await _supabase.From<LeaveRequest>()
                .Filter("request_id", Constants.Operator.Equals, requestId.ToString())
                .Set(x => x.Status, "Pending")
                .Update();

            await _audit.WriteAsync("leave_approval_released",
                $"put the approval of {found.LeaveType.ToLowerInvariant()} leave for driver "
                    + $"{found.UserId} covering {Span(found)} back in the queue",
                "requests", requestId.ToString());

            return Ok();
        }

        /// <summary>
        /// A notice, and whatever the dispatcher wrote, kept apart.
        /// </summary>
        /// <remarks>
        /// Run together into one paragraph there was no telling where the system stopped
        /// speaking and a person started, so a note about the evening shift read as part
        /// of the decision itself. The blank line is what separates them, and the driver's
        /// app renders it.
        /// </remarks>
        private static string WithNote(string body, string? note) =>
            string.IsNullOrWhiteSpace(note) ? body : $"{body}\n\nFrom dispatch: {note.Trim()}";

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
                Body = WithNote(
                    $"Your {r.LeaveType.ToLowerInvariant()} leave for {Span(r)} was "
                        + $"{decision.ToLowerInvariant()}.",
                    r.DecisionNote),
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

            // The allowance as this request left it. Days granted on the same allowance
            // earlier in the year are taken off first, then this request itself when it
            // is one that spends days.
            //
            // Ordered by the day the leave starts, with the request number to settle two
            // that start together. Decisions are made in whatever order they are reached,
            // so ordering by them would have a ledger that jumps about; ordering by the
            // leave itself reads down the year.
            var ledger = mine
                .Where(x => string.Equals(x.LeaveType, r.LeaveType, StringComparison.OrdinalIgnoreCase)
                            && x.StartDate.Year == r.StartDate.Year);

            var granted = ledger
                .Where(x => string.Equals(x.Status, "Approved", StringComparison.OrdinalIgnoreCase)
                            && (x.StartDate.Date, x.RequestId).CompareTo((r.StartDate.Date, r.RequestId)) < 0)
                .Sum(LeaveEntitlement.EffectiveDays);

            // Refused and withdrawn requests spend nothing, so they leave the allowance
            // where the request before them left it.
            var spends = string.Equals(r.Status, "Approved", StringComparison.OrdinalIgnoreCase)
                         || LeaveEntitlement.IsOpen(r.Status);

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
                BalanceAfter = Math.Max(0, entitlement - granted - (spends ? LeaveEntitlement.EffectiveDays(r) : 0)),
                EntitlementOfType = entitlement,
                OtherPendingDays = Math.Max(0, used.Pending - LeaveEntitlement.Days(r)),
                DecisionNote = LeaveHistory.StatusNote(r),
                WithdrawAsked = IsAskOutstanding(r),
                WithdrawReason = r.WithdrawReason,
                WithdrawAskedWhen = r.WithdrawRequestedAt?.ToString("MMM d, yyyy h:mm tt"),
                RevokedCount = r.RevokedDates?.Count ?? 0,
                RevokableDays = RevokableDaysOf(r),
                History = LeaveHistory.Of(r, names),
            };
        }

        /// <summary>
        /// Days of an approved leave that could still be taken back.
        /// </summary>
        /// <remarks>
        /// Empty for anything not approved, and for approved leave whose days have all
        /// passed or already been revoked. The board draws Revoke only where this has
        /// something in it, so a request with nothing to take back offers no button.
        /// </remarks>
        private static List<LeaveDayOption> RevokableDaysOf(LeaveRequest r)
        {
            if (!string.Equals(r.Status, "Approved", StringComparison.OrdinalIgnoreCase))
                return new List<LeaveDayOption>();

            var days = new List<LeaveDayOption>();
            var today = PhClock.OperationalDay.Date;

            for (var d = r.StartDate.Date; d <= r.EndDate.Date; d = d.AddDays(1))
            {
                if (d < today) continue;
                if (LeaveEntitlement.IsRevokedOn(r, d)) continue;

                days.Add(new LeaveDayOption
                {
                    Iso = d.ToString("yyyy-MM-dd"),
                    Label = d.ToString("ddd, MMM d"),
                });
            }

            return days;
        }
    }
}
