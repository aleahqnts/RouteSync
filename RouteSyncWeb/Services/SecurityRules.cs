namespace FleetWise.Services;

/// <summary>One audit entry, reduced to what the rules read.</summary>
public sealed record SecurityEvent(
    long Id, DateTimeOffset At, string Action, string? Ip, string? TargetId, string? ActorId)
{
    /// <summary>The value this entry has in the column a rule groups by.</summary>
    public string? KeyFor(string column) => column switch
    {
        "ip" => Ip,
        "target_id" => TargetId,
        "actor_id" => ActorId,
        _ => null,
    };
}

/// <summary>So many entries within so long.</summary>
public sealed record Threshold(int Count, TimeSpan Window);

/// <summary>A burst of activity found by a rule, from its first counted entry to its last.</summary>
public readonly record struct Episode(DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// One kind of unusual activity, and how much of it makes an incident.
/// </summary>
/// <param name="Code">Stored on every incident the rule raises. Never renamed.</param>
/// <param name="Actions">What an incident from this rule gathers when opened.</param>
/// <param name="Counted">What counts toward a threshold.</param>
/// <param name="Thresholds">Crossing any one of them raises an incident.</param>
/// <param name="Success">
/// When set, a threshold is only crossed by an entry of this action, counting the entries
/// before it. The rule is about something that succeeded after failing.
/// </param>
public sealed record SecurityRule(
    string Code,
    string Title,
    string Severity,
    string KeyColumn,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Counted,
    IReadOnlyList<Threshold> Thresholds,
    string? Success = null)
{
    /// <summary>
    /// How long activity must stop before an incident is over.
    /// </summary>
    /// <remarks>
    /// Never shorter than the rule's own longest window. A slower rule, such as twenty
    /// failures across a day, is crossed by activity spaced hours apart. With a shorter
    /// quiet period each of those entries would close the incident and the next would
    /// cross the threshold again, raising one incident per entry for a single attack.
    /// </remarks>
    public TimeSpan Idle
    {
        get
        {
            var longest = Thresholds.Max(t => t.Window);
            return longest > SecurityRules.MinimumIdle ? longest : SecurityRules.MinimumIdle;
        }
    }
}

/// <summary>
/// What the incident detector looks for.
/// </summary>
/// <remarks>
/// Both sign-in paths allow five failures per account in fifteen minutes, counted from the
/// first failure and checked before the password. The thresholds here are set by that,
/// rather than chosen freely, in two ways.
///
/// A success can follow at most four failures inside one window, because the sixth attempt
/// is refused even with the right password. A rule asking for five failures before a
/// success would never fire.
///
/// Someone who tries four times every fifteen minutes never trips the throttle at all, and
/// leaves no sign of having been slowed down. The longer windows here exist to see exactly
/// that.
/// </remarks>
public static class SecurityRules
{
    public static readonly TimeSpan MinimumIdle = TimeSpan.FromMinutes(30);

    public static readonly IReadOnlyList<SecurityRule> All = new SecurityRule[]
    {
        // Carrier address sharing puts many drivers behind one address, so the bar is set
        // well above what one confused person can reach: the throttle stops recording
        // their failures at five.
        new("failed_signins_address",
            "Many failed sign-ins from one address",
            "medium", "ip",
            new[] { "login_failed", "login_throttled" },
            new[] { "login_failed", "login_throttled" },
            new[] { new Threshold(10, TimeSpan.FromMinutes(15)) }),

        // Deliberately above the throttle's five in fifteen minutes. This is somebody
        // pacing their attempts to stay under it.
        new("failed_signins_account",
            "Many failed sign-ins against one account",
            "medium", "target_id",
            new[] { "login_failed" },
            new[] { "login_failed" },
            new[]
            {
                new Threshold(8, TimeSpan.FromHours(1)),
                new Threshold(20, TimeSpan.FromHours(24)),
            }),

        // The one rule that says somebody may have got in rather than that somebody tried.
        // Grouped by account, not address: under address sharing, one driver's failures
        // and another driver's success can arrive from the same place.
        new("signin_after_failures",
            "Signed in after failed attempts",
            "high", "target_id",
            new[] { "login_failed", "login" },
            new[] { "login_failed" },
            new[] { new Threshold(3, TimeSpan.FromHours(1)) },
            Success: "login"),

        // A stale bookmark or a link from a colleague produces one or two of these. Ten in
        // ten minutes is somebody working through addresses.
        new("pages_refused",
            "Repeatedly refused pages",
            "medium", "actor_id",
            new[] { "access_denied" },
            new[] { "access_denied" },
            new[] { new Threshold(10, TimeSpan.FromMinutes(10)) }),

        // Grouped by address because the device identifier is whatever the caller says it
        // is. Also fires for a counter phone set up with the wrong passcode, which retries
        // on every loop, and which is worth knowing about for its own sake.
        new("counter_refused",
            "Counter phone refused repeatedly",
            "medium", "ip",
            new[] { "token_refused" },
            new[] { "token_refused" },
            new[] { new Threshold(5, TimeSpan.FromMinutes(15)) }),

        new("reset_codes_failing",
            "Password reset codes failing",
            "low", "target_id",
            new[] { "password_reset_failed" },
            new[] { "password_reset_failed" },
            new[] { new Threshold(5, TimeSpan.FromHours(1)) }),
    };

    private static readonly Dictionary<string, SecurityRule> ByCodeMap =
        All.ToDictionary(r => r.Code);

    public static SecurityRule? ByCode(string code) =>
        ByCodeMap.TryGetValue(code, out var rule) ? rule : null;

    /// <summary>Every action any rule reads, so the detector can fetch them in one pass.</summary>
    public static readonly IReadOnlyList<string> AllActions =
        All.SelectMany(r => r.Actions).Distinct().ToList();

    /// <summary>
    /// How far back a scan must look for a threshold that ends now to be seen whole.
    /// </summary>
    public static readonly TimeSpan LongestWindow =
        All.SelectMany(r => r.Thresholds).Max(t => t.Window);

    /// <summary>
    /// The bursts in one key's activity that cross a rule's threshold.
    /// </summary>
    /// <param name="events">
    /// One key's entries for this rule, oldest first, none of them already inside an
    /// incident.
    /// </param>
    /// <remarks>
    /// A burst starts at the first entry that counted toward crossing, not at the entry
    /// that crossed. Opening one should show every failure that caused it, not the last.
    ///
    /// Once crossed, a burst gathers everything after it until the rule's quiet period
    /// passes with nothing new. Activity after that is examined afresh and must cross the
    /// threshold again on its own.
    ///
    /// Deterministic for the same entries, which is what lets a scan run twice over the
    /// same span without raising anything twice.
    /// </remarks>
    public static List<Episode> Episodes(SecurityRule rule, IReadOnlyList<SecurityEvent> events)
    {
        var found = new List<Episode>();
        var counted = rule.Counted.ToHashSet();
        var i = 0;

        while (i < events.Count)
        {
            var crossing = FindCrossing(rule, events, counted, i);
            if (crossing is null) break;

            var (at, start) = crossing.Value;
            var end = events[at].At;
            var next = at + 1;
            while (next < events.Count && events[next].At - end <= rule.Idle)
            {
                end = events[next].At;
                next++;
            }

            found.Add(new Episode(start, end));
            i = next;
        }

        return found;
    }

    /// <summary>
    /// The first entry at or after <paramref name="from"/> that crosses a threshold, and
    /// where the window it crossed began.
    /// </summary>
    private static (int At, DateTimeOffset Start)? FindCrossing(
        SecurityRule rule, IReadOnlyList<SecurityEvent> events, HashSet<string> counted, int from)
    {
        // Positions of the counted entries, in order. A threshold of N is crossed at the
        // Nth of them when the first of those N is recent enough.
        var positions = new List<int>();

        for (var j = from; j < events.Count; j++)
        {
            var e = events[j];

            if (rule.Success is null)
            {
                if (!counted.Contains(e.Action)) continue;
                positions.Add(j);

                DateTimeOffset? start = null;
                foreach (var t in rule.Thresholds)
                {
                    if (positions.Count < t.Count) continue;
                    var first = events[positions[^t.Count]].At;
                    if (e.At - first <= t.Window && (start is null || first < start))
                        start = first;
                }
                if (start is not null) return (j, start.Value);
            }
            else
            {
                if (counted.Contains(e.Action))
                {
                    positions.Add(j);
                    continue;
                }
                if (e.Action != rule.Success) continue;

                // Failures before this success, inside each window.
                DateTimeOffset? start = null;
                foreach (var t in rule.Thresholds)
                {
                    var within = positions
                        .Select(p => events[p].At)
                        .Where(at => at >= e.At - t.Window)
                        .ToList();
                    if (within.Count >= t.Count && (start is null || within[0] < start))
                        start = within[0];
                }
                if (start is not null) return (j, start.Value);

                // A success that crossed nothing ends the run of failures before it. The
                // person got in, so those failures were their own, and they must not be
                // added to a later, unrelated fumble to make three.
                positions.Clear();
            }
        }

        return null;
    }
}
