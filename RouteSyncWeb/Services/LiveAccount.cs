using Microsoft.Extensions.Caching.Memory;
using FleetWise.Models;
using static Postgrest.Constants;

namespace FleetWise.Services
{
    /// <summary>A signed-in person's name, email and role as the database holds them now.</summary>
    /// <param name="RoleName">Null when the role could not be read, so the cookie's stands.</param>
    public sealed record AccountNow(string Name, string Email, string? RoleName);

    /// <summary>
    /// Reads the signed-in person's account as it stands, for the claims each request carries.
    /// </summary>
    /// <remarks>
    /// The sign-in cookie records the name, email and role once and keeps them until the
    /// person signs out. An account renamed on the Users page went on showing the old name
    /// in the sidebar and writing it into every audit entry, and a changed role kept its old
    /// permissions, until the next sign-in.
    ///
    /// Cached for a minute per person, the same as role permissions, so a page and the
    /// requests it makes cost one read between them. Saving an account forgets every entry,
    /// so the person who made the change sees it on the next page rather than a minute on.
    /// </remarks>
    public class LiveAccount
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

        // Bumped when any account is saved. It rides in the cache key, so every entry is
        // passed over at once without having to know which keys exist.
        private static int _generation;

        private readonly Supabase.Client _supabase;
        private readonly IMemoryCache _cache;

        public LiveAccount(Supabase.Client supabase, IMemoryCache cache)
        {
            _supabase = supabase;
            _cache = cache;
        }

        /// <summary>Forgets every cached account, so the next read is fresh.</summary>
        public static void Invalidate() => Interlocked.Increment(ref _generation);

        /// <summary>The account as it stands, or null when it cannot be read.</summary>
        /// <remarks>
        /// Null leaves the cookie's values in place. A database that cannot be reached for a
        /// moment is no reason to show somebody a blank name or take their permissions away.
        /// </remarks>
        public async Task<AccountNow?> ForUserAsync(int userId)
        {
            var key = $"account:{Volatile.Read(ref _generation)}:{userId}";
            if (_cache.TryGetValue(key, out AccountNow? cached) && cached is not null)
                return cached;

            try
            {
                var user = (await _supabase.From<UserModel>()
                    .Filter("user_id", Operator.Equals, userId.ToString())
                    .Get()).Models.FirstOrDefault();
                if (user is null) return null;

                var role = (await _supabase.From<Role>()
                    .Filter("role_id", Operator.Equals, user.RoleId.ToString())
                    .Get()).Models.FirstOrDefault();

                var now = new AccountNow(
                    AuthService.DisplayName(user.FirstName, user.MiddleName, user.LastName),
                    user.EmailAddress ?? "",
                    string.IsNullOrWhiteSpace(role?.RoleName) ? null : role!.RoleName);

                _cache.Set(key, now, Ttl);
                return now;
            }
            catch
            {
                return null;
            }
        }
    }
}
