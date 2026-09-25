using FleetWise.Models;
using FleetWise.Services;
using static FleetWise.Services.RosterRules;

namespace RouteSyncWeb.Tests;

public class RosterRulesTests
{
    private const int North = 1;
    private const int Second = 2;

    private static readonly Dictionary<int, string> Routes = new() { [North] = "North Loop", [Second] = "Second Route" };

    private static RosterSeat CrewSeat(int? driver, string bus, string shift, int? rest = null, int route = North) =>
        new(driver, Crew, route, bus, shift, rest);

    private static RosterSeat FloaterSeat(int? driver, int? rest = null, string home = "Morning", int route = North) =>
        new(driver, Floater, route, null, home, rest);

    private static List<UserModel> Drivers(params int[] ids) =>
        ids.Select(id => new UserModel { UserId = id, FirstName = "D" + id, LastName = "Cruz", RoleId = 2, AccountStatus = "Activated" }).ToList();

    private static List<Vehicle> Buses() => new()
    {
        new Vehicle { VehicleId = "V001", RouteId = North },
        new Vehicle { VehicleId = "V002", RouteId = North },
        new Vehicle { VehicleId = "V009", RouteId = Second },
        new Vehicle { VehicleId = "V666", RouteId = North, RetiredAt = new DateTime(2026, 1, 1) },
    };

    [Fact]
    public void A_complete_roster_has_no_problems()
    {
        var seats = new List<RosterSeat>
        {
            CrewSeat(1, "V001", "Morning", 2),
            CrewSeat(2, "V001", "Afternoon", 3),
            FloaterSeat(3, 5),
        };

        Assert.Empty(Problems(seats, Buses(), Drivers(1, 2, 3), Routes));
    }

    [Fact]
    public void Every_rule_on_save_is_named()
    {
        var drivers = Drivers(1, 2, 3, 4);
        drivers.Add(new UserModel { UserId = 5, FirstName = "Gone", LastName = "Cruz", RoleId = 2, AccountStatus = "Deactivated" });

        var seats = new List<RosterSeat>
        {
            CrewSeat(1, "V001", "Morning", 2),
            FloaterSeat(1, 4),                          // placed twice
            CrewSeat(null, "V001", "Afternoon"),        // a shift the bus runs, nobody on it: allowed
            CrewSeat(2, "V002", "Morning"),             // no rest day
            CrewSeat(3, "V666", "Morning", 1),          // retired bus
            CrewSeat(4, "V009", "Morning", 1),          // bus based on another route
            FloaterSeat(5, 6),                          // inactive account
        };

        var problems = Problems(seats, Buses(), drivers, Routes);

        Assert.Contains("D1 Cruz is placed more than once: V001 on the Morning shift and a North Loop floater.", problems);
        Assert.Contains("D2 Cruz has no rest day.", problems);
        Assert.Contains("V666 is retired and cannot hold a crew.", problems);
        Assert.Contains("V009 is not based on North Loop. Move its crew to the route it runs.", problems);
        Assert.Contains("Gone Cruz is not an active driver.", problems);
        Assert.Equal(5, problems.Count);
    }

    [Fact]
    public void A_bus_shift_with_nobody_on_it_does_not_stop_a_save()
    {
        var seats = new List<RosterSeat>
        {
            CrewSeat(1, "V001", "Morning", 2),
            CrewSeat(null, "V001", "Afternoon"),
            FloaterSeat(2, 5),
        };

        Assert.Empty(Problems(seats, Buses(), Drivers(1, 2), Routes));
        Assert.Equal(1, Shortfall(North, seats, Array.Empty<WeeklyGap>()).UnfilledSeats);
    }

    private static IReadOnlyList<WeeklyGap> Weekly(List<RosterSeat> seats, List<UserModel> drivers) =>
        RosterStructure.WeeklyGaps(seats, drivers, Buses(), Routes);

    [Fact]
    public void Two_drivers_on_one_bus_shift_are_refused()
    {
        var seats = new List<RosterSeat> { CrewSeat(1, "V001", "Morning", 2), CrewSeat(2, "v001", "Morning", 3) };

        Assert.Contains("V001 has more than one driver on the Morning shift.", Problems(seats, Buses(), Drivers(1, 2), Routes));
    }

