using FleetWise.Services;
using Xunit;

namespace RouteSyncWeb.Tests;

public class ApkUpdateFeedTests
{
    private static string Release(string asset, bool pre = false) =>
        $$"""{"draft":false,"prerelease":{{pre.ToString().ToLower()}},"assets":[{"name":"{{asset}}","browser_download_url":"https://x/{{asset}}"}]}""";

    [Fact]
    public void Picks_the_highest_published_driver_apk()
    {
        var json = "[" + string.Join(",", Release("RouteSync-Driver-1.8.4.apk"), Release("RouteSync-Sentinel-9.0.0.apk"),
            Release("RouteSync-Driver-1.9.0.apk", pre: true), Release("RouteSync-Driver-1.8.0.apk")) + "]";

        var pick = ApkUpdateFeed.Pick(json, ApkUpdateFeed.Driver);

        Assert.Equal("1.8.4", pick!.Version);
        Assert.Equal("https://x/RouteSync-Driver-1.8.4.apk", pick.ApkUrl);
    }

    [Fact]
    public void Picks_the_sentinel_apk_apart_from_the_driver_one()
    {
        var json = "[" + string.Join(",", Release("RouteSync-Driver-1.14.0.apk"), Release("RouteSync-Sentinel-1.13.6.apk")) + "]";

        var pick = ApkUpdateFeed.Pick(json, ApkUpdateFeed.Sentinel);

        Assert.Equal("1.13.6", pick!.Version);
        Assert.Equal("https://x/RouteSync-Sentinel-1.13.6.apk", pick.ApkUrl);
    }

    [Fact]
    public void Offers_nothing_when_no_release_carries_one()
        => Assert.Null(ApkUpdateFeed.Pick("[" + Release("notes.txt") + "]", ApkUpdateFeed.Driver));
}
