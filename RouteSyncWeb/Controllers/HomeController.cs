using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using FleetWise.Models;
using FleetWise.Services;

namespace FleetWise.Controllers
{
    [AllowAnonymous]
    public class HomeController : Controller
    {
        private readonly AuthService _authService;
        private readonly AuditLog _audit;
        private readonly LoginThrottle _throttle;
        private readonly PasswordResetApi _reset;

        public HomeController(AuthService authService, AuditLog audit, LoginThrottle throttle,
            PasswordResetApi reset)
        {
            _authService = authService;
            _audit = audit;
            _throttle = throttle;
            _reset = reset;
        }

        /// <summary>The sign-in page, or the dashboard for somebody already signed in.</summary>
        /// <remarks>
        /// Signing in redirects to the dashboard, which leaves the sign-in page one step
        /// back in the browser's history. Pressing back from the dashboard therefore
        /// landed on a sign-in form belonging to a session that is still open, which reads
        /// as having been signed out.
        ///
        /// Sending them on lands them where they already were, so back does nothing rather
        /// than something alarming. Leaving is what the sign-out button is for, and it asks
        /// first.
        /// </remarks>
        public IActionResult Index(int? throttled)
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToAction("Index", "Dashboard");

            if (throttled == 1)
                ModelState.AddModelError("", "Too many sign-in attempts. Wait a minute and try again.");
            return View();
        }

        [HttpPost]
        [EnableRateLimiting("login")]
        public async Task<IActionResult> Index(LoginViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            // Checked before the password is, so a blocked account costs no hashing work.
            if (_throttle.IsBlocked(model.Email))
            {
                await _audit.WriteSignInAsync("login_throttled",
                    $"Sign-in attempts paused for {Attempted(model.Email)} after repeated failures",
                    null, "denied");

                ModelState.AddModelError("", "Too many failed attempts for this account. Try again shortly.");
                return View(model);
            }

            var user = await _authService.ValidateAsync(model.Email, model.Password);
            if (user is null)
            {
                _throttle.RecordFailure(model.Email);

                // The edge functions never see dashboard sign-ins, so this is the only
                // place a failed attempt at it can be recorded. The typed email is kept,
                // which is the point of the entry, but length-capped. The password never is.
                await _audit.WriteSignInAsync("login_failed",
                    $"Failed dashboard sign-in for {Attempted(model.Email)}",
                    null, "denied");

                ModelState.AddModelError("", "Invalid email or password.");
                return View(model);
            }

            // The password has already been verified. If what they typed is the shared
            // temporary password then they have never set their own, so a claim marks the
            // session and they are routed to the change page. Middleware blocks the rest of
            // the app until that is done.
            _throttle.Clear(model.Email);

            var mustChange = model.Password == PasswordPolicy.TemporaryPassword;
            await SignInUserAsync(user, mustChange);

            await _audit.WriteSignInAsync("login",
                $"{user.FullName} ({user.Email}) signed in to the dashboard as {user.RoleName}"
                    + (mustChange ? ", still on the temporary password" : ""),
                user.UserId, role: user.RoleName);

            return mustChange
                ? RedirectToAction(nameof(ChangePassword))
                : RedirectToAction("Index", "Dashboard");
        }

        // ---------------------------------------------------------------------
        // Forgotten password: email a code, trade the code for a token, spend the
        // token on a new password. The work happens in the edge functions, which the
        // driver app uses too; these actions only move the user between the steps.
        //
        // Nothing here is kept in session, since the user has not signed in. The
        // address and then the reset token travel in hidden fields instead. The token
        // is short-lived and single use, so a copy of it is worth nothing later.
        // ---------------------------------------------------------------------

