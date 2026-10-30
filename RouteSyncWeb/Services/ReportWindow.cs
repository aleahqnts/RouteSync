using System.Globalization;
using FleetWise.Models;

namespace FleetWise.Services
{
    // The days a Reports figure covers and the matching days it is compared with.
    //
    // A period still running is compared like for like: October 1 to 20 against September 1
    // to 20, today up to this hour against yesterday up to the same hour. Comparing a part
    // period with a whole one reads as a collapse every time until the period ends. A period
    // already over is compared whole against the one before it.
    //
    // Days are operational days, and the hour is measured from 06:00, so a shift starting at
    // 22:00 comes after one starting at 14:00 and an hour past midnight comes after both.
    public sealed record ReportWindow(
        DateTime Start,
        DateTime End,
        DateTime LastDay,
        DateTime PrevStart,
        DateTime PrevEnd,
        TimeSpan? Cutoff,
        string VsLabel)
    {
        // The window for a period around anchor, as of now (Philippine time).
        // period: "This Day", "This Week", "Last Week" or "This Month".
        public static ReportWindow For(DateTime anchor, string period, DateTime now)
        {
            anchor = anchor.Date;
            var today = now.TimeOfDay < PhClock.DayStartTime ? now.Date.AddDays(-1) : now.Date;
            var weekStart = anchor.AddDays(-(((int)anchor.DayOfWeek + 6) % 7));

            var (start, end) = period switch
            {
                "This Day" => (anchor, anchor),
                "Last Week" => (weekStart.AddDays(-7), weekStart.AddDays(-1)),
                "This Month" => (new DateTime(anchor.Year, anchor.Month, 1), new DateTime(anchor.Year, anchor.Month, 1).AddMonths(1).AddDays(-1)),
                _ => (weekStart, weekStart.AddDays(6)),
            };

            var running = start <= today && today <= end;
            var lastDay = running ? today : end;
            TimeSpan? cutoff = running ? SinceDayStart(now.TimeOfDay) : null;

            var span = (lastDay - start).Days;
            var prevStart = period == "This Month" ? start.AddMonths(-1) : start.AddDays(-(end - start).Days - 1);
            var prevEnd = prevStart.AddDays(span);
            // Months differ in length: a finished month is compared with the whole month
            // before, and a running one never past that month's last day.
            if (period == "This Month" && (!running || prevEnd >= start)) prevEnd = start.AddDays(-1);

            var vs = "vs " + Days(prevStart, prevEnd) + (cutoff is null ? "" : " by this time");
            return new ReportWindow(start, end, lastDay, prevStart, prevEnd, cutoff, vs);
        }

        // Whether a trip falls in the days counted so far.
        public bool InCurrent(Trip t) => In(t, Start, LastDay);

        // Whether a trip falls in the matching days before.
        public bool InPrevious(Trip t) => In(t, PrevStart, PrevEnd);

        // Whether a day, or a shift on the last day, has not happened yet.
        public bool IsAhead(DateTime day, TimeSpan? shiftStart = null) =>
            day > LastDay || (day == LastDay && Cutoff is { } c && shiftStart is { } s && SinceDayStart(s) > c);

        private bool In(Trip t, DateTime from, DateTime to)
        {
            var d = t.Date.Date;
            if (d < from || d > to) return false;
            return d != to || Cutoff is not { } c || SinceDayStart(t.ShiftStartTime) <= c;
        }

        private static TimeSpan SinceDayStart(TimeSpan clock) =>
            TimeSpan.FromTicks((clock - PhClock.DayStartTime + TimeSpan.FromDays(1)).Ticks % TimeSpan.TicksPerDay);

        private static string Days(DateTime from, DateTime to)
        {
            var c = CultureInfo.InvariantCulture;
            if (from == to) return from.ToString("MMM d", c);
            return from.Month == to.Month
                ? $"{from.ToString("MMM d", c)}-{to.Day}"
                : $"{from.ToString("MMM d", c)} to {to.ToString("MMM d", c)}";
        }
    }
}
