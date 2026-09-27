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
    /// The signed-in person's own account, opened from their name at the foot of the rail.
    /// </summary>
    /// <remarks>
    /// Open to every role, since everyone has a name to correct and a password to change.
    /// Only their own row is ever read or written: the account comes from the sign-in, never
    /// from the request. The email address and role are shown and not changed here. The
    /// email is what they sign in with and the role is what they may do, and both belong to
    /// an administrator on the Users page.
    /// </remarks>
    [Authorize]
    public class ProfileController : Controller
    {
        // The width of the name columns in the users table.
        private const int NameMax = 50;

        private readonly Supabase.Client _supabase;
        private readonly AuditLog _audit;

        public ProfileController(Supabase.Client supabase, AuditLog audit)
        {
            _supabase = supabase;
            _audit = audit;
        }

        public sealed class ProfileUpdate
        {
            public string? FirstName { get; set; }
            public string? MiddleName { get; set; }
            public string? LastName { get; set; }
            public string? CurrentPassword { get; set; }
            public string? NewPassword { get; set; }
            public string? ConfirmPassword { get; set; }
        }

        private int? Me() =>
            int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

        private async Task<UserModel?> ReadMeAsync(int id) =>
            (await _supabase.From<UserModel>()
                .Filter("user_id", Operator.Equals, id.ToString())
                .Get()).Models.FirstOrDefault();

        /// <summary>What the dialog opens with.</summary>
        [HttpGet]
        public async Task<IActionResult> Details()
        {
            if (Me() is not int id) return Unauthorized();
            var user = await ReadMeAsync(id);
            if (user is null) return NotFound("Your account could not be found.");

            return Json(new
            {
                userId = user.UserId,
                firstName = user.FirstName ?? "",
                middleName = user.MiddleName ?? "",
                lastName = user.LastName ?? "",
                email = user.EmailAddress ?? "",
                role = User.FindFirstValue(ClaimTypes.Role) ?? "",
            });
        }

        /// <summary>Saves the name, and the password when a new one is given.</summary>
        /// <remarks>
        /// Every problem is returned at once, keyed by field, so the dialog can mark each
        /// field rather than stop at the first. A new password needs the current one: a
        /// dashboard left signed in on a shared desk must not let whoever sits down next
        /// lock its owner out.
        /// </remarks>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Update([FromForm] ProfileUpdate req)
        {
            if (Me() is not int id) return Unauthorized();

            var first = req.FirstName?.Trim() ?? "";
            var middle = req.MiddleName?.Trim() ?? "";
            var last = req.LastName?.Trim() ?? "";
            var errors = new Dictionary<string, string>();

            if (first.Length == 0) errors["firstName"] = "Enter your first name.";
            else if (first.Length > NameMax) errors["firstName"] = $"Keep it to {NameMax} characters.";
            if (middle.Length > NameMax) errors["middleName"] = $"Keep it to {NameMax} characters.";
            if (last.Length == 0) errors["lastName"] = "Enter your last name.";
            else if (last.Length > NameMax) errors["lastName"] = $"Keep it to {NameMax} characters.";

            var changingPassword = !string.IsNullOrEmpty(req.CurrentPassword)
                || !string.IsNullOrEmpty(req.NewPassword)
                || !string.IsNullOrEmpty(req.ConfirmPassword);

            if (changingPassword)
            {
                var next = req.NewPassword ?? "";
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
            }

            if (errors.Count > 0) return BadRequest(new { errors });

            var user = await ReadMeAsync(id);
            if (user is null) return NotFound(new { errors = new { form = "Your account could not be found." } });

            var hasher = new PasswordHasher<UserModel>();
            if (changingPassword)
            {
                if (user.PasswordHash is null
                    || hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword!)
                        == PasswordVerificationResult.Failed)
                {
                    // Recorded, since a wrong current password here is someone at a
                    // signed-in desk trying to take the account over, or its owner
                    // mistyping. The entry cannot tell which; together they show a pattern.
                    await _audit.WriteAsync("change_password",
                        "tried to change their own password with the wrong current password",
                        "users", id, outcome: "failed");
                    return BadRequest(new { errors = new { currentPassword = "That is not your current password." } });
                }

                if (hasher.VerifyHashedPassword(user, user.PasswordHash, req.NewPassword!)
                    != PasswordVerificationResult.Failed)
                    return BadRequest(new { errors = new { newPassword = "Choose a password different from your current one." } });
            }

            var wasName = AuthService.DisplayName(user.FirstName, user.MiddleName, user.LastName);

            user.FirstName = first;
            user.MiddleName = middle.Length == 0 ? null : middle;
            user.LastName = last;
            if (changingPassword) user.PasswordHash = hasher.HashPassword(user, req.NewPassword!);
            user.UpdatedAt = PhClock.Now;

            await _supabase.From<UserModel>().Update(user);
            LiveAccount.Invalidate();

            var name = AuthService.DisplayName(user.FirstName, user.MiddleName, user.LastName);
            if (!string.Equals(wasName, name, StringComparison.Ordinal))
                await _audit.WriteAsync("profile_updated",
                    $"changed their name from {wasName} to {name}", "users", id);
            if (changingPassword)
                await _audit.WriteAsync("change_password", "changed their own password", "users", id);

            return Json(new { name, passwordChanged = changingPassword });
        }
    }
}
