using System.Security.Claims;
using System.Text.RegularExpressions;
using FleetWise.Models;
using FleetWise.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using static Postgrest.Constants;

namespace FleetWise.Controllers
{
    /// <summary>
    /// The signed-in person's own page, opened from their name at the foot of the rail.
    /// </summary>
    /// <remarks>
    /// The dashboard's counterpart of the driver app's profile: the account, personal
    /// details, a password change, and leave, with credits, requests and filing.
    ///
    /// Open to every role, since everyone has details to keep and leave to take. Only their
    /// own row is ever read or written: the account comes from the sign-in, never from the
    /// request. The email address and role are shown and not changed here. The email is
    /// what they sign in with and the role is what they may do, and both belong to an
    /// administrator on the Users page.
    ///
    /// Leave filed here joins the driver requests on the Requests page, marked as staff,
    /// and is decided there by anyone with access to it except the person who filed it.
    /// The rules for filing are the driver app's, from the same LeaveEntitlement.
    ///
    /// The forms post with fetch and are answered per field, so a mistake is marked where
    /// it was made. A save that succeeds reloads the page with a message, since what it
    /// changed shows in more than one place.
    /// </remarks>
    [Authorize]
    public class ProfileController : Controller
    {
        // The width of the name columns in the users table.
        private const int NameMax = 50;
        private const int TextMax = 200;
        private const int ReasonMax = 500;

        // Leave decisions, as the Requests page records them, for the figure on this page.
        private static readonly string[] DecisionActions =
        {
            "leave_approved", "leave_rejected", "leave_decided", "leave_revoked",
            "leave_withdrawal_accepted", "leave_withdrawal_declined",
        };

        private readonly Supabase.Client _supabase;
        private readonly AuditLog _audit;

        public ProfileController(Supabase.Client supabase, AuditLog audit)
        {
            _supabase = supabase;
            _audit = audit;
        }

        private int? Me() =>
            int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

        private async Task<UserModel?> ReadMeAsync(int id) =>
            (await _supabase.From<UserModel>()
                .Filter("user_id", Operator.Equals, id.ToString())
                .Get()).Models.FirstOrDefault();

        private async Task<List<LeaveRequest>> ReadLeaveAsync(int id) =>
            (await _supabase.From<LeaveRequest>()
                .Filter("user_id", Operator.Equals, id.ToString())
                .Order("filed_at", Ordering.Descending)
                .Get()).Models;

        // ---------------------------------------------------------------- page

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (Me() is not int id) return Challenge();

            var userTask = ReadMeAsync(id);
            var leaveTask = ReadLeaveAsync(id);
            var monthStart = new DateTime(PhClock.Now.Year, PhClock.Now.Month, 1);
            var since = Uri.EscapeDataString(
                new DateTimeOffset(monthStart, TimeSpan.FromHours(8)).ToString("o"));
            var actionsTask = _audit.QueryAsync(
                $"actor_id=eq.{id}&occurred_at=gte.{since}&limit=1");
            var decidedTask = _audit.QueryAsync(
                $"actor_id=eq.{id}&occurred_at=gte.{since}&action=in.({string.Join(",", DecisionActions)})&limit=1");
            var recentTask = _audit.QueryAsync(
                $"actor_id=eq.{id}&order=occurred_at.desc&limit=6");

            await Task.WhenAll(userTask, leaveTask, actionsTask, decidedTask, recentTask);

            var user = userTask.Result;
            if (user is null) return NotFound("Your account could not be found.");
            var leave = leaveTask.Result;
            var year = PhClock.OperationalDay.Year;

            var vm = new ProfilePageViewModel
            {
                FullName = AuthService.DisplayName(user.FirstName, user.MiddleName, user.LastName),
                Initials = Initials(user.FirstName, user.LastName),
                FirstName = user.FirstName ?? "",
                MiddleName = user.MiddleName ?? "",
                LastName = user.LastName ?? "",
                Email = user.EmailAddress ?? "",
                Role = User.FindFirstValue(ClaimTypes.Role) ?? "",
                Status = user.AccountStatus ?? "",
                MemberSince = PhClock.ToPh(new DateTimeOffset(user.CreatedAt)),
                LastSignIn = user.LastLogin is DateTime seen ? PhClock.ToPh(new DateTimeOffset(seen)) : null,
                ContactNumber = user.ContactNumber ?? "",
                Address = user.Address ?? "",
                EmergencyContactName = user.EmergencyContactName ?? "",
                EmergencyContactNumber = user.EmergencyContactNumber ?? "",

                // Null when the trail could not be read, so the page says so rather than
                // showing a month with nothing in it.
                ActionsThisMonth = actionsTask.Result.Rows is null ? null : actionsTask.Result.Total,
                DecisionsThisMonth = decidedTask.Result.Rows is null ? null : decidedTask.Result.Total,
                LeaveDaysTaken = LeaveEntitlement.Types
                    .Sum(t => LeaveEntitlement.Used(leave, t, year).Approved),
                Recent = recentTask.Result.Rows ?? new(),

                Year = year,
                Balances = LeaveEntitlement.Types.Select(t =>
                {
                    var used = LeaveEntitlement.Used(leave, t, year);
                    return new ProfileLeaveBalance(
                        t,
                        Math.Max(0, LeaveEntitlement.DaysPerYear[t] - used.Approved),
                        LeaveEntitlement.DaysPerYear[t],
                        used.Pending);
                }).ToList(),
                Requests = leave,
                Today = PhClock.OperationalDay.Date,
                Earliest = PhClock.OperationalDay.Date.AddDays(-LeaveEntitlement.BackdatingDays),
            };

