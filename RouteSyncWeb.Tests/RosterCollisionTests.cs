using FleetWise.Models;
using FleetWise.Services;
using static FleetWise.Services.RosterRules;

namespace RouteSyncWeb.Tests;

/// <summary>
/// Rest days a single floater cannot cover because of when they fall, not how many there are.
/// </summary>
/// <remarks>
/// The October 2026 roster published with every Wednesday Morning empty on two buses. On each
/// route a Morning driver rested Wednesday, an Evening driver rested Tuesday, and one floater
/// covered both: the Tuesday Evening, then nothing, because a Morning straight after an Evening
/// breaks the rest between shifts. A weekday head count saw one resting and one working on each
/// day and called it covered.
/// </remarks>
public class RosterCollisionTests
{
    private const int North = 1;

    private static readonly Dictionary<int, string> Routes = new() { [North] = "North Loop" };

    private const int Aleah = 1;      // V001 Morning, rests Wednesday
    private const int Evening = 2;    // V002 Evening, rests Tuesday
    private const int Floater = 10;   // rests Monday
    private const int Spare = 20;     // on the books, on no route

    private static List<Vehicle> Buses(int count = 2) =>
        Enumerable.Range(1, count).Select(n => new Vehicle { VehicleId = $"V{n:000}", RouteId = North }).ToList();

    private static List<UserModel> Drivers(params int[] ids) =>
        ids.Select(id => new UserModel { UserId = id, FirstName = "D" + id, LastName = "Cruz", RoleId = 2, AccountStatus = "Activated" }).ToList();

    private static List<RosterSeat> October() => new()
    {
        new(Aleah, Crew, North, "V001", "Morning", 3),
        new(Evening, Crew, North, "V002", "Evening", 2),
        new(Floater, RosterRules.Floater, North, null, "Morning", 1),
    };

    [Fact]
    public void An_Evening_rest_day_then_a_Morning_one_leaves_the_Morning_empty_every_week()
    {
        var weekly = RosterStructure.WeeklyGaps(October(), Drivers(Aleah, Evening, Floater), Buses(), Routes);

        var gap = Assert.Single(weekly);
        Assert.Equal(3, gap.Weekday);
        Assert.Equal("V001", gap.VehicleId);
        Assert.Equal("Morning", gap.Shift);
        Assert.True(gap.RestDay);
        Assert.Contains("just off a shift", gap.Reason);
    }

    [Fact]
    public void The_strip_calls_the_day_short_where_a_head_count_called_it_covered()
    {
        var seats = October();
        var weekly = RosterStructure.WeeklyGaps(seats, Drivers(Aleah, Evening, Floater), Buses(), Routes);

        var wednesday = Capacity(North, seats, weekly)[2];

        // One resting, one floater working: what the old strip counted as covered.
        Assert.Equal(1, wednesday.Resting);
        Assert.Equal(1, wednesday.Covering);
        Assert.True(wednesday.Short);

        var s = Shortfall(North, seats, weekly);
        Assert.Equal(0, s.FloatersMissing);
        Assert.Equal(1, s.GapsPerWeek);
        Assert.True(s.RestDaysCollide);
    }

    [Fact]
    public void Suggest_moves_one_rest_day_to_the_nearest_quiet_day_and_no_other()
    {
        var fix = SuggestRestDays(October(), Buses(), Drivers(Aleah, Evening, Floater), Routes, new HashSet<int>());

        Assert.Equal(0, fix.GapsLeft);
        Assert.Equal(1, fix.Moved);
        Assert.Equal(0, fix.Added);

        var byDriver = fix.Seats.ToDictionary(s => s.DriverId!.Value);
        // Thursday over Tuesday: as near to Wednesday, and nobody else rests on it.
        Assert.Equal(4, byDriver[Aleah].RestWeekday);
        Assert.Equal(2, byDriver[Evening].RestWeekday);
        Assert.Equal(1, byDriver[Floater].RestWeekday);

        Assert.StartsWith("Rests Thursday", byDriver[Aleah].Suggested);
        Assert.Contains("Moved D1 Cruz's rest day from Wednesday to Thursday.", fix.Notes);
    }