    [Fact]
    public void The_capacity_strip_counts_crew_and_floaters_and_takes_short_days_from_the_planner()
    {
        var seats = new List<RosterSeat>
        {
            CrewSeat(1, "V001", "Morning", 1),
            CrewSeat(2, "V001", "Afternoon", 1),
            CrewSeat(3, "V002", "Morning", 2),
            CrewSeat(null, "V002", "Afternoon"),     // not counted until it has a driver
            FloaterSeat(4, 2),
            CrewSeat(5, "V009", "Morning", 1, Second),
        };
        var drivers = Drivers(1, 2, 3, 4, 5);

        var strip = Capacity(North, seats, Weekly(seats, drivers));

        // Monday: the floater takes the Morning and is then too soon after it for the Afternoon.
        Assert.Equal(new DayCover(1, 2, 1, true), strip[0]);
        // Tuesday: the floater's own rest day.
        Assert.Equal(new DayCover(2, 1, 0, true), strip[1]);
        Assert.Equal(new DayCover(3, 0, 1, false), strip[2]);
    }

    [Fact]
    public void The_shortfall_counts_the_rest_days_the_planner_cannot_cover()
    {
        // Seven bus shifts usually take two floaters; one is placed and one shift has no driver.
        var seats = new List<RosterSeat>
        {
            CrewSeat(1, "V001", "Morning", 1),
            CrewSeat(2, "V001", "Afternoon", 1),
            CrewSeat(3, "V001", "Evening", 3),
            CrewSeat(4, "V002", "Morning", 4),
            CrewSeat(5, "V002", "Afternoon", 5),
            CrewSeat(6, "V002", "Evening", 6),
            CrewSeat(null, "V003", "Morning"),
            FloaterSeat(7, 2),
        };

        var s = Shortfall(North, seats, Weekly(seats, Drivers(1, 2, 3, 4, 5, 6, 7)));

        Assert.Equal(7, s.CrewSeats);
        Assert.Equal(2, s.FloatersNeeded);
        Assert.Equal(1, s.Floaters);
        Assert.Equal(2, s.DriversShort);
        Assert.True(s.GapsPerWeek > 0);
        Assert.False(s.RestDaysCollide);   // short of floaters, which is its own sentence
    }

    [Fact]
    public void Suggest_sets_missing_rest_days_and_leaves_the_rest_where_they_are()
    {
        var seats = new List<RosterSeat>
        {
            FloaterSeat(10, 1), FloaterSeat(11, 4),
            CrewSeat(1, "V001", "Morning", 6),
            CrewSeat(2, "V001", "Afternoon"),
            CrewSeat(3, "V002", "Morning"),
            CrewSeat(null, "V002", "Afternoon"),
        };

        var fix = SuggestRestDays(seats, Buses(), Drivers(1, 2, 3, 10, 11), Routes, new HashSet<int>());
        var byDriver = fix.Seats.Where(s => s.DriverId is not null).ToDictionary(s => s.DriverId!.Value);

        Assert.Equal(1, byDriver[10].RestWeekday);
        Assert.Equal(4, byDriver[11].RestWeekday);
        Assert.Equal(6, byDriver[1].RestWeekday);
        Assert.NotNull(byDriver[2].RestWeekday);
        Assert.NotNull(byDriver[3].RestWeekday);
        // Crewmates on one bus do not rest together.
        Assert.NotEqual(byDriver[1].RestWeekday, byDriver[2].RestWeekday);
        Assert.Equal(2, fix.RestDaysSet);
        Assert.Equal(0, fix.Moved);

        // A bus shift with nobody on it is left as it was.
        Assert.Contains(fix.Seats, s => s.DriverId is null && s.RestWeekday is null);
    }

    [Fact]
    public void Suggest_leaves_floaters_usual_shifts_alone()
    {
        var seats = new List<RosterSeat>
        {
            FloaterSeat(10, 1, home: "Evening"), FloaterSeat(11, 4, home: "Evening"),
            CrewSeat(1, "V001", "Morning", 2), CrewSeat(2, "V002", "Morning", 3),
        };

        var fix = SuggestRestDays(seats, Buses(), Drivers(1, 2, 10, 11), Routes, new HashSet<int>());

        Assert.All(fix.Seats.Where(s => s.Kind == Floater), s => Assert.Equal("Evening", s.Shift));
    }

    [Fact]
    public void Unplaced_lists_active_drivers_with_no_place()
    {
        var drivers = Drivers(1, 2, 3);
        drivers.Add(new UserModel { UserId = 4, FirstName = "Gone", RoleId = 2, AccountStatus = "Deactivated" });
        drivers.Add(new UserModel { UserId = 5, FirstName = "Admin", RoleId = 1, AccountStatus = "Activated" });

        var unplaced = Unplaced(new List<RosterSeat> { CrewSeat(1, "V001", "Morning", 1), FloaterSeat(null) }, drivers);

        Assert.Equal(new[] { 2, 3 }, unplaced.Select(d => d.UserId));
    }
}
