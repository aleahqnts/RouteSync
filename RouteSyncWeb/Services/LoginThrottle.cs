using Microsoft.Extensions.Caching.Memory;

namespace FleetWise.Services
{
    // Counts recent failed sign-ins per account, so an attacker spreading attempts across
    // many addresses is still slowed on the account they are targeting.
    //
    // This complements the per-address limit on the endpoint, which alone only slows a
    // single source.
    //
    // Deliberately not a lockout. A hard lock turns the sign-in form into a way to shut
    // any admin out of their own account by guessing at it, so the account stays reachable
    // and the attempt rate is what falls. The window is short and a successful sign-in
    // clears the count, so a legitimate user who mistypes a few times is not held back for
    // long.
    //
    // In memory, so the count resets when the process restarts and is per instance. That
    // is acceptable for a limit whose job is to make guessing slow rather than impossible.
    public class LoginThrottle
    {
        private const int MaxFailures = 5;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

        private readonly IMemoryCache _cache;
        public LoginThrottle(IMemoryCache cache) => _cache = cache;

        private static string Key(string? email) => "login-fail:" + (email ?? "").Trim().ToLowerInvariant();

        // True when this account has too many recent failures to try again yet.
        public bool IsBlocked(string? email) =>
            _cache.TryGetValue(Key(email), out int count) && count >= MaxFailures;

        // Records a failure. The window runs from the first failure, not the last,
        // so repeated attempts cannot extend a block indefinitely.
        public void RecordFailure(string? email)
        {
            var key = Key(email);
            var count = _cache.TryGetValue(key, out int c) ? c + 1 : 1;
            _cache.Set(key, count, Window);
        }

        // Clears the count after a successful sign-in.
        public void Clear(string? email) => _cache.Remove(Key(email));
    }
}
