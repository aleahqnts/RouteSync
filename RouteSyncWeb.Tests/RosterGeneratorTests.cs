using FleetWise.Models;
using FleetWise.Services;
using static FleetWise.Services.RosterRules;
using static RouteSyncWeb.Tests.World;

namespace RouteSyncWeb.Tests;

public class RosterGeneratorTests
{
    private static readonly DateTime October = new(2026, 10, 1);

    private static RosterSeat CrewSeat(UserModel d, Vehicle bus, string shift, int rest) =>
        new(d.UserId, Crew, NorthLoop, bus.VehicleId, shift, rest);

    private static RosterSeat FloaterSeat(UserModel d, int rest, string home = "Morning") =>
        new(d.UserId, Floater, NorthLoop, null, home, rest);

    private static RosterWorld WorldFor(World w, IReadOnlyList<RosterSeat> seats,
        Dictionary<string, TripRosterMark>? marks = null,
        HashSet<(DateTime, string, string)>? skips = null,
        DateTime? operationalDay = null) => new()
    {
        Month = October,
        OperationalDay = operationalDay ?? Today,
        Now = World.Now,
        Seats = seats,
        Trips = w.Trips,
        Marks = marks ?? new Dictionary<string, TripRosterMark>(),
        Drivers = w.Drivers,
        Vehicles = w.Vehicles,
        Leave = w.Leave,
        Skips = skips ?? new HashSet<(DateTime, string, string)>(),
        RouteNames = new Dictionary<int, string> { [NorthLoop] = "North Loop" },
    };

    // ---- Rotation ------------------------------------------------------------------------

    [Fact]
    public void Rotation_runs_backward_within_the_shifts_each_bus_runs()
    {
        var w = new World();
        var dayBus = w.Bus("B01");
        var nightBus = w.Bus("B02");
        var odd = w.Bus("B03");
        var a = w.Driver("A"); var b = w.Driver("B");
        var c = w.Driver("C"); var d = w.Driver("D"); var e = w.Driver("E");
        var f = w.Driver("F"); var g = w.Driver("G");
        var floater = w.Driver("Float");

        var seats = new List<RosterSeat>
        {
            CrewSeat(a, dayBus, "Morning", 1), CrewSeat(b, dayBus, "Afternoon", 2),
            CrewSeat(c, nightBus, "Morning", 3), CrewSeat(d, nightBus, "Afternoon", 4), CrewSeat(e, nightBus, "Evening", 5),
            CrewSeat(f, odd, "Morning", 6), CrewSeat(g, odd, "Evening", 7),
            FloaterSeat(floater, 1, "Morning"),
        };

        var next = RosterGenerator.Rotate(seats).ToDictionary(s => s.DriverId!.Value);

        // A crew of two swaps.
        Assert.Equal("Afternoon", next[a.UserId].Shift);
        Assert.Equal("Morning", next[b.UserId].Shift);
        // A crew of three steps backward: Morning to Evening to Afternoon to Morning.
        Assert.Equal("Evening", next[c.UserId].Shift);
        Assert.Equal("Morning", next[d.UserId].Shift);
        Assert.Equal("Afternoon", next[e.UserId].Shift);
        // A bus running Morning and Evening swaps, skipping the Afternoon it does not run.
        Assert.Equal("Evening", next[f.UserId].Shift);
        Assert.Equal("Morning", next[g.UserId].Shift);
        // A floater's home shift moves the same way within the route's shifts.
        Assert.Equal("Evening", next[floater.UserId].Shift);
        // Rest days and buses stay.
        Assert.All(seats, s => Assert.Equal(s.RestWeekday, next[s.DriverId!.Value].RestWeekday));
        Assert.All(seats, s => Assert.Equal(s.VehicleId, next[s.DriverId!.Value].VehicleId));
    }

    // ---- Expansion ---------------------------------------------------------------------------

