namespace FleetWiseMobile.Services;

// Philippine time, UTC+8 with no daylight saving, used everywhere the app needs the
// current time.
//
// The system operates in one time zone, so Philippine wall-clock time is both stored and
// displayed directly rather than converted at each boundary.
public static class PhTime
{
    private static readonly TimeZoneInfo Tz = Resolve();

    public static DateTime Now => At(DateTimeOffset.UtcNow);

    // A moment, such as when a GPS fix was taken, on the Philippine clock.
    public static DateTime At(DateTimeOffset moment) => TimeZoneInfo.ConvertTime(moment, Tz).DateTime;

    // When a service day begins.
    public static readonly TimeSpan DayStartTime = TimeSpan.FromHours(6);

    // The service day now in progress, which runs from six in the morning to just
    // before six the next.
    //
    // The same rule as PhClock.OperationalDay on the dashboard. A shift that starts at
    // ten at night belongs to the day it started, so at two in the morning the day still
    // being worked is yesterday by the calendar, and anything asking "is this day still
    // live" has to agree with the board the dispatcher is looking at.
    public static DateTime OperationalDay =>
        Now.TimeOfDay < DayStartTime ? Now.Date.AddDays(-1) : Now.Date;

    // Philippine wall-clock time is written into timestamptz columns, which the database
    // records as +00. On the way back, postgrest converts to the device's local time,
    // adding eight hours, and tags the result as either Local or Unspecified depending on
    // the path taken.
    //
    // This recovers the wall-clock value that was actually stored:
    //   Utc              already the stored wall-clock, used as-is
    //   Local or Unspec  device-local, converted back to strip the added offset
    public static DateTime Raw(DateTime dt) => dt.Kind == DateTimeKind.Utc
        ? dt
        : DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();

    // A column written in true UTC, as Philippine time.
    //
    // The database clock and the edge functions stamp in UTC: inspections submitted
    // through the app, leave filed and answered, the audit trail.
    public static DateTime FromUtc(DateTime dt) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(Raw(dt), DateTimeKind.Utc)), Tz).DateTime;

    // When an inspection was submitted, or skipped, as Philippine time.
    //
    // A skipped inspection is written by this app in Philippine time, a submitted one by
    // the edge function in UTC, so the row's status says which.
    public static DateTime Inspected(FleetWiseMobile.Models.BusChecklist checklist) =>
        string.Equals(checklist.ChecklistStatus, "Skipped", StringComparison.OrdinalIgnoreCase)
            ? Raw(checklist.SubmittedAt)
            : FromUtc(checklist.SubmittedAt);

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "Asia/Manila", "Singapore Standard Time", "Taipei Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* try next */ }
        }
        return TimeZoneInfo.CreateCustomTimeZone("PH", TimeSpan.FromHours(8), "PH", "PH");
    }
}