            return View(vm);
        }

        private static string Initials(string? first, string? last)
        {
            var a = string.IsNullOrWhiteSpace(first) ? "" : first.Trim()[..1];
            var b = string.IsNullOrWhiteSpace(last) ? "" : last.Trim()[..1];
            var both = (a + b).ToUpperInvariant();
            return both.Length == 0 ? "?" : both;
        }

        // ---------------------------------------------------------------- details

        public sealed class DetailsInput
        {
            public string? FirstName { get; set; }
            public string? MiddleName { get; set; }
            public string? LastName { get; set; }
            public string? ContactNumber { get; set; }
            public string? Address { get; set; }
            public string? EmergencyContactName { get; set; }
            public string? EmergencyContactNumber { get; set; }
        }

        /// <summary>Saves the name and the personal details.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveDetails([FromForm] DetailsInput req)
        {
            if (Me() is not int id) return Unauthorized();

            var first = Clean(req.FirstName);
            var middle = Clean(req.MiddleName);
            var last = Clean(req.LastName);
            var address = Clean(req.Address);
            var emName = Clean(req.EmergencyContactName);
            var errors = new Dictionary<string, string>();

            if (first.Length == 0) errors["firstName"] = "Enter your first name.";
            else if (first.Length > NameMax) errors["firstName"] = $"Keep it to {NameMax} characters.";
            if (middle.Length > NameMax) errors["middleName"] = $"Keep it to {NameMax} characters.";
            if (last.Length == 0) errors["lastName"] = "Enter your last name.";
            else if (last.Length > NameMax) errors["lastName"] = $"Keep it to {NameMax} characters.";
            if (address.Length > TextMax) errors["address"] = $"Keep it to {TextMax} characters.";
            if (emName.Length > NameMax * 2) errors["emergencyContactName"] = $"Keep it to {NameMax * 2} characters.";

            var contact = Phone(req.ContactNumber, out var contactOk);
            if (!contactOk) errors["contactNumber"] = "Use the format 09XX XXX XXXX.";
            var emNumber = Phone(req.EmergencyContactNumber, out var emOk);
            if (!emOk) errors["emergencyContactNumber"] = "Use the format 09XX XXX XXXX.";

            if (errors.Count > 0) return BadRequest(new { errors });

            var user = await ReadMeAsync(id);
            if (user is null) return NotFound(new { errors = new { form = "Your account could not be found." } });

            var wasName = AuthService.DisplayName(user.FirstName, user.MiddleName, user.LastName);

            user.FirstName = first;
            user.MiddleName = middle.Length == 0 ? null : middle;
            user.LastName = last;
            user.ContactNumber = contact;
            user.Address = address.Length == 0 ? null : address;
            user.EmergencyContactName = emName.Length == 0 ? null : emName;
            user.EmergencyContactNumber = emNumber;
            user.UpdatedAt = PhClock.Now;

            await _supabase.From<UserModel>().Update(user);
            LiveAccount.Invalidate();

            var name = AuthService.DisplayName(user.FirstName, user.MiddleName, user.LastName);
            await _audit.WriteAsync("profile_updated",
                string.Equals(wasName, name, StringComparison.Ordinal)
                    ? "updated their profile details"
                    : $"updated their profile, changing their name from {wasName} to {name}",
                "users", id);

            TempData["ProfileSaved"] = "Your details were saved.";
            return Json(new { ok = true });
        }

        private static string Clean(string? s) => (s ?? "").Trim();

        /// <summary>
        /// A mobile number as the driver app keeps it, 09XX XXX XXXX, or null when blank.
        /// </summary>
        private static string? Phone(string? input, out bool valid)
        {
            var digits = new string((input ?? "").Where(char.IsDigit).ToArray());
            valid = digits.Length == 0 || (digits.Length == 11 && digits.StartsWith("09"));
            if (digits.Length == 0 || !valid) return null;
            return $"{digits[..4]} {digits[4..7]} {digits[7..]}";
        }

