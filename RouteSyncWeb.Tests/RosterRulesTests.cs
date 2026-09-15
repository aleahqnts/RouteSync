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
            CrewSeat(null, "V001", "Afternoon"),        // a shift the bus runs, nobody on it
            CrewSeat(2, "V002", "Morning"),             // no rest day
            CrewSeat(3, "V666", "Morning", 1),          // retired bus
            CrewSeat(4, "V009", "Morning", 1),          // bus based on another route
            FloaterSeat(5, 6),                          // inactive account
        };

        var problems = Problems(seats, Buses(), drivers, Routes);

        Assert.Contains("D1 Cruz is placed more than once: V001 on the Morning shift and a North Loop floater.", problems);
        Assert.Contains("V001 runs the Afternoon shift with no driver.", problems);
        Assert.Contains("D2 Cruz has no rest day.", problems);
        Assert.Contains("V666 is retired and cannot hold a crew.", problems);
        Assert.Contains("V009 is not based on North Loop. Move its crew to the route it runs.", problems);
        Assert.Contains("Gone Cruz is not an active driver.", problems);
        Assert.Equal(6, problems.Count);
    }

    [Fact]
    public void Two_drivers_on_one_bus_shift_are_refused()
    {
        var seats = new List<RosterSeat> { CrewSeat(1, "V001", "Morning", 2), CrewSeat(2, "v001", "Morning", 3) };

        Assert.Contains("V001 has more than one driver on the Morning shift.", Problems(seats, Buses(), Drivers(1, 2), Routes));
    }

    [Fact]
    public void The_capacity_strip_counts_crew_resting_against_floaters_working()
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

        var strip = Capacity(North, seats);

        Assert.Equal(new DayCover(1, 2, 1), strip[0]);
        Assert.True(strip[0].Short);
        Assert.Equal(new DayCover(2, 1, 0), strip[1]);
        Assert.Equal(new DayCover(3, 0, 1), strip[2]);
        Assert.False(strip[2].Short);
    }

    [Fact]
    public void The_shortfall_names_missing_drivers_and_weekly_gaps()
    {
        // Seven bus shifts need two floaters; one is placed and one shift has no driver.
        var seats = new List<RosterSeat>
        {
            CrewSeat(1, "V001", "Morning", 1),
            CrewSeat(2, "V001", "Afternoon", 1),
            CrewSeat(3, "V001", "Evening", 2),
            CrewSeat(4, "V002", "Morning", 3),
            CrewSeat(5, "V002", "Afternoon", 4),
            CrewSeat(6, "V002", "Evening", 5),
            CrewSeat(null, "V003", "Morning"),
            FloaterSeat(7, 2),
        };

        var s = Shortfall(North, seats);

        Assert.Equal(7, s.CrewSeats);
        Assert.Equal(2, s.FloatersNeeded);
        Assert.Equal(1, s.Floaters);
        Assert.Equal(2, s.DriversShort);
        // Monday has two resting and one floater; Tuesday one resting and the floater off.
        Assert.Equal(2, s.GapsPerWeek);
    }

    [Fact]
    public void Suggested_floater_rest_days_are_spread_across_the_week()
    {
        var seats = new List<RosterSeat> { FloaterSeat(1), FloaterSeat(2), FloaterSeat(3) };

        var suggested = SuggestRestDays(seats);

        Assert.Equal(new int?[] { 1, 3, 5 }, suggested.Select(s => s.RestWeekday));
    }

    [Fact]
    public void Suggested_crew_rest_days_follow_spare_cover_and_keep_crewmates_apart()
    {
        // Two floaters rest Monday and Thursday, so every other day has two to spare.
        var seats = new List<RosterSeat>
        {
            FloaterSeat(10), FloaterSeat(11),
            CrewSeat(1, "V001", "Morning"),
            CrewSeat(2, "V001", "Afternoon"),
            CrewSeat(3, "V002", "Morning"),
            CrewSeat(4, "V002", "Afternoon"),
            CrewSeat(null, "V003", "Morning"),
        };

        var byDriver = SuggestRestDays(seats).Where(s => s.DriverId is not null).ToDictionary(s => s.DriverId!.Value);

        Assert.Equal(1, byDriver[10].RestWeekday);
        Assert.Equal(4, byDriver[11].RestWeekday);

        var crewDays = new[] { 1, 2, 3, 4 }.Select(d => byDriver[d].RestWeekday!.Value).ToList();
        Assert.All(crewDays, d => Assert.DoesNotContain(d, new[] { 1, 4 }));
        Assert.NotEqual(byDriver[1].RestWeekday, byDriver[2].RestWeekday);
        Assert.NotEqual(byDriver[3].RestWeekday, byDriver[4].RestWeekday);
        Assert.Equal(4, crewDays.Distinct().Count());

        // A bus shift with nobody on it is left as it was.
        Assert.Contains(SuggestRestDays(seats), s => s.DriverId is null && s.RestWeekday is null);
    }

    [Fact]
    public void Suggested_home_shifts_follow_where_the_crew_are()
    {
        var seats = new List<RosterSeat>
        {
            FloaterSeat(10, home: "Evening"), FloaterSeat(11, home: "Evening"), FloaterSeat(12, home: "Evening"),
            CrewSeat(1, "V001", "Morning"), CrewSeat(2, "V002", "Morning"), CrewSeat(3, "V003", "Morning"),
            CrewSeat(4, "V001", "Afternoon"), CrewSeat(5, "V002", "Afternoon"),
            CrewSeat(6, "V001", "Evening"),
        };

        var homes = SuggestRestDays(seats).Where(s => s.Kind == Floater).Select(s => s.Shift).ToList();

        Assert.Equal(new[] { "Morning", "Morning", "Afternoon" }, homes);
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
