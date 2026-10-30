namespace FleetWiseMobile.Services;

// One GPS fix as the send rule sees it.
//
// Speed is in metres per second and null when the fix carries none. Accuracy is the
// radius in metres the fix lies within and null when unknown. Count is the trip's
// passenger count at the moment the fix was taken.
public readonly record struct TelemetryFix(DateTime At, double? Speed, double? Accuracy, int Count);

// Which GPS fixes become telemetry rows during a trip.
//
// A moving bus is reported every MovingEverySeconds seconds, which is
// what the fleet map needs to carry its marker smoothly between readings. A standing bus,
// at a stop or in traffic, is reported every StandingEverySeconds: its
// position is not changing, and every row is kept for days.
//
// A bus that pulls away or comes to a stop is reported at once, so the map starts or
// stops its marker within a second of the bus. So is a change in the passenger count,
// which is audit evidence.
//
// Only a fix from the GPS itself is sent. It always carries a speed, zero when
// standing. A fix with no speed came from cell towers or wifi, which can place a bus tens
// of metres from where it is while claiming to be accurate, and is not sent. The trip's
// first row is the exception: the bus has to appear on the map somewhere.
public static class TelemetrySendRule
{
    public const double MovingEverySeconds = 5;
    public const double StandingEverySeconds = 30;

    // Seconds after the last row before pulling away or stopping is reported, so a bus
    // crawling in traffic at about walking pace is not reported every second.
    public const double ChangeAfterSeconds = 2;

    // Metres per second at or above which the bus counts as moving, as on the fleet map.
    public const double MovingSpeed = 1.0;

    // Accuracy radius, in metres, beyond which a fix is not sent.
    public const double PoorAccuracy = 25;

    // The fixes are taken about once a second, so an interval is met slightly early rather
    // than missed by a fraction and sent a second late.
    private const double Slack = 0.5;

    // Whether a fix is good enough to place the bus.
    public static bool Usable(TelemetryFix fix) =>
        fix.Speed is not null && !(fix.Accuracy > PoorAccuracy);

    // Whether this fix should be written, given the last one that was.
    // last: The last fix written for the trip, or null before the first.
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
