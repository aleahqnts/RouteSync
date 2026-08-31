using Microsoft.Extensions.Caching.Memory;
using FleetWise.Models;

namespace FleetWise.Services
{
    /// <summary>
    /// What a role may see, read from the database rather than from a sign-in cookie.
    /// </summary>
    /// <remarks>
    /// Permissions used to travel in the cookie, written once at sign-in. Granting a
    /// permission then did nothing for anyone already signed in until they signed in
    /// again, and revoking one left them holding it, which is the direction that matters.
    ///
    /// Reading them per request would be a round trip to Supabase on every page, in an
    /// authorization filter, where the wait lands straight on the response. So they are
    /// cached: permissions belong to a role rather than to a person, and there are three
    /// roles, so under any real traffic this makes far fewer calls than there are page
    /// loads. The expiry is only a backstop, because saving a role drops the entry.
    ///
    /// What this does not fix: the cookie names the role, so moving someone from one role
    /// to another still waits for their next sign-in. Editing a role is the thing done
    /// while people are working; reassigning one is rarer and usually agreed with them
    /// first.
    /// </remarks>
    public class RolePermissions
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

        // Bumped when any role is saved. It rides in the cache key, so every entry is
        // abandoned at once and a renamed role cannot leave a stale one behind under its
        // old name.
        private static int _generation;

        private readonly Supabase.Client _supabase;
        private readonly IMemoryCache _cache;

        public RolePermissions(Supabase.Client supabase, IMemoryCache cache)
        {
            _supabase = supabase;
            _cache = cache;
        }

        /// <summary>Forgets every cached role, so the next read is fresh.</summary>
        public static void Invalidate() => Interlocked.Increment(ref _generation);

        /// <summary>The permission keys a role currently holds.</summary>
        public async Task<IReadOnlyList<string>> ForRoleAsync(string? roleName)
        {
            if (string.IsNullOrWhiteSpace(roleName)) return Array.Empty<string>();

            var key = $"perm:{Volatile.Read(ref _generation)}:{roleName}";
            if (_cache.TryGetValue(key, out IReadOnlyList<string>? cached) && cached is not null)
                return cached;

            IReadOnlyList<string> permissions;
            try
            {
                var response = await _supabase.From<Role>()
                    .Filter("role_name", Postgrest.Constants.Operator.Equals, roleName)
                    .Get();

                permissions = response.Models.FirstOrDefault()?.WebPermissions?
                    .Where(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .ToList() ?? new List<string>();
            }
            catch
            {
                // A read that fails grants nothing, and is not cached, so the next request
                // tries again. Failing closed on a lookup the operator cannot see is the
                // safer way round: the alternative hands out access because a query timed
                // out.
                return Array.Empty<string>();
            }

            _cache.Set(key, permissions, Ttl);
            return permissions;
        }
    }
}
