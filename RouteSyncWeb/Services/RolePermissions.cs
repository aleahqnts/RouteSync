using Microsoft.Extensions.Caching.Memory;
using FleetWise.Models;

namespace FleetWise.Services
{
    // What a role may see, read from the database rather than from a sign-in cookie.
    //
    // Permissions do not travel in the cookie, which is written once at sign-in: granting one would then do nothing for anyone already signed in until they signed in again, and revoking one would leave them holding it, which is the direction that matters.
    //
    // Reading them per request would be a round trip to Supabase on every page, in an
    // authorization filter, where the wait lands straight on the response. So they are
    // cached: permissions belong to a role rather than to a person, and there are three
    // roles, so under any real traffic this makes far fewer calls than there are page
    // loads. The expiry is only a backstop, because saving a role drops the entry.
    //
    // What this does not fix: the cookie names the role, so moving someone from one role
    // to another still waits for their next sign-in. Editing a role is the thing done
    // while people are working; reassigning one is rarer and usually agreed with them
    // first.
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

        // Forgets every cached role, so the next read is fresh.
        public static void Invalidate() => Interlocked.Increment(ref _generation);

        // How long the last successful read stands in when the database cannot be reached.
        private static readonly TimeSpan LastGoodTtl = TimeSpan.FromMinutes(10);

        // The permission keys a role currently holds.
        public Task<IReadOnlyList<string>> ForRoleAsync(string? roleName)
        {
            if (string.IsNullOrWhiteSpace(roleName))
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

            return ResolveAsync(_cache, Volatile.Read(ref _generation), roleName, async () =>
            {
                var response = await _supabase.From<Role>()
                    .Filter("role_name", Postgrest.Constants.Operator.Equals, roleName)
                    .Get();

                return response.Models.FirstOrDefault()?.WebPermissions?
                    .Where(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .ToList() ?? new List<string>();
            });
        }

        // The cached permissions when fresh, otherwise a new read.
        //
        // A read that fails falls back to the last permissions this role was read with, so a
        // database that cannot be reached for a moment does not lock everyone out of every
        // page and fill the audit trail with refusals. LiveAccount keeps the cookie's role on
        // a failed read for the same reason. The fallback belongs to the same generation as
        // the fresh entry, so a role saved since then is never served its old permissions:
        // with no earlier read to stand on, a failure grants nothing and is not cached, and
        // the next request tries again.
        public static async Task<IReadOnlyList<string>> ResolveAsync(
            IMemoryCache cache, int generation, string roleName,
            Func<Task<IReadOnlyList<string>>> read)
        {
            var key = $"perm:{generation}:{roleName}";
            if (cache.TryGetValue(key, out IReadOnlyList<string>? cached) && cached is not null)
                return cached;

            var lastGoodKey = $"perm-last:{generation}:{roleName}";
            IReadOnlyList<string> permissions;
            try
            {
                permissions = await read();
            }
            catch
            {
                return cache.TryGetValue(lastGoodKey, out IReadOnlyList<string>? lastGood) && lastGood is not null
                    ? lastGood
                    : Array.Empty<string>();
            }

            cache.Set(key, permissions, Ttl);
            cache.Set(lastGoodKey, permissions, LastGoodTtl);
            return permissions;
        }
    }
}
