using FleetWise.Models;
using FleetWise.Services;
using static FleetWise.Services.RosterRules;
using static RouteSyncWeb.Tests.World;

namespace RouteSyncWeb.Tests;

public class RosterAutoFillTests
{
    private static readonly Dictionary<int, string> Routes = new() { [NorthLoop] = "North Loop", [SecondRoute] = "Second Route" };

    private static RosterSeat CrewOn(UserModel? d, Vehicle bus, string shift, int? rest = null, int route = NorthLoop) =>
        new(d?.UserId, Crew, route, bus.VehicleId, shift, rest);

    private static RosterSeat FloaterOn(UserModel? d, int? rest, string home = "Morning", int route = NorthLoop) =>
        new(d?.UserId, Floater, route, null, home, rest);

    private static AutoFillResult Fill(World w, IReadOnlyList<RosterSeat> seats) =>
        AutoFill(seats, w.Vehicles, w.Drivers, Routes, new RosterHistory(w.Trips));

    private static RosterSeat At(AutoFillResult r, Vehicle bus, string shift) =>
        r.Seats.Single(s => s.Kind == Crew && s.VehicleId == bus.VehicleId && s.Shift == shift);

    private static void Drove(World w, UserModel d, Vehicle bus, int trips, int route = NorthLoop)
    {
        for (var k = 1; k <= trips; k++) w.Trip(Today.AddDays(-k), "Morning", d, bus, route);
    }

    [Fact]
    public void An_inactive_driver_is_replaced_by_whoever_drove_that_bus_most()
    {
        var w = new World();
        var b01 = w.Bus("B01"); var b02 = w.Bus("B02");
        var ana = w.Driver("Ana"); var gone = w.Driver("Gone", status: "Deactivated"); var flo = w.Driver("Flo");
        var knowsRoute = w.Driver("Aaron"); var knowsBus = w.Driver("Xavier");
        Drove(w, knowsRoute, b02, 5);
        Drove(w, knowsBus, b01, 3);

        var seats = new List<RosterSeat>
        {
            CrewOn(ana, b01, "Morning", 1), CrewOn(gone, b01, "Afternoon", 2), FloaterOn(flo, 3),
        };

        var r = Fill(w, seats);
        var afternoon = At(r, b01, "Afternoon");

        Assert.Equal(knowsBus.UserId, afternoon.DriverId);
        Assert.NotNull(afternoon.RestWeekday);
        Assert.StartsWith("Replaces Gone Cruz, who is no longer an active driver. 3 trips on B01 in the last 30 days.", afternoon.Suggested);
        Assert.Equal(CrewOn(ana, b01, "Morning", 1), At(r, b01, "Morning"));
        Assert.Equal((1, 1), (r.Filled, r.Emptied));
    }

    [Fact]
    public void A_driver_already_placed_never_moves()
    {
        var w = new World();
        var b01 = w.Bus("B01"); var b02 = w.Bus("B02");
        var placed = w.Driver("Placed"); var veteran = w.Driver("Veteran"); var flo = w.Driver("Flo");
        Drove(w, veteran, b01, 10);

        var seats = new List<RosterSeat> { CrewOn(placed, b01, "Morning", 1), CrewOn(null, b02, "Morning"), FloaterOn(flo, 2) };

        var r = Fill(w, seats);

        Assert.Equal(placed.UserId, At(r, b01, "Morning").DriverId);
        Assert.Equal(veteran.UserId, At(r, b02, "Morning").DriverId);
        Assert.Contains("10 trips on North Loop in the last 30 days.", At(r, b02, "Morning").Suggested);
    }

    [Fact]
    public void Route_history_comes_first_then_a_new_driver_then_one_who_knows_another_route()
    {
        var w = new World();
        var b01 = w.Bus("B01"); var b02 = w.Bus("B02"); var s01 = w.Bus("S01", SecondRoute);
        // Names in the opposite order, so the rule and not the alphabet decides.
        var fromElsewhere = w.Driver("Aaron"); var newcomer = w.Driver("Ben"); var local = w.Driver("Carl");
        Drove(w, fromElsewhere, s01, 4, SecondRoute);
        Drove(w, local, b02, 2);

        var seats = new List<RosterSeat>
        {
            CrewOn(null, b01, "Morning"), CrewOn(null, b01, "Afternoon"), CrewOn(null, b01, "Evening"),
        };

        var r = Fill(w, seats);

        Assert.Equal(local.UserId, At(r, b01, "Morning").DriverId);
        Assert.Equal(newcomer.UserId, At(r, b01, "Afternoon").DriverId);
        Assert.Equal(fromElsewhere.UserId, At(r, b01, "Evening").DriverId);
        Assert.Contains("New driver, with no trips in the last 30 days.", At(r, b01, "Afternoon").Suggested);
        Assert.Contains("Free, and new to North Loop.", At(r, b01, "Evening").Suggested);
        Assert.DoesNotContain(r.Seats, s => s.Kind == Floater);
        Assert.Contains("North Loop needs another floater and no active driver is free.", r.Notes);
    }

