using FleetWise.Models;
using FleetWise.Services;

namespace RouteSyncWeb.Tests;

public class BreakSlotTests
{
    private static TimeSpan T(int h) => TimeSpan.FromHours(h);

    [Theory]
    [InlineData("Morning", 9, 10, 11)]
    [InlineData("Afternoon", 17, 18, 19)]
    [InlineData("Evening", 1, 2, 3)]
    public void Each_shift_has_three_slots_three_to_five_hours_in(string shift, int a, int b, int c)
    {
        var start = TripStatus.Windows[shift].Start;

        Assert.Equal(new[] { T(a), T(b), T(c) }, BreakSlots.For(start));
        Assert.True(BreakSlots.IsSlot(start, T(b)));
        Assert.False(BreakSlots.IsSlot(start, start));
    }

    [Fact]
    public void A_new_trip_takes_the_least_used_slot_and_the_earliest_on_a_tie()
    {
        var morning = T(6);

        Assert.Equal(T(9), BreakSlots.LeastUsed(morning, Array.Empty<TimeSpan?>()));
        Assert.Equal(T(10), BreakSlots.LeastUsed(morning, new TimeSpan?[] { T(9) }));
        Assert.Equal(T(11), BreakSlots.LeastUsed(morning, new TimeSpan?[] { T(9), T(10) }));
        Assert.Equal(T(9), BreakSlots.LeastUsed(morning, new TimeSpan?[] { T(9), T(10), T(11) }));

        // A trip from before breaks existed takes no slot, and a gap left by a deleted trip
        // is filled before a slot already doubled up.
        Assert.Equal(T(10), BreakSlots.LeastUsed(morning, new TimeSpan?[] { null, T(9), T(9), T(11) }));
    }

    [Fact]
    public void An_Evening_break_falls_on_the_day_after_the_trip_date()
    {
        var trip = new Trip
        {
            Date = new DateTime(2026, 9, 15),
            ShiftType = "Evening",
            ShiftStartTime = T(22),
            ShiftEndTime = T(6),
            BreakStart = T(2),
        };

        Assert.Equal((new DateTime(2026, 9, 16, 2, 0, 0), new DateTime(2026, 9, 16, 3, 0, 0)), BreakSlots.WindowOf(trip));
        Assert.True(BreakSlots.IsOnBreak(trip, new DateTime(2026, 9, 16, 2, 30, 0)));
        Assert.False(BreakSlots.IsOnBreak(trip, new DateTime(2026, 9, 15, 2, 30, 0)));
        Assert.False(BreakSlots.IsOnBreak(trip, new DateTime(2026, 9, 16, 3, 0, 0)));
        Assert.Equal("2:00 AM to 3:00 AM", BreakSlots.Label(T(2)));
    }

    [Fact]
    public void A_trip_with_no_slot_is_never_on_break()
    {
        var trip = new Trip { Date = new DateTime(2026, 9, 15), ShiftStartTime = T(6), ShiftEndTime = T(14) };

        Assert.Null(BreakSlots.WindowOf(trip));
        Assert.False(BreakSlots.IsOnBreak(trip, new DateTime(2026, 9, 15, 9, 30, 0)));
    }
}
