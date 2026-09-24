namespace FleetWise.Services;

/// <summary>
/// Reads the audit trail and raises or extends security incidents.
/// </summary>
/// <remarks>
/// Runs on a timer and on request from the incidents page, and the two share one scan at
/// a time. Two scans racing over the same span could each decide a burst was new; the
/// table's uniqueness would stop the second copy, but only after both had done the work.
///
/// Reads from where the last scan reached, less the longest window any rule uses, so a
/// threshold ending now is always seen whole. The dashboard's host sleeps when nobody is
/// using it, while drivers' sign-ins go on being recorded by the edge functions, and
/// starting from the last scan rather than from a fixed distance back means a long sleep
/// delays an incident rather than losing it.
///
/// Every step is safe to repeat. A burst already on record is recognised and extended
/// rather than raised again, and a count is worked out from the entries rather than added
/// to, so a scan that re-reads a span it has seen, or stops part way and is run again,
/// changes nothing it should not.
/// </remarks>
public sealed class SecurityDetector
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Read again before the previous scan's end. Identifiers are handed out when a row
    /// is written but can become visible out of order, so a scan that stopped at one entry
    /// can have passed a neighbour that committed a moment later.
    /// </summary>
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The furthest back a scan will read after a long gap, so waking after months does
    /// not ask for the whole trail in one go.
    /// </summary>
    private static readonly TimeSpan MaxCatchUp = TimeSpan.FromDays(30);

    private const int PageSize = 1000;

    private readonly AuditLog _audit;
    private readonly SecurityIncidents _incidents;

    public SecurityDetector(AuditLog audit, SecurityIncidents incidents)
    {
        _audit = audit;
        _incidents = incidents;
    }

    /// <param name="Ran">False when a scan could not read what it needed and changed nothing.</param>
    public sealed record ScanResult(bool Ran, int Raised, int Extended);

    public async Task<ScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        // Waits briefly for a scan already under way rather than refusing outright, so a
        // press of Scan now just after the timer fires still reports what was found.
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken))
            return new(false, 0, 0);

        try { return await RunAsync(); }
        finally { Gate.Release(); }
    }

    private async Task<ScanResult> RunAsync()
    {
        var startedAt = DateTimeOffset.UtcNow;

        var through = await _incidents.ScannedThroughAsync();
        if (through is null) return new(false, 0, 0);

        var from = through.Value - SecurityRules.LongestWindow - Overlap;
        if (from < startedAt - MaxCatchUp) from = startedAt - MaxCatchUp;

        var events = await ReadEventsAsync(from);
        if (events is null) return new(false, 0, 0);

        // Any incident that activity in this span could still extend.
        var longestIdle = SecurityRules.All.Max(r => r.Idle);
        var existing = await _incidents.TouchingAsync(from - longestIdle);
        if (existing is null) return new(false, 0, 0);

        var raised = 0;
        var extended = 0;

        foreach (var rule in SecurityRules.All)
        {
            var actions = rule.Actions.ToHashSet();
            var byKey = events
                .Where(e => actions.Contains(e.Action))
                .Select(e => (Key: e.KeyFor(rule.KeyColumn), Event: e))
                .Where(x => !string.IsNullOrEmpty(x.Key))
                .GroupBy(x => x.Key!, x => x.Event);

            foreach (var group in byKey)
            {
                var keyEvents = group.OrderBy(e => e.At).ThenBy(e => e.Id).ToList();

                var latest = existing
                    .Where(i => i.Rule == rule.Code && i.KeyValue == group.Key)
                    .OrderByDescending(i => i.LastSeenAt)
                    .FirstOrDefault();

                var coveredThrough = DateTimeOffset.MinValue;

                if (latest is not null)
                {
                    // Activity that follows the latest incident closely is more of it.
                    var end = latest.LastSeenAt;
                    foreach (var e in keyEvents.Where(e => e.At > latest.LastSeenAt))
                    {
                        if (e.At - end > rule.Idle) break;
                        end = e.At;
                    }

                    var count = await CountAsync(latest, end, keyEvents, from);
                    if (count is not null
                        && (end != latest.LastSeenAt || count.Value != latest.EventCount)
                        && await _incidents.UpdateSpanAsync(latest.IncidentId, end, count.Value))
                    {
                        extended++;
                    }

                    coveredThrough = end;
                }

                // Only what comes after the latest incident is examined for a new one.
                // Anything before it was examined when it was raised, and looking again
                // could find the same burst with a different first entry.
                var fresh = keyEvents.Where(e => e.At > coveredThrough).ToList();

                foreach (var episode in SecurityRules.Episodes(rule, fresh))
                {
                    var count = fresh.Count(e => e.At >= episode.Start && e.At <= episode.End);
                    if (await _incidents.InsertAsync(rule, group.Key, episode, count))
                        raised++;
                }
            }
        }

        // Moved on only after everything above was read. A scan that failed part way is
        // repeated in full next time, which the idempotence above makes harmless.
        await _incidents.SetScannedThroughAsync(startedAt);

        return new(true, raised, extended);
    }

    /// <summary>
    /// The number of entries an incident holds through <paramref name="end"/>.
    /// </summary>
    /// <remarks>
    /// Worked out from the entries rather than added to, so an entry seen twice is counted
    /// once. Counted from what this scan already read when the incident falls inside it,
    /// and asked of the trail when it began earlier. An older incident that has not grown
    /// is left as it stands.
    /// </remarks>
    private async Task<int?> CountAsync(
        SecurityIncident incident, DateTimeOffset end, List<SecurityEvent> keyEvents, DateTimeOffset readFrom)
    {
        var frozen = incident.Actions.ToHashSet();
        var allRead = frozen.All(a => SecurityRules.AllActions.Contains(a));

        if (incident.FirstSeenAt >= readFrom && allRead)
        {
            return keyEvents.Count(e =>
                frozen.Contains(e.Action) && e.At >= incident.FirstSeenAt && e.At <= end);
        }

        if (end == incident.LastSeenAt) return incident.EventCount;

        var filter = SecurityIncidents.FilterFor(incident, end);
        if (filter is null) return null;

        var (rows, total) = await _audit.QueryAsync($"select=id&{filter}&limit=1");
        return rows is null ? null : total;
    }

    /// <summary>Every entry any rule reads, from a point onward, oldest first.</summary>
    /// <returns>Null when the trail could not be read in full.</returns>
    private async Task<List<SecurityEvent>?> ReadEventsAsync(DateTimeOffset from)
    {
        var actions = string.Join(",", SecurityRules.AllActions);
        var all = new List<SecurityEvent>();
        long after = 0;

        // Paged by identifier rather than by offset, so an entry written while this reads
        // cannot shift a page and be skipped or seen twice.
        while (true)
        {
            var (rows, _) = await _audit.QueryAsync(
                "select=id,occurred_at,action,ip,target_id,actor_id"
                + $"&action=in.({actions})"
                + $"&occurred_at=gte.{SecurityIncidents.Stamp(from)}"
                + $"&id=gt.{after}&order=id.asc&limit={PageSize}");

            if (rows is null) return null;

            all.AddRange(rows.Select(r =>
                new SecurityEvent(r.Id, r.OccurredAt, r.Action, r.Ip, r.TargetId, r.ActorId)));

            if (rows.Count < PageSize) return all;
            after = rows[^1].Id;
        }
    }
}
