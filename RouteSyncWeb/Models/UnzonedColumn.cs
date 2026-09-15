namespace FleetWise.Models;

/// <summary>
/// Prepares values for Postgres columns that carry no time zone: <c>date</c> and
/// <c>timestamp without time zone</c>.
/// </summary>
/// <remarks>
/// The Postgrest client writes every <see cref="DateTime"/> through a converter that turns
/// it into UTC, and it treats a value of unspecified kind as local time. A column with no
/// zone keeps only the digits that arrive, so on a machine set to Philippine time a trip
/// dated the 15th is sent as 16:00 on the 14th and stored as the 14th. A server running in
/// UTC converts nothing, which is why the difference shows only on a machine in another
/// zone.
///
/// Marking the value as UTC makes that conversion a no-op on every machine, so the column
/// always receives the calendar day or wall-clock time the code holds, exactly as a UTC
/// server already writes it.
///
/// Applied in the setters of the affected model properties rather than at each write:
/// values read back from the database arrive with an unspecified kind, and a whole-row
/// write sends them straight back out. The one path a setter cannot see is
/// <c>Set(x =&gt; x.Column, value)</c>, which serializes the value it is handed, so pass it
/// a value from these methods.
/// </remarks>
public static class UnzonedColumn
{
    /// <summary>A calendar day, for a <c>date</c> column.</summary>
    public static DateTime Day(DateTime value) =>
        DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);

    /// <inheritdoc cref="Day(DateTime)"/>
    public static DateTime? Day(DateTime? value) =>
        value is DateTime v ? Day(v) : null;

    /// <summary>A wall-clock time, for a <c>timestamp without time zone</c> column.</summary>
    public static DateTime WallClock(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <inheritdoc cref="WallClock(DateTime)"/>
    public static DateTime? WallClock(DateTime? value) =>
        value is DateTime v ? WallClock(v) : null;
}