    [Fact]
    public void A_month_is_written_with_floaters_on_every_crew_rest_day()
    {
        var w = new World();
        var bus = w.Bus("B01");
        var mon = w.Driver("Mon"); var tue = w.Driver("Tue"); var floater = w.Driver("Float");
        var seats = new List<RosterSeat>
        {
            CrewSeat(mon, bus, "Morning", 1), CrewSeat(tue, bus, "Afternoon", 2), FloaterSeat(floater, 3),
        };

        var plan = RosterGenerator.Plan(WorldFor(w, seats));

        Assert.Equal(62, plan.Inserts.Count);          // 31 days, 2 shifts
        Assert.Empty(plan.Gaps);
        Assert.Equal(8, plan.CoversFilled);            // 4 Mondays and 4 Tuesdays in October 2026
        Assert.All(plan.Inserts.Where(i => i.Date.DayOfWeek == DayOfWeek.Monday && i.Shift == "Morning"),
                   i => Assert.Equal(floater.UserId, i.DriverId));
        Assert.All(plan.Inserts.Where(i => i.Date.DayOfWeek == DayOfWeek.Tuesday && i.Shift == "Afternoon"),
                   i => Assert.Equal(floater.UserId, i.DriverId));
        Assert.DoesNotContain(plan.Inserts, i => i.DriverId == mon.UserId && i.Date.DayOfWeek == DayOfWeek.Monday);
    }

    [Fact]
    public void A_rest_day_no_floater_can_cover_is_a_gap_saying_why()
    {
        var w = new World();
        var bus = w.Bus("B01");
        var crew = w.Driver("Pedro", "Reyes"); var floater = w.Driver("Float");
        var seats = new List<RosterSeat> { CrewSeat(crew, bus, "Morning", 1), FloaterSeat(floater, 1) };

        var plan = RosterGenerator.Plan(WorldFor(w, seats));

        Assert.Equal(4, plan.Gaps.Count);
        Assert.All(plan.Gaps, g => Assert.Equal(DayOfWeek.Monday, g.Date.DayOfWeek));
        Assert.Equal("Pedro Reyes on their rest day, no floater free (1 resting)", plan.Gaps[0].Reason);
    }

    [Fact]
    public void Leave_puts_a_floater_on_the_crew_drivers_shift()
    {
        var w = new World();
        var bus = w.Bus("B01");
        var crew = w.Driver("Crew"); var floater = w.Driver("Float");
        w.OnLeave(crew, new DateTime(2026, 10, 8), new DateTime(2026, 10, 8));
        var seats = new List<RosterSeat> { CrewSeat(crew, bus, "Morning", 1), FloaterSeat(floater, 3) };

        var plan = RosterGenerator.Plan(WorldFor(w, seats));

        var eighth = Assert.Single(plan.Inserts, i => i.Date == new DateTime(2026, 10, 8));
        Assert.Equal(floater.UserId, eighth.DriverId);
        Assert.True(eighth.IsCover);
    }

    [Fact]
    public void An_Evening_into_the_first_Morning_is_covered_rather_than_driven()
    {
        var w = new World();
        var bus = w.Bus("B01");
        var crew = w.Driver("Crew"); var floater = w.Driver("Float");
        // Last month this driver was on Evenings; this month the roster puts them on Mornings.
        w.Trip(new DateTime(2026, 9, 30), "Evening", crew, bus);
        var seats = new List<RosterSeat> { CrewSeat(crew, bus, "Morning", 7), FloaterSeat(floater, 3) };

        var plan = RosterGenerator.Plan(WorldFor(w, seats));

        Assert.Equal(floater.UserId, plan.Inserts.Single(i => i.Date == October).DriverId);
        Assert.Equal(crew.UserId, plan.Inserts.Single(i => i.Date == October.AddDays(1)).DriverId);
    }

    [Fact]
    public void Breaks_are_staggered_by_the_buses_place_on_the_shift()
    {
        var w = new World();
        var seats = new List<RosterSeat>();
        for (var i = 1; i <= 4; i++)
            seats.Add(CrewSeat(w.Driver("D" + i), w.Bus($"B0{i}"), "Morning", 7));

        var plan = RosterGenerator.Plan(WorldFor(w, seats));

        var firstDay = plan.Inserts.Where(i => i.Date == October).OrderBy(i => i.VehicleId).Select(i => i.BreakStart.Hours);
        Assert.Equal(new[] { 9, 10, 11, 9 }, firstDay);
    }

