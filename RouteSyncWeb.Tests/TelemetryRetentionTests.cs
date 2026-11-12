using FleetWise.Services;

namespace RouteSyncWeb.Tests;

public class TelemetryRetentionTests
{
    private static readonly DateTime Cutoff = new(2026, 11, 1, 10, 0, 0);

    [Fact]
    public void A_busy_fleet_keeps_the_calendar_window()
    {
        var days = Enumerable.Range(0, 20).Select(i => new DateTime(2026, 11, 20).AddDays(-i));
        Assert.Equal(Cutoff, TelemetryRetentionService.KeepFrom(Cutoff, days, 15));
    }

    [Fact]
    public void An_idle_fleet_keeps_its_last_days_with_trips()
    {
        var days = new[] { new DateTime(2026, 10, 2), new DateTime(2026, 10, 2), new DateTime(2026, 9, 30), new DateTime(2026, 9, 1) };
        Assert.Equal(new DateTime(2026, 9, 30), TelemetryRetentionService.KeepFrom(Cutoff, days, 2));
    }

    [Fact]
    public void No_trips_at_all_keeps_the_calendar_window()
    {
        Assert.Equal(Cutoff, TelemetryRetentionService.KeepFrom(Cutoff, Array.Empty<DateTime>(), 15));
    }
}
