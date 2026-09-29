using FleetWise.Models;
using FleetWise.Services;

namespace RouteSyncWeb.Tests;

public class HourlyBoardingsTests
{
    private static readonly DateTime Day = new(2026, 9, 29);

    private static Trip Trip(string id, int boarded, string status = "Completed", DateTime? end = null) => new()
    {
        TripId = id,
        Date = Day,
        ShiftType = "Morning",
        ShiftStartTime = TripStatus.Windows["Morning"].Start,
        ShiftEndTime = TripStatus.Windows["Morning"].End,
        TotalBoarded = boarded,
        TripStatus = status,
        // As Postgrest hands it over: the stored Philippine digits read as UTC and moved to
        // local time, which ToUniversalTime turns back into those digits.
        ActualEndTime = end is DateTime e ? DateTime.SpecifyKind(e, DateTimeKind.Utc).ToLocalTime() : null,
    };

    // Boardings the database counted in the Philippine hour given, which it returns as an
    // instant in UTC.
    private static BoardedHour At(string trip, int hour, int boarded = 1) =>
        new(trip, new DateTimeOffset(Day.AddHours(hour), TimeSpan.FromHours(8)).ToUniversalTime(), boarded);

    [Fact]
    public void A_boarding_counts_in_the_Philippine_hour_it_happened()
    {
        var trips = new[] { Trip("T1", 2) };
        var boarded = new[] { At("T1", 6), At("T1", 8) };

        var hours = HourlyBoardings.ByHour(Day, trips, boarded, Day.AddDays(1).AddHours(6));

        Assert.Equal(1, hours[0]);   // 6:00 to 7:00
        Assert.Equal(1, hours[2]);   // 8:00 to 9:00
        Assert.Equal(2, hours.Sum(h => h ?? 0));
    }

    [Fact]
    public void Passengers_added_by_hand_count_in_the_hour_the_trip_ended()
    {
        var trips = new[] { Trip("T1", 5, end: Day.AddHours(13).AddMinutes(40)) };
        var boarded = new[] { At("T1", 7) };

        var hours = HourlyBoardings.ByHour(Day, trips, boarded, Day.AddDays(1).AddHours(6));

        Assert.Equal(1, hours[1]);   // 7:00 to 8:00, from the camera
        Assert.Equal(4, hours[7]);   // 13:00 to 14:00, where the trip ended
        Assert.Equal(5, hours.Sum(h => h ?? 0));
    }

    [Fact]
    public void Hours_still_to_come_are_empty_rather_than_zero()
    {
        var now = Day.AddHours(9).AddMinutes(15);
        var hours = HourlyBoardings.ByHour(Day, new[] { Trip("T1", 0, "Active") }, Array.Empty<BoardedHour>(), now);

        Assert.Equal(0, hours[3]);   // 9:00 to 10:00, under way
        Assert.Null(hours[4]);       // 10:00 onward, not started
    }

    [Fact]
    public void Boardings_on_another_route_are_left_out()
    {
        var hours = HourlyBoardings.ByHour(Day, new[] { Trip("T1", 1) }, new[] { At("T1", 7), At("T9", 7) }, Day.AddDays(1).AddHours(6));

        Assert.Equal(1, hours[1]);
    }

    [Fact]
    public void Boardings_before_the_day_began_are_not_taken_for_hand_added_ones()
    {
        // A trip still running from the night before: two boarded at 5 AM, before the day's
        // first hour, and one at 7 AM.
        var trips = new[] { Trip("T1", 5, end: Day.AddHours(13).AddMinutes(40)) };
        var boarded = new[] { At("T1", 5, 2), At("T1", 7) };

        var hours = HourlyBoardings.ByHour(Day, trips, boarded, Day.AddDays(1).AddHours(6));

        Assert.Equal(1, hours[1]);   // 7:00 to 8:00
        Assert.Equal(2, hours[7]);   // the two added by hand, where the trip ended
        Assert.Equal(3, hours.Sum(h => h ?? 0));
    }

    [Fact]
    public void The_database_count_is_read_as_the_hour_it_names()
    {
        var parsed = HourlyBoardings.ParseHours(
            "[{\"trip_id\":\"T1\",\"hour_start\":\"2026-09-28T22:00:00+00:00\",\"boarded\":3}]");

        var row = Assert.Single(parsed);
        Assert.Equal("T1", row.TripId);
        Assert.Equal(3, row.Boarded);

        var hours = HourlyBoardings.ByHour(Day, new[] { Trip("T1", 3) }, parsed, Day.AddDays(1).AddHours(6));
        Assert.Equal(3, hours[0]);   // 22:00 UTC is 6:00 to 7:00 in Manila

        Assert.Empty(HourlyBoardings.ParseHours("[]"));
        Assert.Empty(HourlyBoardings.ParseHours(null));
    }

    [Fact]
    public void The_usual_day_leaves_out_days_nobody_boarded()
    {
        var busy = new int?[24];
        busy[2] = 10;
        var quiet = new int?[24];
        quiet[3] = 4;
        var empty = new int?[24];

        var usual = HourlyBoardings.Usual(new[] { busy, empty });
        Assert.NotNull(usual);
        Assert.Equal(10, usual![2]);

        var both = HourlyBoardings.Usual(new[] { busy, quiet });
        Assert.Equal(5, both![2]);
        Assert.Equal(2, both[3]);

        Assert.Null(HourlyBoardings.Usual(new[] { empty }));
    }
}
