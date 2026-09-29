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

    // A boarding at the given Philippine time, stored as the device sends it, in true UTC.
    private static BoardingEvent In(string trip, int hour, int minute = 0) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TripId = trip,
        Direction = "in",
        DeviceTimestamp = new DateTimeOffset(Day.AddHours(hour).AddMinutes(minute), TimeSpan.FromHours(8)),
    };

    [Fact]
    public void A_boarding_counts_in_the_Philippine_hour_it_happened()
    {
        var trips = new[] { Trip("T1", 2) };
        var events = new[] { In("T1", 6, 30), In("T1", 8, 59) };

        var hours = HourlyBoardings.ByHour(Day, trips, events, Day.AddDays(1).AddHours(6));

        Assert.Equal(1, hours[0]);   // 6:00 to 7:00
        Assert.Equal(1, hours[2]);   // 8:00 to 9:00
        Assert.Equal(2, hours.Sum(h => h ?? 0));
    }

    [Fact]
    public void Passengers_added_by_hand_count_in_the_hour_the_trip_ended()
    {
        var trips = new[] { Trip("T1", 5, end: Day.AddHours(13).AddMinutes(40)) };
        var events = new[] { In("T1", 7) };

        var hours = HourlyBoardings.ByHour(Day, trips, events, Day.AddDays(1).AddHours(6));

        Assert.Equal(1, hours[1]);   // 7:00 to 8:00, from the camera
        Assert.Equal(4, hours[7]);   // 13:00 to 14:00, where the trip ended
        Assert.Equal(5, hours.Sum(h => h ?? 0));
    }

    [Fact]
    public void Hours_still_to_come_are_empty_rather_than_zero()
    {
        var now = Day.AddHours(9).AddMinutes(15);
        var hours = HourlyBoardings.ByHour(Day, new[] { Trip("T1", 0, "Active") }, Array.Empty<BoardingEvent>(), now);

        Assert.Equal(0, hours[3]);   // 9:00 to 10:00, under way
        Assert.Null(hours[4]);       // 10:00 onward, not started
    }

    [Fact]
    public void Boardings_on_another_route_are_left_out()
    {
        var hours = HourlyBoardings.ByHour(Day, new[] { Trip("T1", 1) }, new[] { In("T1", 7), In("T9", 7) }, Day.AddDays(1).AddHours(6));

        Assert.Equal(1, hours[1]);
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
