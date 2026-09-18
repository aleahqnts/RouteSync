namespace FleetWise.Services
{
    /// <summary>Waiting inside a background service, without an exception when the host stops.</summary>
    /// <remarks>
    /// A cancellable wait ends by raising <see cref="OperationCanceledException"/>, which is not a
    /// fault: it is how a service is told the application is closing. A debugger set to break on
    /// that exception pauses on every shutdown because of it, and while it is paused the whole
    /// application is stopped, so pages served from the same process hang. These waits finish
    /// quietly and say whether the time passed instead.
    /// </remarks>
    public static class HostedWait
    {
        /// <summary>Waits for a span. False when the host stopped before it passed.</summary>
        public static async Task<bool> ForAsync(TimeSpan span, CancellationToken stoppingToken)
        {
            if (stoppingToken.IsCancellationRequested) return false;

            var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = stoppingToken.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(false), stopped);

            // The timer is released as soon as the race is settled, so a wait the host outran
            // does not hold one until its span is up.
            using var elapsed = new CancellationTokenSource();
            var waiting = Task.Delay(span, elapsed.Token);

            var first = await Task.WhenAny(waiting, stopped.Task);
            elapsed.Cancel();

            return first == waiting;
        }
    }
}
