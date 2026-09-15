using FleetWise.Models;

namespace FleetWise.Services
{
    /// <summary>
    /// Runs the monthly roster cycle when nobody does: drafts next month's roster from this
    /// month's, and publishes it if it is still a draft by the publish day.
    /// </summary>
    /// <remarks>
    /// <para>From the draft day (the 20th unless <c>Roster:DraftDay</c> says otherwise), a month
    /// with no roster yet is drafted from the month before, every shift rotated. From the
    /// publish day (the 25th, <c>Roster:PublishDay</c>), a roster still in draft is published
    /// as trips and every driver on it is told.</para>
    ///
    /// <para>The host sleeps when nobody is using it, so nothing here can wait for the
    /// morning of the 20th. It looks on startup and every half hour, and every step is safe
    /// to take twice: a month is drafted only when it has no roster at all, and published
    /// only while it is still a draft. A draft that should have been published before its
    /// month began is published on waking, late rather than never.</para>
    ///
    /// <para>A roster a person started during its own month is theirs to publish. Only a
    /// draft that existed before the month began is published for them.</para>
    ///
    /// <para>Every dashboard instance runs this against the same database, and the version
    /// check in the save and publish functions makes all but the first a no-op.
    /// <c>Roster:AutoCycle</c> set to false turns it off.</para>
    /// </remarks>
    public sealed class RosterCycleService : BackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(30);

        private readonly IServiceScopeFactory _scopes;
        private readonly IConfiguration _config;
        private readonly ILogger<RosterCycleService> _logger;

        /// <summary>Refusals already recorded today, so a roster that cannot be published is reported once a day, not every sweep.</summary>
        private readonly HashSet<string> _reported = new();

        public RosterCycleService(IServiceScopeFactory scopes, IConfiguration config, ILogger<RosterCycleService> logger)
        {
            _scopes = scopes;
            _config = config;
            _logger = logger;
        }

        public static int DraftDay(IConfiguration config) => Math.Clamp(config.GetValue("Roster:DraftDay", 20), 1, 28);

        public static int PublishDay(IConfiguration config) => Math.Clamp(config.GetValue("Roster:PublishDay", 25), 1, 28);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_config.GetValue("Roster:AutoCycle", true))
            {
                _logger.LogInformation("Roster cycle is turned off by Roster:AutoCycle.");
                return;
            }

            _logger.LogInformation("Roster cycle active: drafts from day {Draft}, publishes from day {Publish}, checked every {Interval}.",
                DraftDay(_config), PublishDay(_config), SweepInterval);

            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            using var timer = new PeriodicTimer(SweepInterval);
            do
            {
                try
                {
                    await SweepAsync();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // Tables not there yet, or the database out of reach. Tried again next time.
                    _logger.LogWarning(ex, "Roster cycle sweep failed; will retry next interval.");
                }
            }
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
        }

        private async Task SweepAsync()
        {
            using var scope = _scopes.CreateScope();
            var publisher = scope.ServiceProvider.GetRequiredService<RosterPublisher>();
            var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();

            var today = PhClock.OperationalDay;
            var thisMonth = RosterPublisher.FirstOf(today);
            var nextMonth = thisMonth.AddMonths(1);

            _reported.RemoveWhere(k => !k.EndsWith(today.ToString("|yyyy-MM-dd"), StringComparison.Ordinal));

            // A draft that was waiting before this month began and was never published.
            var current = await publisher.ReadMonthAsync(thisMonth);
            if (current is { Status: "Draft" } && ExistedBefore(current, thisMonth))
                await PublishAsync(publisher, audit, thisMonth, today);

            var upcoming = await publisher.ReadMonthAsync(nextMonth);

            if (today.Day >= DraftDay(_config) && upcoming is null
                && (await publisher.ReadSlotsAsync(thisMonth)).Count > 0)
            {
                var drafted = await publisher.GenerateAsync(nextMonth, 0, by: null);
                if (drafted.Step == RosterStep.Done)
                    _logger.LogInformation("Drafted the {Month:MMMM yyyy} roster.", nextMonth);
                upcoming = await publisher.ReadMonthAsync(nextMonth);
            }

            if (today.Day >= PublishDay(_config) && upcoming is { Status: "Draft" })
                await PublishAsync(publisher, audit, nextMonth, today);
        }

        private async Task PublishAsync(RosterPublisher publisher, AuditLog audit, DateTime month, DateTime today)
        {
            var result = await publisher.PublishAsync(month, version: null, by: null);

            if (result.Step == RosterStep.Done)
            {
                _logger.LogInformation("Published the {Month:MMMM yyyy} roster on schedule.", month);
                return;
            }

            if (result.Step != RosterStep.Refused) return;   // a race or a clash: next sweep tries again

            var key = $"{month:yyyy-MM}|{today:yyyy-MM-dd}";
            if (!_reported.Add(key)) return;

            await audit.WriteAsync("roster_published",
                $"could not publish the {month:MMMM yyyy} roster on schedule: "
                    + string.Join(" ", result.Problems ?? Array.Empty<string>()),
                "roster_months", month.ToString("yyyy-MM-dd"), outcome: "failed");
        }

        /// <summary>Whether a roster was drafted or saved before its month began, in Philippine time.</summary>
        private static bool ExistedBefore(RosterMonth roster, DateTime month)
        {
            var at = roster.GeneratedAt ?? roster.SavedAt;
            return at is DateTime stamp && PhClock.ToPh(new DateTimeOffset(stamp)).Date < month;
        }
    }
}