    [Fact]
    public void Crew_places_are_filled_before_floaters()
    {
        var w = new World();
        var b01 = w.Bus("B01");
        var ana = w.Driver("Ana"); var only = w.Driver("Only");

        var r = Fill(w, new List<RosterSeat> { CrewOn(ana, b01, "Morning", 1), CrewOn(null, b01, "Afternoon") });

        Assert.Equal(only.UserId, At(r, b01, "Afternoon").DriverId);
        Assert.DoesNotContain(r.Seats, s => s.Kind == Floater);
        Assert.Equal(0, r.LeftEmpty);
    }

    [Fact]
    public void A_route_gets_one_floater_for_every_six_crew_places_with_a_rest_day_and_usual_shift()
    {
        var w = new World();
        var b01 = w.Bus("B01"); var b02 = w.Bus("B02"); var b03 = w.Bus("B03");
        var crew = Enumerable.Range(1, 7).Select(i => w.Driver("Crew" + i)).ToList();
        var flo = w.Driver("Flo"); var spare = w.Driver("Spare");

        var seats = new List<RosterSeat>
        {
            CrewOn(crew[0], b01, "Morning", 1), CrewOn(crew[1], b01, "Afternoon", 2), CrewOn(crew[2], b01, "Evening", 3),
            CrewOn(crew[3], b02, "Morning", 4), CrewOn(crew[4], b02, "Afternoon", 5),
            CrewOn(crew[5], b03, "Morning", 6), CrewOn(crew[6], b03, "Afternoon", 7),
            FloaterOn(flo, 1, "Morning"),
        };

        var r = Fill(w, seats);
        var added = r.Seats.Single(s => s.Kind == Floater && s.DriverId == spare.UserId);

        Assert.Equal(2, r.Seats.Count(s => s.Kind == Floater));
        // Monday is taken by the other floater; every other day has one crew driver resting.
        Assert.Equal(2, added.RestWeekday);
        // Morning and Afternoon carry most crew, and Morning already has its floater.
        Assert.Equal("Afternoon", added.Shift);
        Assert.Contains("Usually covers the Afternoon shift", added.Suggested);
    }

    [Fact]
    public void A_retired_bus_comes_off_and_its_crew_are_free_to_place_again()
    {
        var w = new World();
        var old = w.Bus("B01", retired: true); var b02 = w.Bus("B02");
        var rita = w.Driver("Rita"); var sam = w.Driver("Sam"); var flo = w.Driver("Flo");

        var seats = new List<RosterSeat>
        {
            CrewOn(rita, old, "Morning", 1), CrewOn(sam, old, "Afternoon", 2), CrewOn(null, b02, "Morning"), FloaterOn(flo, 3),
        };

        var r = Fill(w, seats);

        Assert.DoesNotContain(r.Seats, s => s.VehicleId == "B01");
        Assert.Equal(rita.UserId, At(r, b02, "Morning").DriverId);
        Assert.Contains("B01 is retired, so its Morning and Afternoon places came off the roster and Rita Cruz and Sam Cruz were released.", r.Notes);
        Assert.Equal(2, r.Emptied);

        // Nothing on the roster is left to carry that, so it is kept apart from what the
        // marks say.
        Assert.Equal(new[] { "B01 is retired, so its Morning and Afternoon places came off the roster and Rita Cruz and Sam Cruz were released." }, r.Unmarked);
        Assert.DoesNotContain(r.Unmarked, n => n.StartsWith("Put "));
    }

    [Fact]
    public void A_bus_moved_to_another_route_takes_its_shifts_there_empty()
    {
        var w = new World();
        var moved = w.Bus("B01", SecondRoute); var b02 = w.Bus("B02");
        var mo = w.Driver("Mo"); var nat = w.Driver("Nat"); var flo = w.Driver("Flo");

        var seats = new List<RosterSeat> { CrewOn(mo, moved, "Morning", 1), CrewOn(nat, b02, "Morning", 2), FloaterOn(flo, 3) };

        var r = Fill(w, seats);
        var place = At(r, moved, "Morning");

        Assert.Equal(SecondRoute, place.RouteId);
        Assert.StartsWith("Moved with B01 from North Loop.", place.Suggested);
        Assert.Contains("B01 now runs on Second Route, so its Morning place moved there from North Loop and Mo Cruz was released.", r.Notes);
    }

    [Fact]
    public void Rest_days_are_set_only_where_missing()
    {
        var w = new World();
        var b01 = w.Bus("B01");
        var a = w.Driver("A"); var b = w.Driver("B"); var f = w.Driver("F"); var g = w.Driver("G");

        var seats = new List<RosterSeat>
        {
            CrewOn(a, b01, "Morning", 3), CrewOn(b, b01, "Afternoon"), FloaterOn(f, null), FloaterOn(g, 5),
        };

        var r = Fill(w, seats);

        Assert.Equal(seats[0], r.Seats[0]);
        Assert.Equal(seats[3], r.Seats[3]);
        Assert.Equal(1, r.Seats[2].RestWeekday);     // no floater rests Monday, and no crew
        Assert.Equal(2, r.Seats[1].RestWeekday);     // most cover to spare, earliest on a tie
        Assert.Equal(2, r.RestDaysSet);

        // A rest day is all auto-fill did to these places; the drivers are the ones a person chose.
        Assert.False(MarkIsAboutDriver(r.Seats[1].Suggested));
        Assert.False(MarkIsAboutDriver(r.Seats[2].Suggested));
        Assert.Empty(r.Unmarked);
    }

