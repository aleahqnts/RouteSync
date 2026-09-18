using System.Diagnostics;
using FleetWise.Services;

namespace RouteSyncWeb.Tests;

/// <summary>
/// The wait a background service sleeps on between sweeps. It has to end quietly when the host
/// stops: an exception there pauses a debugger set to break on one, and a paused process serves
/// no pages.
/// </summary>
public class HostedWaitTests
{
    [Fact]
    public async Task A_span_that_passes_answers_true()
    {
        Assert.True(await HostedWait.ForAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None));
    }

    [Fact]
    public async Task A_host_already_stopped_answers_false_without_waiting()
    {
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();

        var clock = Stopwatch.StartNew();
        var passed = await HostedWait.ForAsync(TimeSpan.FromMinutes(5), stopped.Token);

        Assert.False(passed);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"waited {clock.Elapsed}");
    }

    [Fact]
    public async Task A_host_stopping_mid_wait_answers_false_rather_than_throwing()
    {
        using var stopping = new CancellationTokenSource();
        var waiting = HostedWait.ForAsync(TimeSpan.FromMinutes(5), stopping.Token);

        stopping.Cancel();

        var clock = Stopwatch.StartNew();
        Assert.False(await waiting);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"waited {clock.Elapsed}");
    }
}
