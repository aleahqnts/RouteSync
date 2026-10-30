namespace FleetWise.Models;

// Prepares values for Postgres columns that carry no time zone: date and
// timestamp without time zone.
//
// The Postgrest client writes every DateTime through a converter that turns
// it into UTC, and it treats a value of unspecified kind as local time. A column with no
// zone keeps only the digits that arrive, so on a machine set to Philippine time a trip
// dated the 15th is sent as 16:00 on the 14th and stored as the 14th. A server running in
// UTC converts nothing, which is why the difference shows only on a machine in another
// zone.
//
// Marking the value as UTC makes that conversion a no-op on every machine, so the column
// always receives the calendar day or wall-clock time the code holds, exactly as a UTC
// server already writes it.
//
// Applied in the setters of the affected model properties rather than at each write:
// values read back from the database arrive with an unspecified kind, and a whole-row
// write sends them straight back out. The one path a setter cannot see is
// Set(x => x.Column, value), which serializes the value it is handed, so pass it
// a value from these methods.
public static class UnzonedColumn
{
    // A calendar day, for a date column.
    public static DateTime Day(DateTime value) =>
        DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);

    // Same as Day(DateTime).
    public static DateTime? Day(DateTime? value) =>
        value is DateTime v ? Day(v) : null;

    // A wall-clock time, for a timestamp without time zone column.
    public static DateTime WallClock(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc);

    // Same as WallClock(DateTime).
    public static DateTime? WallClock(DateTime? value) =>
        value is DateTime v ? WallClock(v) : null;
}