    [Fact]
    public void A_mark_on_a_place_auto_fill_chose_the_driver_for_is_about_the_driver()
    {
        var w = new World();
        var b01 = w.Bus("B01");
        var ana = w.Driver("Ana"); var flo = w.Driver("Flo");

        var r = Fill(w, new List<RosterSeat> { CrewOn(null, b01, "Morning"), FloaterOn(flo, 3) });
        var place = At(r, b01, "Morning");

        Assert.Equal(ana.UserId, place.DriverId);
        Assert.True(MarkIsAboutDriver(place.Suggested));
        Assert.False(MarkIsAboutDriver(null));
    }

    [Fact]
    public void A_place_nobody_is_free_for_stays_empty_and_says_so()
    {
        var w = new World();
        var b01 = w.Bus("B01");
        var a = w.Driver("A"); var f = w.Driver("F");

        var r = Fill(w, new List<RosterSeat> { CrewOn(a, b01, "Morning", 1), CrewOn(null, b01, "Afternoon"), FloaterOn(f, 2) });

        Assert.Null(At(r, b01, "Afternoon").DriverId);
        Assert.Equal("No active driver is free for this place.", At(r, b01, "Afternoon").Suggested);
        Assert.Equal(1, r.LeftEmpty);
        Assert.Contains("Nobody is free for B01 Afternoon.", r.Notes);
    }

    [Fact]
    public void A_complete_roster_is_left_as_it_is()
    {
        var w = new World();
        var b01 = w.Bus("B01");
        var a = w.Driver("A"); var b = w.Driver("B"); var f = w.Driver("F");
        var seats = new List<RosterSeat> { CrewOn(a, b01, "Morning", 1), CrewOn(b, b01, "Afternoon", 2), FloaterOn(f, 3) };

        var r = Fill(w, seats);

        Assert.False(r.Changed);
        Assert.Equal(seats, r.Seats);
        Assert.Single(r.Notes);
    }

    [Fact]
    public void An_inactive_floater_row_is_removed_and_a_free_driver_takes_its_place()
    {
        var w = new World();
        var b01 = w.Bus("B01");
        var a = w.Driver("A"); var gone = w.Driver("Gone", status: "Deactivated"); var fresh = w.Driver("Fresh");

        var r = Fill(w, new List<RosterSeat> { CrewOn(a, b01, "Morning", 1), FloaterOn(gone, 2) });

        Assert.DoesNotContain(r.Seats, s => s.DriverId == gone.UserId);
        Assert.Equal(fresh.UserId, r.Seats.Single(s => s.Kind == Floater).DriverId);
        Assert.Contains("Removed Gone Cruz from the North Loop floaters: no longer an active driver.", r.Notes);
    }

    // ---- Publishing a place with nobody on it ------------------------------------------------

    [Fact]
    public void A_bus_shift_with_nobody_on_it_is_covered_by_a_floater_or_left_unfilled()
    {
        var w = new World();
        var bus = w.Bus("B01");
        var flo = w.Driver("Flo");
        var seats = new List<RosterSeat> { CrewOn(null, bus, "Morning"), FloaterOn(flo, 1) };

        var plan = RosterGenerator.Plan(new RosterWorld
        {
            Month = new DateTime(2026, 10, 1),
            OperationalDay = Today,
            Now = World.Now,
            Seats = seats,
            Trips = w.Trips,
            Marks = new Dictionary<string, TripRosterMark>(),
            Drivers = w.Drivers,
            Vehicles = w.Vehicles,
            Leave = w.Leave,
            Skips = new HashSet<(DateTime, string, string)>(),
            RouteNames = Routes,
        });

        // October 2026 has four Mondays, the floater's rest day.
        Assert.Equal(27, plan.Inserts.Count);
        Assert.All(plan.Inserts, i => Assert.True(i.IsCover && i.DriverId == flo.UserId));
        Assert.Equal(4, plan.Gaps.Count);
        Assert.Equal("Nobody is rostered on this shift, no floater free (1 resting)", plan.Gaps[0].Reason);
    }

    [Fact]
    public void Rotation_does_not_carry_last_months_suggestions()
    {
        var seats = new List<RosterSeat>
        {
            new(1, Crew, NorthLoop, "B01", "Morning", 1, "Auto-filled last month."),
            new(2, Crew, NorthLoop, "B01", "Afternoon", 2),
        };

        Assert.All(RosterGenerator.Rotate(seats), s => Assert.Null(s.Suggested));
    }
}