    [Fact]
    public void Skips_stay_empty_and_retired_buses_become_gaps()
    {
        var w = new World();
        var bus = w.Bus("B01");
        var gone = w.Bus("B02", retired: true);
        var a = w.Driver("A"); var b = w.Driver("B");
        var seats = new List<RosterSeat> { CrewSeat(a, bus, "Morning", 7), CrewSeat(b, gone, "Morning", 7) };
        var skips = new HashSet<(DateTime, string, string)> { (October, "B01", "Morning") };

        var plan = RosterGenerator.Plan(WorldFor(w, seats, skips: skips));

        Assert.Equal(1, plan.Skipped);
        Assert.DoesNotContain(plan.Inserts, i => i.Date == October);
        Assert.All(plan.Gaps.Where(g => g.VehicleId == "B02"), g => Assert.Equal("Bus retired", g.Reason));
        Assert.Equal(31, plan.Gaps.Count(g => g.VehicleId == "B02"));
        Assert.DoesNotContain(plan.Inserts, i => i.VehicleId == "B02");
    }

    // ---- Around what people did ----------------------------------------------------------------

    [Fact]
    public void Trips_made_or_edited_by_hand_are_kept_and_the_displaced_crew_is_named_idle()
    {
        var w = new World();
        var bus = w.Bus("B01");
        var crew = w.Driver("Crew"); var other = w.Driver("Other");
        var manual = w.Trip(new DateTime(2026, 10, 2), "Morning", other, bus);
        var edited = w.Trip(new DateTime(2026, 10, 3), "Morning", other, bus);
        var marks = new Dictionary<string, TripRosterMark> { [edited.TripId] = new(October, HandEdited: true) };
        var seats = new List<RosterSeat> { CrewSeat(crew, bus, "Morning", 7) };

        var plan = RosterGenerator.Plan(WorldFor(w, seats, marks));

        Assert.Contains(plan.Kept, k => k.TripId == manual.TripId && !k.ByHand);
        Assert.Contains(plan.Kept, k => k.TripId == edited.TripId && k.ByHand);
        Assert.DoesNotContain(plan.Inserts, i => i.Date == manual.Date || i.Date == edited.Date);
        Assert.DoesNotContain(plan.Updates, u => u.TripId == manual.TripId || u.TripId == edited.TripId);
        Assert.Equal(2, plan.Idle.Count(i => i.DriverId == crew.UserId));
    }

    [Fact]
    public void A_republish_rewrites_untouched_roster_trips_in_place_and_removes_ones_no_longer_run()
    {
        var w = new World();
        var bus = w.Bus("B01");
        var crew = w.Driver("Crew"); var old = w.Driver("Old");

        var wrongDriver = w.Trip(new DateTime(2026, 10, 5), "Morning", old, bus);
        wrongDriver.BreakStart = TimeSpan.FromHours(9);
        var matching = w.Trip(new DateTime(2026, 10, 6), "Morning", crew, bus);
        matching.BreakStart = TimeSpan.FromHours(9);
        var notRun = w.Trip(new DateTime(2026, 10, 6), "Evening", old, bus);
        var marks = new[] { wrongDriver, matching, notRun }
            .ToDictionary(t => t.TripId, _ => new TripRosterMark(October, HandEdited: false));

        var seats = new List<RosterSeat> { CrewSeat(crew, bus, "Morning", 7) };
        var plan = RosterGenerator.Plan(WorldFor(w, seats, marks));

        var update = Assert.Single(plan.Updates);
        Assert.Equal(wrongDriver.TripId, update.TripId);
        Assert.Equal(crew.UserId, update.DriverId);
        Assert.Equal(1, plan.Unchanged);
        Assert.Equal(new[] { notRun.TripId }, plan.Deletes);
        Assert.DoesNotContain(plan.Inserts, i => i.Date == wrongDriver.Date || i.Date == matching.Date);
    }

    [Fact]
    public void Publishing_the_current_month_plans_only_the_days_after_today()
    {
        var w = new World();
        var bus = w.Bus("B01");
        var seats = new List<RosterSeat> { CrewSeat(w.Driver("Crew"), bus, "Morning", 7) };

        var plan = RosterGenerator.Plan(WorldFor(w, seats, operationalDay: new DateTime(2026, 10, 20)));

        Assert.Equal(new DateTime(2026, 10, 21), plan.From);
        Assert.Equal(new DateTime(2026, 10, 21), plan.Inserts.Min(i => i.Date));
    }
}
