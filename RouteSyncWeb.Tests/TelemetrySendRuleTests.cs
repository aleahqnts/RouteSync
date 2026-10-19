using FleetWiseMobile.Services;

namespace RouteSyncWeb.Tests;

public class TelemetrySendRuleTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 8, 0, 0);

    private static TelemetryFix Fix(double seconds, double? speed, int count = 0, double? accuracy = 3) =>
        new(T0.AddSeconds(seconds), speed, accuracy, count);

    [Fact]
    public void The_first_fix_of_a_trip_is_always_sent()
    {
        Assert.True(TelemetrySendRule.ShouldWrite(null, Fix(0, null, accuracy: 100)));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void A_moving_bus_is_sent_every_five_seconds(double after, bool sent)
    {
        Assert.Equal(sent, TelemetrySendRule.ShouldWrite(Fix(0, 5), Fix(after, 5)));
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(29, false)]
    [InlineData(30, true)]
    public void A_standing_bus_is_sent_every_thirty_seconds(double after, bool sent)
    {
        Assert.Equal(sent, TelemetrySendRule.ShouldWrite(Fix(0, 0), Fix(after, 0.3)));
    }

    [Fact]
    public void A_bus_pulling_away_is_sent_at_once()
    {
        Assert.True(TelemetrySendRule.ShouldWrite(Fix(0, 0), Fix(12, 2.5)));
    }

    [Fact]
    public void A_count_change_is_sent_at_once_even_standing()
    {
        Assert.True(TelemetrySendRule.ShouldWrite(Fix(0, 0, count: 4), Fix(1, 0, count: 5)));
    }

    [Fact]
    public void A_bus_coming_to_a_stop_is_sent_at_once()
    {
        Assert.True(TelemetrySendRule.ShouldWrite(Fix(0, 5), Fix(2, 0.4)));
    }

    [Fact]
    public void A_bus_crawling_on_and_off_is_not_sent_every_second()
    {
        Assert.False(TelemetrySendRule.ShouldWrite(Fix(0, 1.2), Fix(1, 0.8)));
        Assert.False(TelemetrySendRule.ShouldWrite(Fix(0, 0.8), Fix(1, 1.2)));
    }

    [Theory]
    [InlineData(4.0, 2.0, true)]     // a GPS fix
    [InlineData(0.0, 1.5, true)]     // a GPS fix standing still
    [InlineData(null, 11.5, false)]  // a cell tower or wifi fix: no speed
    [InlineData(5.0, 30.0, false)]   // too inaccurate
    [InlineData(5.0, null, true)]    // accuracy not given
    public void Only_a_gps_fix_good_to_25_m_places_the_bus(double? speed, double? accuracy, bool usable)
    {
        Assert.Equal(usable, TelemetrySendRule.Usable(Fix(0, speed, accuracy: accuracy)));
    }
}
