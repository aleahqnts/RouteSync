using FleetWise.Models;

namespace FleetWise.Services
{
    /// <summary>
    /// Timestamps read back from the database as Philippine time, for display.
    /// </summary>
    /// <remarks>
    /// <para>Columns hold one of two conventions, depending on what writes them:</para>
    /// <list type="bullet">
    /// <item><b>Philippine clock time</b>, written by this dashboard and the driver app and
    /// stored as if it were UTC: trip start and end, telemetry, messages, maintenance notes,
    /// items and resolutions, schedule saves, availability, account creation and sign-in.
    /// The stored digits are already Philippine time.</item>
    /// <item><b>True UTC</b>, written by the database clock, the edge functions and the
    /// counter phone: the audit trail, leave requests, inspections submitted through the
    /// driver app, inspection photos, boarding events, roster saves and security incidents.
    /// These are eight hours behind Philippine time.</item>
    /// </list>
    /// <para>Two columns hold both, and the row says which. An inspection the driver skipped
    /// is written by the driver app in Philippine time, where a submitted one comes from the
    /// edge function in UTC. A maintenance order opened by a failed inspection is written by
    /// the edge function in UTC, where one opened here is in Philippine time.</para>
    ///
    /// <para>Postgrest hands a timestamp back moved into the server's own time zone. That is
    /// UTC on the host and Philippine time on a developer's machine, so a value is first
    /// returned to the digits actually stored, and only then read by its convention.</para>
    /// </remarks>
    public static class StoredTimes
    {
        /// <summary>The digits the column holds, whatever time zone the client moved them into.</summary>
        /// <remarks>
        /// The client hands a value back moved into the machine's zone and marked either Local
        /// or Unspecified, depending on the path it took. Both are moved back; only a value
        /// already marked UTC is taken as it is.
        /// </remarks>
        public static DateTime Stored(DateTime fromDb) =>
            fromDb.Kind == DateTimeKind.Utc ? fromDb : fromDb.ToUniversalTime();

        /// <summary>A column written in Philippine clock time.</summary>
        public static DateTime FromWall(DateTime fromDb) =>
            DateTime.SpecifyKind(Stored(fromDb), DateTimeKind.Unspecified);

        /// <summary>A column written in Philippine clock time, as the UTC instant it names.</summary>
        public static DateTime WallAsUtc(DateTime fromDb) =>
            new DateTimeOffset(FromWall(fromDb), TimeSpan.FromHours(8)).UtcDateTime;

        /// <summary>A column written in true UTC, moved to Philippine time.</summary>
        public static DateTime FromUtc(DateTime fromDb) =>
            PhClock.ToPh(new DateTimeOffset(DateTime.SpecifyKind(Stored(fromDb), DateTimeKind.Utc)));

        /// <summary>When an inspection was submitted, or skipped.</summary>
        public static DateTime Inspected(BusChecklist checklist) =>
            string.Equals(checklist.ChecklistStatus, "Skipped", StringComparison.OrdinalIgnoreCase)
                ? FromWall(checklist.SubmittedAt)
                : FromUtc(checklist.SubmittedAt);

        /// <summary>When a maintenance order was opened.</summary>
        public static DateTime Opened(MaintenanceLog log) =>
            log.ChecklistId is not null ? FromUtc(log.CreatedAt) : FromWall(log.CreatedAt);
    }
}
