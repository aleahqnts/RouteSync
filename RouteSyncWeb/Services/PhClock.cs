namespace FleetWise.Services
{
    // Philippine wall-clock time, a fixed offset with no daylight saving.
    //
    // Timestamps are written as Philippine wall-clock rather than UTC throughout, so dates
    // and ordering do not drift by eight hours between what is stored and what is shown.
    public static class PhClock
    {
        private static readonly TimeZoneInfo Tz = ResolveTz();

        // Manila wall-clock with no kind attached, so it serializes without a zone marker
        // and the database stores the literal time intended.
        public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Tz);

        // The mobile app reads these columns without converting, so the wall-clock value
        // is tagged as UTC and serializes with its digits unchanged. Left unspecified it
        // would be treated as server-local and shifted on the way out.
        public static DateTime NowForDb => DateTime.SpecifyKind(Now, DateTimeKind.Utc);

        // The Philippine calendar date, for date-only columns.
        public static DateTime Today => Now.Date;

        // Converts a real UTC instant to Philippine wall-clock for display. Rows written
        // by the database itself, such as the audit trail, are genuine UTC rather than the
        // wall-clock convention used above, so they need converting.
        public static DateTime ToPh(DateTimeOffset instant) =>
            TimeZoneInfo.ConvertTime(instant, Tz).DateTime;

        // One operating cycle runs 06:00 to 05:59 the next morning, so an overnight
        // night shift counts under its start date. Before 6 AM the current cycle is still
        // yesterday's cycle. Use this (not Today) anywhere a "service day" is meant.
        public static readonly TimeSpan DayStartTime = TimeSpan.FromHours(6);
        public static DateTime OperationalDay => Now.TimeOfDay < DayStartTime ? Today.AddDays(-1) : Today;

        private static TimeZoneInfo ResolveTz()
        {
            // IANA id works cross-platform on .NET 6+; Windows id is the fallback; a fixed
            // +8 offset is the last resort so the app never fails to start over a tz lookup.
            foreach (var id in new[] { "Asia/Manila", "Singapore Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch { /* try next */ }
            }
            return TimeZoneInfo.CreateCustomTimeZone("PH", TimeSpan.FromHours(8), "Philippine Time", "PHT");
        }
    }
}
