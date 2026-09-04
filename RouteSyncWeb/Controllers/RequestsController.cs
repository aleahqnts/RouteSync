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

        /// <summary>A driver's asking that nobody has answered yet.</summary>
        private static bool IsAskOutstanding(LeaveRequest r) =>
            r.WithdrawRequestedAt is not null && r.WithdrawAnsweredAt is null;

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

            var found = (await _supabase.From<LeaveRequest>()
                .Filter("request_id", Constants.Operator.Equals, requestId.ToString())
                .Get()).Models.FirstOrDefault();

            if (found is null) return NotFound();

            if (!LeaveEntitlement.IsOpen(found.Status))
                return BadRequest($"This request was already {found.Status.ToLowerInvariant()}.");

            // Granting leave over days the driver is still booked to drive is what puts a
            // bus on the road with nobody in it: approving does not touch the schedule, so
            // the assignment survives the approval and the driver, told they are off, stays
            // home.
            //
            // So an approval is two steps. The first reports what is in the way and holds
            // the request. The dispatcher clears those shifts in the planner, where a
            // driver on leave already highlights. The second runs this check again against
            // the schedule as it stands by then, and only a clean answer grants the leave.
            //
            // The check at the second step is what carries the safety, not the holding.
            // Nothing is believed from the first.
            if (decision == "Approved")
            {
                // How late leave may be filed is decided here as well as in the driver's
                // app. The app inserts its own rows under the driver's own token, so the
                // form it fills in is a courtesy; granting the days is what spends the
                // allowance, and this is the point that can refuse.
                //
                // Only approval is gated. A request that should never have been filed can
                // still be rejected, which is how it leaves the queue.
                var late = LeaveEntitlement.BackdatingProblem(
                    found.LeaveType, found.StartDate, PhClock.OperationalDay);

                if (late is not null)
                {
                    await _audit.WriteAsync("leave_decided",
                        $"could not approve {found.LeaveType.ToLowerInvariant()} leave for driver "
                            + $"{found.UserId} covering {Span(found)}: {late}",
                        "requests", requestId.ToString(), outcome: "failed");

                    return BadRequest(late);
                }

                var blocking = await BlockingTripsAsync(found);
                if (blocking.Count > 0)
                {
                    if (!string.Equals(found.Status, "AwaitingChange", StringComparison.OrdinalIgnoreCase))
                    {
                        await _supabase.From<LeaveRequest>()
                            .Filter("request_id", Constants.Operator.Equals, requestId.ToString())
                            .Set(x => x.Status, "AwaitingChange")
                            .Update();

                        await _audit.WriteAsync("leave_approval_held",
                            $"began approving {found.LeaveType.ToLowerInvariant()} leave for driver "
                                + $"{found.UserId} covering {Span(found)}, held because they are still "
                                + $"assigned on {blocking.Count} {(blocking.Count == 1 ? "shift" : "shifts")}",
                            "requests", requestId.ToString());
                    }

                    return Conflict(new
                    {
                        message = blocking.Count == 1
                            ? "This driver is still assigned to a shift during this leave."
                            : $"This driver is still assigned to {blocking.Count} shifts during this leave.",
                        blocking,
                    });
                }
            }

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
        /// Declining clears the mark and leaves the leave exactly as it was. Either way
        /// the driver is told and the audit trail keeps both the asking and the answer.
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

            if (accept)
            {
                write = write
                    .Set(x => x.Status, "Cancelled")
                    .Set(x => x.DecidedAt, PhClock.Now)
                    .Set(x => x.DecisionNote,
                         string.IsNullOrWhiteSpace(note) ? "Cancelled at the driver's request." : note.Trim());

                if (by.HasValue) write = write.Set(x => x.DecidedBy, by.Value);
            }

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
        private async Task<List<BlockingShift>> BlockingTripsAsync(LeaveRequest r)
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
                .Select(t => new BlockingShift
                {
                    Date = t.Date.ToString("MMM d"),
                    Shift = t.ShiftType,
                    Week = t.Date.AddDays(-(((int)t.Date.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd"),
                })
                .ToList();
        }

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
                DecisionNote = r.DecisionNote,
                WithdrawAsked = r.WithdrawRequestedAt is not null && r.WithdrawAnsweredAt is null,
                WithdrawReason = r.WithdrawReason,
                WithdrawAskedWhen = r.WithdrawRequestedAt?.ToString("MMM d, yyyy h:mm tt"),
                RevokedCount = r.RevokedDates?.Count ?? 0,
                RevokableDays = RevokableDaysOf(r),
                History = HistoryOf(r, names),
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
                    At = r.FiledAt,
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
                    At = decided,
                    When = decided.ToString("MMM d, yyyy h:mm tt"),
                    // A withdrawal is the driver's own doing and carries no decider.
                    By = r.DecidedBy is null ? Who(r.UserId) : Who(r.DecidedBy),
                    Note = r.DecisionNote,
                });
            }

            if (r.WithdrawRequestedAt is DateTime asked)
            {
                events.Add(new LeaveEventViewModel
                {
                    Action = "Cancellation asked for",
                    At = asked,
                    When = asked.ToString("MMM d, yyyy h:mm tt"),
                    By = Who(r.UserId),
                    Note = r.WithdrawReason,
                });

                // Accepting cancels the request, which the decision below already reports.
                // Declining leaves the leave standing, and without this there would be
                // nothing to say the asking was ever answered.
                if (r.WithdrawAnsweredAt is DateTime answered
                    && string.Equals(r.Status, "Approved", StringComparison.OrdinalIgnoreCase))
                {
                    events.Add(new LeaveEventViewModel
                    {
                        Action = "Cancellation declined",
                        At = answered,
                        When = answered.ToString("MMM d, yyyy h:mm tt"),
                        By = Who(r.DecidedBy),
                    });
                }
            }

            // After the decision, not instead of it. Leave that was granted and then taken
            // back has two things that happened to it, and a history showing only the
            // second reads as though it was never granted.
            if (r.RevokedAt is DateTime revoked)
            {
                var which = r.RevokedDates is { Count: > 0 }
                    ? $"{r.RevokedDates.Count} {(r.RevokedDates.Count == 1 ? "day" : "days")} taken back"
                    : "All days taken back";

                events.Add(new LeaveEventViewModel
                {
                    Action = "Revoked",
                    At = revoked,
                    When = revoked.ToString("MMM d, yyyy h:mm tt"),
                    By = Who(r.RevokedBy),
                    Note = string.IsNullOrWhiteSpace(r.RevokeNote) ? which : $"{which}. {r.RevokeNote}",
                });
            }

            // In the order they happened, not the order they were assembled. OrderBy is
            // stable, so two stamped the same second keep the order they were added in.
            return events.OrderBy(e => e.At).ToList();
        }
    }
}
