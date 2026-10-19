namespace FleetWiseMobile.Services;

/// <summary>One GPS fix as the send rule sees it.</summary>
/// <param name="At">When the fix was taken.</param>
/// <param name="Speed">Metres per second, or null when the fix carries none.</param>
/// <param name="Accuracy">Radius in metres the fix lies within, or null when unknown.</param>
/// <param name="Count">The trip's passenger count when the fix was taken.</param>
public readonly record struct TelemetryFix(DateTime At, double? Speed, double? Accuracy, int Count);

/// <summary>Which GPS fixes become telemetry rows during a trip.</summary>
/// <remarks>
/// <para>A moving bus is reported every <see cref="MovingEverySeconds"/> seconds, which is
/// what the fleet map needs to carry its marker smoothly between readings. A standing bus,
/// at a stop or in traffic, is reported every <see cref="StandingEverySeconds"/>: its
/// position is not changing, and every row is kept for days.</para>
///
/// <para>A bus that pulls away or comes to a stop is reported at once, so the map starts or
/// stops its marker within a second of the bus. So is a change in the passenger count,
/// which is audit evidence.</para>
///
/// <para>Only a fix from the GPS itself is sent. It always carries a speed, zero when
/// standing. A fix with no speed came from cell towers or wifi, which can place a bus tens
/// of metres from where it is while claiming to be accurate, and is not sent. The trip's
/// first row is the exception: the bus has to appear on the map somewhere.</para>
/// </remarks>
public static class TelemetrySendRule
{
    public const double MovingEverySeconds = 5;
    public const double StandingEverySeconds = 30;

    /// <summary>
    /// Seconds after the last row before pulling away or stopping is reported, so a bus
    /// crawling in traffic at about walking pace is not reported every second.
    /// </summary>
    public const double ChangeAfterSeconds = 2;

    /// <summary>Metres per second at or above which the bus counts as moving, as on the fleet map.</summary>
    public const double MovingSpeed = 1.0;

    /// <summary>Accuracy radius, in metres, beyond which a fix is not sent.</summary>
    public const double PoorAccuracy = 25;

    /// <summary>
    /// The fixes are taken about once a second, so an interval is met slightly early rather
    /// than missed by a fraction and sent a second late.
    /// </summary>
    private const double Slack = 0.5;

    /// <summary>Whether a fix is good enough to place the bus.</summary>
    public static bool Usable(TelemetryFix fix) =>
        fix.Speed is not null && !(fix.Accuracy > PoorAccuracy);

    /// <summary>Whether this fix should be written, given the last one that was.</summary>
    /// <param name="last">The last fix written for the trip, or null before the first.</param>
    public static bool ShouldWrite(TelemetryFix? last, TelemetryFix fix)
    {
        if (last is not TelemetryFix was)
            return true;
        if (fix.Count != was.Count)
            return true;

        var since = (fix.At - was.At).TotalSeconds;
        var moving = fix.Speed >= MovingSpeed;
        if (moving != was.Speed >= MovingSpeed && since >= ChangeAfterSeconds - Slack)
            return true;

        var every = moving ? MovingEverySeconds : StandingEverySeconds;
        return since >= every - Slack;
    }
}
