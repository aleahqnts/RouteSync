using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FleetWise.Services;

namespace FleetWise.Controllers
{
    // Takes, renews and lets go of an edit hold for the page that has a record open. The
    // page renews every 10 seconds while open and lets go when it closes.
    [Authorize]
    public class LocksController : Controller
    {
        // What can be held, and the section a caller needs to hold it.
        private static readonly Regex Key = new(@"^(trip:[A-Za-z0-9_-]{1,64}|user:\d{1,9})$");

        private readonly EditLocks _locks;

        public LocksController(EditLocks locks) => _locks = locks;

        // 200 when the caller has the hold. 409 with who has it and since when, or with
        // lapsed set when the caller's own hold ended for being idle.
        [HttpPost]
        public IActionResult Hold(string key, bool active)
        {
            if (!Allowed(key, out var me)) return BadRequest();

            var h = _locks.Take(key, me, User.Identity?.Name ?? "Someone", active, PhClock.Now);
            if (h is null) return Ok();
            if (h.UserId == me) return Conflict(new { lapsed = true });
            return Conflict(new { by = h.Name, since = EditLocks.Clock(h.Since) });
        }

        [HttpPost]
        public IActionResult Release(string key)
        {
            if (Allowed(key, out var me)) _locks.Release(key, me);
            return Ok();
        }

        private bool Allowed(string key, out int me)
        {
            int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out me);
            if (me <= 0 || key is null || !Key.IsMatch(key)) return false;
            return User.HasClaim("perm", key.StartsWith("trip:") ? "routes" : "users");
        }
    }
}
