using FleetWise.Services;

namespace RouteSyncWeb.Tests;

public class AppVersionTests
{
    [Fact]
    public void A_release_reads_as_its_number_alone()
    {
        Assert.Equal("1.2.0", AppVersion.TextFor("1.2.0+3f141448e6e8f8b5b7eaedcdf29b0ab739bfba70"));
        Assert.Equal("1.2.0", AppVersion.TextFor("1.2.0"));
    }

    [Fact]
    public void A_build_between_releases_reads_as_a_preview_with_its_commit()
    {
        Assert.Equal("1.3.0 preview · 3f14144", AppVersion.TextFor("1.3.0-alpha.0.4+3f141448e6e8f8b5b7eaedcdf29b0ab739bfba70"));
        Assert.Equal("1.3.0 preview", AppVersion.TextFor("1.3.0-alpha.0.4"));
    }

    [Fact]
    public void A_build_with_no_version_shows_nothing()
    {
        Assert.Equal("", AppVersion.TextFor(null));
        Assert.Equal("", AppVersion.TextFor(" "));
    }

    [Fact]
    public void This_build_has_a_version_from_the_tag()
    {
        // Built from a checkout with tags, so the number is never the 0.0.0 of a build that
        // found none.
        Assert.False(string.IsNullOrEmpty(AppVersion.Text));
        Assert.DoesNotContain("0.0.0", AppVersion.Text);
    }
}
