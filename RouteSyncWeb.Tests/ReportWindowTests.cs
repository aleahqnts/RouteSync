using FleetWise.Models;
using FleetWise.Services;

namespace RouteSyncWeb.Tests;

public class ReportWindowTests
{
    // Tuesday 20 October 2026, 10:30 in the morning.
    private static readonly DateTime Now = new(2026, 10, 20, 10, 30, 0);
    private static readonly DateTime Today = new(2026, 10, 20);

    private static Trip On(DateTime day, int hour) =>
        new() { Date = day, ShiftStartTime = TimeSpan.FromHours(hour) };

    [Fact]
    public void A_running_month_is_compared_with_the_same_days_last_month()
    {
        var w = ReportWindow.For(Today, "This Month", Now);
        Assert.Equal(new DateTime(2026, 10, 20), w.LastDay);
        Assert.Equal((new DateTime(2026, 9, 1), new DateTime(2026, 9, 20)), (w.PrevStart, w.PrevEnd));
        Assert.Equal("vs Sep 1-20 by this time", w.VsLabel);
        Assert.True(w.IsAhead(new DateTime(2026, 10, 21)));
        Assert.False(w.IsAhead(new DateTime(2026, 10, 19)));
    }

    [Fact]
    public void A_long_month_to_date_stops_at_the_end_of_a_shorter_one()
    {
        var w = ReportWindow.For(new DateTime(2026, 3, 31), "This Month", new DateTime(2026, 3, 31, 12, 0, 0));
        Assert.Equal(new DateTime(2026, 2, 28), w.PrevEnd);
    }

    [Fact]
    public void A_finished_period_is_compared_whole()
    {
        var w = ReportWindow.For(new DateTime(2026, 9, 10), "This Month", Now);
        Assert.Equal((new DateTime(2026, 8, 1), new DateTime(2026, 8, 31)), (w.PrevStart, w.PrevEnd));
        Assert.Null(w.Cutoff);
        Assert.Equal("vs Aug 1-31", w.VsLabel);

        var last = ReportWindow.For(Today, "Last Week", Now);
        Assert.Equal((new DateTime(2026, 10, 12), new DateTime(2026, 10, 18)), (last.Start, last.End));
        Assert.Equal((new DateTime(2026, 10, 5), new DateTime(2026, 10, 11)), (last.PrevStart, last.PrevEnd));
    }

    [Fact]
    public void A_running_week_runs_Monday_to_today_against_the_same_days_before()
    {
        var w = ReportWindow.For(Today, "This Week", Now);
        Assert.Equal((new DateTime(2026, 10, 19), new DateTime(2026, 10, 20)), (w.Start, w.LastDay));
        Assert.Equal((new DateTime(2026, 10, 12), new DateTime(2026, 10, 13)), (w.PrevStart, w.PrevEnd));
    }

    [Fact]
    public void Today_counts_only_shifts_started_by_now_on_both_days()
    {
        var w = ReportWindow.For(Today, "This Day", Now);
        Assert.True(w.InCurrent(On(Today, 6)));
        Assert.False(w.InCurrent(On(Today, 14)));
        Assert.True(w.InPrevious(On(Today.AddDays(-1), 6)));
        Assert.False(w.InPrevious(On(Today.AddDays(-1), 14)));
        Assert.True(w.IsAhead(Today, TimeSpan.FromHours(22)));
        Assert.Equal("vs Oct 19 by this time", w.VsLabel);
    }

    [Fact]
    public void After_midnight_the_evening_shift_has_started_and_the_day_is_still_yesterday()
    {
        var w = ReportWindow.For(Today, "This Day", new DateTime(2026, 10, 21, 1, 0, 0));
        Assert.True(w.InCurrent(On(Today, 22)));
        Assert.False(w.IsAhead(Today, TimeSpan.FromHours(22)));
    }
}