        // ---------------------------------------------------------------- password

        public sealed class PasswordInput
        {
            public string? CurrentPassword { get; set; }
            public string? NewPassword { get; set; }
            public string? ConfirmPassword { get; set; }
        }

        /// <summary>Changes the password, given the current one.</summary>
        /// <remarks>
        /// The current password is asked for so that a dashboard left signed in on a shared
        /// desk does not let whoever sits down next lock its owner out.
        /// </remarks>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangePassword([FromForm] PasswordInput req)
        {
            if (Me() is not int id) return Unauthorized();

            var next = req.NewPassword ?? "";
            var errors = new Dictionary<string, string>();

            if (string.IsNullOrEmpty(req.CurrentPassword))
                errors["currentPassword"] = "Enter your current password.";
            if (next.Length < PasswordPolicy.MinLength || next.Length > PasswordPolicy.MaxLength)
                errors["newPassword"] = PasswordPolicy.LengthMessage;
            else if (!Regex.IsMatch(next, PasswordPolicy.ComplexityPattern))
                errors["newPassword"] = PasswordPolicy.ComplexityMessage;
            else if (next == PasswordPolicy.TemporaryPassword)
                errors["newPassword"] = "Choose a password different from the temporary one.";
            if (req.ConfirmPassword != next)
                errors["confirmPassword"] = "Passwords do not match.";

            if (errors.Count > 0) return BadRequest(new { errors });

            var user = await ReadMeAsync(id);
            if (user is null) return NotFound(new { errors = new { form = "Your account could not be found." } });

            var hasher = new PasswordHasher<UserModel>();
            if (user.PasswordHash is null
                || hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword!)
                    == PasswordVerificationResult.Failed)
            {
                // Recorded, since a wrong current password here is someone at a signed-in
                // desk trying to take the account over, or its owner mistyping. The entry
                // cannot tell which; together they show a pattern.
                await _audit.WriteAsync("change_password",
                    "tried to change their own password with the wrong current password",
                    "users", id, outcome: "failed");
                return BadRequest(new { errors = new { currentPassword = "That is not your current password." } });
            }

            if (hasher.VerifyHashedPassword(user, user.PasswordHash, next) != PasswordVerificationResult.Failed)
                return BadRequest(new { errors = new { newPassword = "Choose a password different from your current one." } });

            user.PasswordHash = hasher.HashPassword(user, next);
            user.UpdatedAt = PhClock.Now;
            await _supabase.From<UserModel>().Update(user);

            await _audit.WriteAsync("change_password", "changed their own password", "users", id);