        [HttpGet]
        public IActionResult ForgotPassword() => View(new ForgotPasswordViewModel());

        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("login")]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var r = await _reset.RequestAsync(model.Email.Trim());
            if (r.Outcome == PasswordResetApi.Outcome.Unreachable)
            {
                ModelState.AddModelError("", "Could not reach the server. Try again in a moment.");
                return View(model);
            }
            // A rejection is a rate limit, never "no such account": the function answers
            // the same way for an address it has never seen. The next step is shown either
            // way rather than confirming who holds an account.
            if (r.Outcome == PasswordResetApi.Outcome.Denied)
            {
                ModelState.AddModelError("", r.Message ?? "Reset request rejected.");
                return View(model);
            }

            return View(nameof(VerifyResetCode), new VerifyResetCodeViewModel
            {
                Email = model.Email.Trim(),
                SentAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
        }

        /// <summary>
        /// A step of the reset reached without its form, such as by stepping back to
        /// it, restarts the flow rather than answering with an error.
        /// </summary>
        [HttpGet]
        public IActionResult VerifyResetCode() => RedirectToAction(nameof(ForgotPassword));

        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("login")]
        public async Task<IActionResult> VerifyResetCode(VerifyResetCodeViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var r = await _reset.VerifyAsync(model.Email, model.Code.Trim());
            if (r.Outcome == PasswordResetApi.Outcome.Unreachable)
            {
                ModelState.AddModelError("", "Could not reach the server. Try again in a moment.");
                return View(model);
            }
            if (r.Outcome == PasswordResetApi.Outcome.Denied)
            {
                ModelState.AddModelError("", r.Message ?? "That code is invalid or has expired.");
                return View(model);
            }

            return View(nameof(ResetPassword),
                new ResetPasswordViewModel { ResetToken = r.Token! });
        }

        /// <inheritdoc cref="VerifyResetCode()"/>
        [HttpGet]
        public IActionResult ResetPassword() => RedirectToAction(nameof(ForgotPassword));

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
        {
            if (model.NewPassword == PasswordPolicy.TemporaryPassword)
                ModelState.AddModelError(nameof(model.NewPassword),
                    "Choose a password different from the temporary one.");

            if (!ModelState.IsValid)
                return View(model);

            var r = await _reset.CompleteAsync(model.ResetToken, model.NewPassword);
            if (r.Outcome == PasswordResetApi.Outcome.Unreachable)
            {
                ModelState.AddModelError("", "Could not reach the server. Try again in a moment.");
                return View(model);
            }
            if (r.Outcome == PasswordResetApi.Outcome.Denied)
            {
                // The token is spent or has timed out, so the code cannot be reused and
                // the whole flow has to start again.
                ModelState.AddModelError("", r.Message ?? "Start the reset again.");
                return View(nameof(ForgotPassword), new ForgotPasswordViewModel());
            }

            // The reset issues no cookie, so the password is proved once more at sign-in.
            TempData["ResetDone"] = "Password updated. Sign in with your new password.";
            return RedirectToAction(nameof(Index));
        }

        [Authorize]
        [HttpGet]
        public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
        {
            if (model.NewPassword == PasswordPolicy.TemporaryPassword)
                ModelState.AddModelError(nameof(model.NewPassword), "Choose a password different from the temporary one.");

            if (!ModelState.IsValid)
                return View(model);

            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            await _authService.UpdatePasswordAsync(userId, model.NewPassword);

            // Pairs with the database trigger's own entry. That one proves the hash
            // changed; this one distinguishes the account holder changing their password
            // from an administrator resetting it.
            await _audit.WriteAsync("change_password", "changed their own password",
                "users", userId);

            // The cookie is reissued without the must-change claim, which unlocks the app.
            var authed = new AuthenticatedUser(
                userId,
                User.FindFirstValue(ClaimTypes.Name) ?? "",
                User.FindFirstValue(ClaimTypes.Email) ?? "",
                User.FindFirstValue(ClaimTypes.Role) ?? "",
                User.FindAll("perm").Select(c => c.Value).ToList());
            await SignInUserAsync(authed, mustChange: false);

            TempData["Success"] = "Password updated. Welcome aboard!";
            return RedirectToAction("Index", "Dashboard");
        }

        private async Task SignInUserAsync(AuthenticatedUser user, bool mustChange)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                new(ClaimTypes.Name, user.FullName),
                new(ClaimTypes.Email, user.Email),
                new(ClaimTypes.Role, user.RoleName),
            };
            if (mustChange)
                claims.Add(new Claim(PasswordPolicy.MustChangeClaim, "1"));

            // One permission claim per section the role may see. The sidebar reads them to
            // hide links, and the permission filter to block direct access.
            foreach (var p in user.Permissions ?? new List<string>())
                claims.Add(new Claim("perm", p));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        }

        [HttpPost]
        public async Task<IActionResult> Logout()
        {
            // Recorded before the cookie is cleared, while there is still an identity to
            // name. It closes the session in the timeline: everything between the sign-in
            // entry and this one was done by that person on that machine.
            if (User?.Identity?.IsAuthenticated == true)
                await _audit.WriteAsync("logout", "signed out of the dashboard");

            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Index", "Home");
        }

        /// <summary>
        /// Where UseExceptionHandler sends a request that faulted.
        /// </summary>
        /// <remarks>
        /// Without this the handler path resolves to nothing, the handler answers 404, and
        /// the browser shows its own blank page instead of the error view.
        ///
        /// The identifier shown is the one the server logs against the same request, so a
        /// report of a failure can be matched to the entry that describes it.
        /// </remarks>
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error() => View(new ErrorViewModel
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier,
        });

        /// <summary>
        /// The email as typed on a failed attempt. Untrusted input, so it is length-capped
        /// before being stored.
        /// </summary>
        private static string Attempted(string? email)
        {
            var e = (email ?? "").Trim();
            if (e.Length == 0) return "(no email)";
            return e.Length > 120 ? e[..120] : e;
        }
    }
}
