using FleetWise.Services;
using Xunit;

namespace RouteSyncWeb.Tests;

public class DriverUpdateFeedTests
{
    private static string Release(string asset, bool pre = false) =>
        $$"""{"draft":false,"prerelease":{{pre.ToString().ToLower()}},"assets":[{"name":"{{asset}}","browser_download_url":"https://x/{{asset}}"}]}""";

    [Fact]
    public void Picks_the_highest_published_driver_apk()
    {
        var json = "[" + string.Join(",", Release("RouteSync-Driver-1.8.4.apk"), Release("RouteSync-Sentinel-9.0.0.apk"),
            Release("RouteSync-Driver-1.9.0.apk", pre: true), Release("RouteSync-Driver-1.8.0.apk")) + "]";

        var pick = DriverUpdateFeed.Pick(json);

        Assert.Equal("1.8.4", pick!.Version);
        Assert.Equal("https://x/RouteSync-Driver-1.8.4.apk", pick.ApkUrl);
    }

    [Fact]
    public void Offers_nothing_when_no_release_carries_one()
        => Assert.Null(DriverUpdateFeed.Pick("[" + Release("notes.txt") + "]"));
}
