using FleetWise.Services;
using Microsoft.Extensions.Caching.Memory;

namespace RouteSyncWeb.Tests;

public class RefusedPagesTests
{
    private static MemoryCache NewCache() => new(new MemoryCacheOptions());

    private static Task<IReadOnlyList<string>> Grants(params string[] keys) =>
        Task.FromResult<IReadOnlyList<string>>(keys);

    private static Task<IReadOnlyList<string>> Down() =>
        throw new HttpRequestException("database unreachable");

    [Fact]
    public async Task A_failed_read_keeps_the_permissions_last_read_for_the_role()
    {
        var cache = NewCache();
        Assert.Contains("routes", await RolePermissions.ResolveAsync(cache, 0, "Admin", () => Grants("routes")));

        // The fresh entry has expired; only the fallback is left.
        cache.Remove("perm:0:Admin");

        Assert.Contains("routes", await RolePermissions.ResolveAsync(cache, 0, "Admin", Down));
    }

    [Fact]
    public async Task A_failed_read_with_nothing_read_before_grants_nothing()
    {
        Assert.Empty(await RolePermissions.ResolveAsync(NewCache(), 0, "Admin", Down));
    }

    [Fact]
    public async Task A_role_saved_since_the_last_read_is_not_served_its_old_permissions()
    {
        var cache = NewCache();
        await RolePermissions.ResolveAsync(cache, 0, "Admin", () => Grants("routes"));

        Assert.Empty(await RolePermissions.ResolveAsync(cache, 1, "Admin", Down));
    }

    [Fact]
    public void A_poll_refused_repeatedly_is_recorded_once_per_window()
    {
        var cache = NewCache();

        Assert.True(RequirePermissionAttribute.IsFirstInWindow(cache, "1021", "/Dispatch/Pulse", "routes"));
        for (var i = 0; i < 70; i++)
            Assert.False(RequirePermissionAttribute.IsFirstInWindow(cache, "1021", "/Dispatch/Pulse", "routes"));

        // A different address, or a different person, is still its own entry.
        Assert.True(RequirePermissionAttribute.IsFirstInWindow(cache, "1021", "/Routes", "routes"));
        Assert.True(RequirePermissionAttribute.IsFirstInWindow(cache, "1022", "/Dispatch/Pulse", "routes"));
    }
}