    [Fact]
    public void Suggest_adds_a_spare_driver_before_moving_anybody()
    {
        var fix = SuggestRestDays(October(), Buses(), Drivers(Aleah, Evening, Floater, Spare), Routes, new HashSet<int>());

        Assert.Equal(0, fix.GapsLeft);
        Assert.Equal(1, fix.Added);
        Assert.Equal(0, fix.Moved);

        var added = Assert.Single(fix.Seats, s => s.DriverId == Spare);
        Assert.Equal(RosterRules.Floater, added.Kind);
        Assert.Equal(North, added.RouteId);
        Assert.NotNull(added.RestWeekday);
        Assert.True(MarkIsAboutDriver(added.Suggested));

        // Everybody else keeps the rest day they had.
        var byDriver = fix.Seats.ToDictionary(s => s.DriverId!.Value);
        Assert.Equal(3, byDriver[Aleah].RestWeekday);
        Assert.Equal(2, byDriver[Evening].RestWeekday);
    }

    [Fact]
    public void A_held_back_driver_is_never_offered_as_the_spare()
    {
        var fix = SuggestRestDays(October(), Buses(), Drivers(Aleah, Evening, Floater, Spare), Routes, new HashSet<int> { Spare });

        Assert.Equal(0, fix.Added);
        Assert.Equal(1, fix.Moved);
        Assert.DoesNotContain(fix.Seats, s => s.DriverId == Spare);
    }

    [Fact]
    public void A_route_no_arrangement_can_cover_says_it_needs_another_floater()
    {
        // Seven Morning crews, a rest day each, and one floater who can work at most six days
        // running and only one Morning a day. No arrangement of rest days covers the seventh.
        var seats = Enumerable.Range(1, 7)
            .Select(n => new RosterSeat(n, Crew, North, $"V{n:000}", "Morning", n))
            .Append(new RosterSeat(Floater, RosterRules.Floater, North, null, "Morning", 1))
            .ToList();
        var drivers = Drivers(1, 2, 3, 4, 5, 6, 7, Floater);

        var fix = SuggestRestDays(seats, Buses(7), drivers, Routes, new HashSet<int>());

        Assert.True(fix.GapsLeft > 0);
        Assert.Contains(fix.Unmarked, n => n.EndsWith("It needs another floater."));
    }

    [Fact]
    public void A_roster_that_already_works_comes_back_unchanged()
    {
        var seats = new List<RosterSeat>
        {
            new(Aleah, Crew, North, "V001", "Morning", 4),
            new(Evening, Crew, North, "V002", "Evening", 2),
            new(Floater, RosterRules.Floater, North, null, "Morning", 1),
        };

        var fix = SuggestRestDays(seats, Buses(), Drivers(Aleah, Evening, Floater), Routes, new HashSet<int>());

        Assert.False(fix.Changed);
        Assert.Equal(seats.Select(s => s.RestWeekday), fix.Seats.Select(s => s.RestWeekday));
    }

    // ---- Auto-fill -------------------------------------------------------------------------

    [Fact]
    public void Auto_fill_never_places_a_held_back_driver()
    {
        var seats = new List<RosterSeat>
        {
            new(null, Crew, North, "V001", "Morning", null),
            new(Floater, RosterRules.Floater, North, null, "Morning", 1),
        };
        var drivers = Drivers(Floater, Spare);

        var held = RosterRules.AutoFill(seats, Buses(), drivers, Routes, RosterHistory.None, new HashSet<int> { Spare });
        Assert.Contains(held.Seats, s => s.Kind == Crew && s.DriverId is null);

        var free = RosterRules.AutoFill(seats, Buses(), drivers, Routes, RosterHistory.None, new HashSet<int>());
        Assert.Contains(free.Seats, s => s.Kind == Crew && s.DriverId == Spare);
    }

    [Fact]
    public void Auto_fill_settles_a_new_rest_day_away_from_a_collision_and_moves_no_existing_one()
    {
        // Counted by spare floaters, Aleah's missing rest day would land on Wednesday, the day
        // after the Evening driver's, which one floater cannot cover.
        var seats = new List<RosterSeat>
        {
            new(Aleah, Crew, North, "V001", "Morning", null),
            new(Evening, Crew, North, "V002", "Evening", 2),
            new(Floater, RosterRules.Floater, North, null, "Morning", 1),
        };

        var fill = RosterRules.AutoFill(seats, Buses(), Drivers(Aleah, Evening, Floater), Routes, RosterHistory.None);
        var byDriver = fill.Seats.ToDictionary(s => s.DriverId!.Value);

        Assert.NotEqual(3, byDriver[Aleah].RestWeekday);
        Assert.Equal(2, byDriver[Evening].RestWeekday);
        Assert.Equal(1, byDriver[Floater].RestWeekday);
        Assert.Equal(0, RosterStructure.RestDayGapCount(fill.Seats, Drivers(Aleah, Evening, Floater), Buses(), Routes));
    }
}
