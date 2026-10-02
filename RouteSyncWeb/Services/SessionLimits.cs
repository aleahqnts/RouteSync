using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace FleetWise.Services
{
    /// <summary>
    /// How long a dashboard sign-in lasts.
    /// </summary>
    /// <remarks>
    /// A sign-in ends after <see cref="Idle"/> with no request from the browser, and in any
    /// case <see cref="Longest"/> after the password was typed. A page that refreshes itself
    /// keeps an open tab signed in, so a dispatcher is not sent to the sign-in page in the
    /// middle of a shift, but a browser closed and reopened later, or restored by its
    /// "continue where you left off" setting, asks for the password again.
    /// </remarks>
    public static class SessionLimits
    {
        public static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan Longest = TimeSpan.FromHours(12);

        /// <summary>When the password was typed, as Unix seconds.</summary>
        public const string SignedInClaim = "signed_in_at";

        /// <summary>
        /// Ends a sign-in older than <see cref="Longest"/>. A cookie written before the
        /// claim existed carries no time and is ended too.
        /// </summary>
        public static async Task ValidateAsync(CookieValidatePrincipalContext context)
        {
            var raw = context.Principal?.FindFirst(SignedInClaim)?.Value;
            if (long.TryParse(raw, out var seconds)
                && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds) < Longest)
                return;

            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