            TempData["ProfileSaved"] = "Your password was changed.";
            return Json(new { ok = true });
        }

        // ---------------------------------------------------------------- leave

        public sealed class LeaveInput
        {
            public string? LeaveType { get; set; }
            public DateTime? StartDate { get; set; }
            public DateTime? EndDate { get; set; }
            public string? Reason { get; set; }
        }

        /// <summary>Files a leave request, which waits on someone else's decision.</summary>
        /// <remarks>
        /// The driver app's checks, in its order: the dates, the type, how late it may be
        /// filed, days already asked for, and what is left of the allowance.
        /// </remarks>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> FileLeave([FromForm] LeaveInput req)
        {
            if (Me() is not int id) return Unauthorized();

            var type = LeaveEntitlement.Types.FirstOrDefault(t =>
                string.Equals(t, req.LeaveType, StringComparison.OrdinalIgnoreCase));
            var reason = Clean(req.Reason);

            if (type is null)
                return BadRequest(new { errors = new { leaveType = "Choose a leave type." } });
            if (req.StartDate is not DateTime startAt)
                return BadRequest(new { errors = new { startDate = "Choose the first day." } });
            if (req.EndDate is not DateTime endAt)
                return BadRequest(new { errors = new { endDate = "Choose the last day." } });

            var start = startAt.Date;
            var end = endAt.Date;
            if (end < start)
                return BadRequest(new { errors = new { endDate = "The end date is before the start date." } });
            if (reason.Length > ReasonMax)
                return BadRequest(new { errors = new { reason = $"Keep it to {ReasonMax} characters." } });

            var late = LeaveEntitlement.BackdatingProblem(type, start, PhClock.OperationalDay);
            if (late is not null)
                return BadRequest(new { errors = LeaveEntitlement.AllowsBackdating(type)
                    ? (object)new { startDate = late }
                    : new { leaveType = late } });

            var mine = await ReadLeaveAsync(id);

            // Days already asked for, of any type. Filing over them is not a second
            // request, it is the same days twice, and the balance is spent twice with it.
            var clash = mine.FirstOrDefault(r =>
                (LeaveEntitlement.IsOpen(r.Status)
                 || string.Equals(r.Status, "Approved", StringComparison.OrdinalIgnoreCase))
                && r.StartDate.Date <= end
                && r.EndDate.Date >= start);
            if (clash is not null)
                return BadRequest(new { errors = new { startDate =
                    string.Equals(clash.Status, "Approved", StringComparison.OrdinalIgnoreCase)
                        ? $"You already have approved {clash.LeaveType.ToLowerInvariant()} leave covering {Span(clash)}."
                        : $"You already have a request covering {Span(clash)} awaiting a decision." } });

            var days = (int)(end - start).TotalDays + 1;
            var left = LeaveEntitlement.Remaining(mine, type, start.Year);
            if (days > left)
                return BadRequest(new { errors = new { leaveType = left == 0
                    ? $"No {type.ToLowerInvariant()} leave left for {start.Year}."
                    : $"That is {days} days and you have {left} left." } });

            await _supabase.From<LeaveRequest>().Insert(new LeaveRequest
            {
                UserId = id,
                LeaveType = type,
                StartDate = start,
                EndDate = end,
                Reason = reason.Length == 0 ? null : reason,
                Status = "Pending",
                // The same instant the database default gives a driver's request.
                FiledAt = DateTime.UtcNow,
            });

            await _audit.WriteAsync("leave_filed",
                $"filed {type.ToLowerInvariant()} leave for {SpanOf(start, end)} ({days} {(days == 1 ? "day" : "days")})",
                "leave_requests", id);

            TempData["ProfileSaved"] = "Leave request sent. Someone with access to Requests will decide it.";
            return Json(new { ok = true });
        }

        /// <summary>Withdraws a request of one's own that has not been decided.</summary>
        /// <remarks>
        /// Filtered on status as well as id, so a request decided while this page was open
        /// is not withdrawn out from under the decision.
        /// </remarks>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelLeave(long requestId)
        {
            if (Me() is not int id) return Unauthorized();

            var changed = await _supabase.From<LeaveRequest>()
                .Filter("request_id", Operator.Equals, requestId.ToString())
                .Filter("user_id", Operator.Equals, id.ToString())
                .Filter("status", Operator.In, new List<object> { "Pending", "AwaitingChange" })
                .Set(x => x.Status, "Cancelled")
                .Set(x => x.DecidedAt, PhClock.Now)
                .Update();

            if (changed.Models.Count == 0)
                return BadRequest(new { errors = new { form = "That request could not be cancelled. It may already have been decided." } });

            await _audit.WriteAsync("leave_cancelled", "withdrew their own leave request",
                "leave_requests", requestId);

            TempData["ProfileSaved"] = "Leave request cancelled.";
            return Json(new { ok = true });
        }

        /// <summary>Asks for granted leave to be cancelled.</summary>
        /// <remarks>
        /// An ask, not an act, as for a driver: it lands on the Requests page, where someone
        /// else answers it. Only approved leave not yet begun and not already asked about.
        /// </remarks>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AskToCancel(long requestId, string? reason)
        {
            if (Me() is not int id) return Unauthorized();

            var why = Clean(reason);
            if (why.Length == 0)
                return BadRequest(new { errors = new { withdrawReason = "Say why, so whoever decides can weigh it." } });
            if (why.Length > ReasonMax)
                return BadRequest(new { errors = new { withdrawReason = $"Keep it to {ReasonMax} characters." } });

            var changed = await _supabase.From<LeaveRequest>()
                .Filter("request_id", Operator.Equals, requestId.ToString())
                .Filter("user_id", Operator.Equals, id.ToString())
                .Filter("status", Operator.Equals, "Approved")
                .Filter<object>("withdraw_requested_at", Operator.Is, null)
                .Filter("start_date", Operator.GreaterThanOrEqual, PhClock.OperationalDay.ToString("yyyy-MM-dd"))
                .Set(x => x.WithdrawRequestedAt, PhClock.Now)
                .Set(x => x.WithdrawReason, why)
                .Update();

            if (changed.Models.Count == 0)
                return BadRequest(new { errors = new { withdrawReason = "This leave can no longer be asked about. It may have started or already been asked about." } });

            await _audit.WriteAsync("leave_withdrawal_asked", "asked for their approved leave to be cancelled",
                "leave_requests", requestId);

            TempData["ProfileSaved"] = "Asked for this leave to be cancelled.";
            return Json(new { ok = true });
        }

        private static string Span(LeaveRequest r) => SpanOf(r.StartDate, r.EndDate);

        private static string SpanOf(DateTime start, DateTime end) =>
            start.Date == end.Date
                ? start.ToString("MMM d, yyyy")
                : start.Year == end.Year
                    ? $"{start:MMM d} to {end:MMM d, yyyy}"
                    : $"{start:MMM d, yyyy} to {end:MMM d, yyyy}";
    }
}
