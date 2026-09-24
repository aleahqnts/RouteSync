namespace FleetWise.Services;

/// <summary>
/// Runs the security incident detector every few minutes.
/// </summary>
/// <remarks>
/// A short interval, because an incident is only useful while there is still time to act
/// on it. The incidents page also offers a scan on request, which is what anybody
/// demonstrating a rule will reach for rather than waiting.
///
/// While the host sleeps nothing runs, and nothing is lost: the detector reads from where
/// it last reached, so the first scan after waking covers the whole gap.
/// </remarks>
public sealed class SecurityDetectorService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SecurityDetectorService> _logger;

    public SecurityDetectorService(IServiceScopeFactory scopes, ILogger<SecurityDetectorService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await HostedWait.ForAsync(StartupDelay, stoppingToken)) return;

        do
        {
            try
            {
                // The detector reads through the audit service, which lives per request, so
                // each scan gets a scope of its own.
                using var scope = _scopes.CreateScope();
                var detector = scope.ServiceProvider.GetRequiredService<SecurityDetector>();
                var result = await detector.ScanAsync(stoppingToken);

                if (!result.Ran)
                    _logger.LogWarning("Security scan could not read what it needed; will retry.");
                else if (result.Raised > 0)
                    _logger.LogInformation("Security scan raised {Raised} incident(s).", result.Raised);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // A failure must not end the loop. The next scan re-reads the same span.
                _logger.LogWarning(ex, "Security scan failed; will retry next interval.");
            }
        }
        while (await HostedWait.ForAsync(Interval, stoppingToken));
    }
}
