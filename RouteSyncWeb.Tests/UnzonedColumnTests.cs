using FleetWise.Models;
using Newtonsoft.Json;

namespace RouteSyncWeb.Tests;

/// <summary>
/// Columns with no time zone are written through the Postgrest client's own serializer
/// settings, which convert every value to UTC and treat an unspecified one as local time.
/// </summary>
/// <remarks>
/// On a machine set to a zone ahead of UTC, such as Philippine time, an unnormalized date
/// serializes as the previous day. On a UTC machine these tests pass either way; run here,
/// they are what catches the shift.
/// </remarks>
public class UnzonedColumnTests
{
    private static readonly JsonSerializerSettings Settings =
        Postgrest.Client.SerializerSettings(new Postgrest.ClientOptions());

    private static string Write(object model) => JsonConvert.SerializeObject(model, Settings);

    public static TheoryData<DateTimeKind> Kinds => new()
    {
        DateTimeKind.Unspecified,
        DateTimeKind.Local,
        DateTimeKind.Utc,
    };

    [Theory, MemberData(nameof(Kinds))]
    public void A_trip_date_is_written_as_its_calendar_day(DateTimeKind kind)
    {
        var trip = new Trip { Date = DateTime.SpecifyKind(new DateTime(2026, 9, 15), kind) };

        Assert.Contains("\"date\":\"2026-09-15T00:00:00Z\"", Write(trip));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Other_date_columns_are_written_as_their_calendar_day(DateTimeKind kind)
    {
        DateTime Day(int d) => DateTime.SpecifyKind(new DateTime(2026, 9, d), kind);

        Assert.Contains("\"last_maintenance_date\":\"2026-09-15T00:00:00Z\"",
                        Write(new Vehicle { LastMaintenanceDate = Day(15) }));

        // The week start is the table's key, which the client sends only on an upsert and
        // leaves out of a plain serialization, so the normalized value is checked directly.
        var week = new ScheduleWeek { WeekStart = Day(14) };
        Assert.Equal(DateTimeKind.Utc, week.WeekStart.Kind);
        Assert.Equal(new DateTime(2026, 9, 14), week.WeekStart);

        var leave = Write(new LeaveRequest { StartDate = Day(15), EndDate = Day(16) });
        Assert.Contains("\"start_date\":\"2026-09-15T00:00:00Z\"", leave);
        Assert.Contains("\"end_date\":\"2026-09-16T00:00:00Z\"", leave);
    }

    [Fact]
    public void A_timestamp_without_time_zone_keeps_its_wall_clock_digits()
    {
        var availability = new DriverAvailability { UpdatedAt = new DateTime(2026, 9, 15, 9, 45, 0) };

        Assert.Contains("\"updated_at\":\"2026-09-15T09:45:00Z\"", Write(availability));
    }

    [Fact]
    public void A_date_read_back_is_written_out_unchanged()
    {
        var trip = JsonConvert.DeserializeObject<Trip>("{\"date\":\"2026-09-15\"}", Settings)!;

        Assert.Equal(new DateTime(2026, 9, 15), trip.Date);
        Assert.Contains("\"date\":\"2026-09-15T00:00:00Z\"", Write(trip));
    }

    [Fact]
    public void A_missing_maintenance_date_stays_missing()
    {
        Assert.Contains("\"last_maintenance_date\":null", Write(new Vehicle { LastMaintenanceDate = null }));
    }
}
