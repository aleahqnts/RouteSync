using FleetWise.Services;

namespace RouteSyncWeb.Tests;

public class FiguresTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1k")]
    [InlineData(1650, "1.6k")]
    [InlineData(9999, "9.9k")]
    [InlineData(16420, "16k")]
    [InlineData(160999, "160k")]
    [InlineData(1_250_000, "1.2M")]
    public void A_count_is_written_short(long n, string expected) =>
        Assert.Equal(expected, Figures.Compact(n));
}
