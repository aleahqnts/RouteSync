using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace FleetWise.Services
{
    // How long a dashboard sign-in lasts.
    //
    // A sign-in ends after Idle with no request from the browser, and in any
    // case Longest after the password was typed. A page that refreshes itself
    // keeps an open tab signed in, so a dispatcher is not sent to the sign-in page in the
    // middle of a shift, but a browser closed and reopened later, or restored by its
    // "continue where you left off" setting, asks for the password again.
    public static class SessionLimits
    {
        public static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan Longest = TimeSpan.FromHours(12);

        // When the password was typed, as Unix seconds.
        public const string SignedInClaim = "signed_in_at";

        // Ends a sign-in older than Longest. A cookie written before the
        // claim existed carries no time and is ended too.
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
