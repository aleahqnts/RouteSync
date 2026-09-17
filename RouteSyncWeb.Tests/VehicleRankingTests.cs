using FleetWise.Services;
using static RouteSyncWeb.Tests.World;

namespace RouteSyncWeb.Tests;

public class VehicleRankingTests
{
    [Fact]
    public void Tiers_run_home_route_then_elsewhere_then_open_incident()
    {
        var w = new World();
        var grounded = w.Bus("B01", outOfService: true);
        var trip = w.Trip(Wednesday, "Morning", w.Driver("Juan"), grounded);

        var flagged = w.Bus("B02");
        w.Incident(flagged, "Brake noise");
        var noRoute = w.Bus("B03", route: null);
        var otherRoute = w.Bus("B04", route: SecondRoute);
        var home = w.Bus("B05");

        var ranked = SchedulingRules.RankVehicles(trip, w.Snapshot()).Candidates;

        Assert.Equal(new[] { "B05", "B03", "B04", "B02" }, ranked.Select(c => c.VehicleId));
        Assert.Equal(new[] { 1, 2, 2, 3 }, ranked.Select(c => c.Tier));
        Assert.Equal("Needs attention: Brake noise", ranked[3].Warning);
        Assert.Equal("Free this shift · Based on North Loop · 40 seats · 0 trips this week", ranked[0].Reason);
        Assert.Contains("No home route", ranked[1].Reason);
        Assert.Contains("Based on Second Route", ranked[2].Reason);
    }

    [Fact]
    public void Leaves_out_the_current_bus_retired_grounded_and_booked_buses()
    {
        var w = new World();
        var current = w.Bus("B01");
        var trip = w.Trip(Wednesday, "Morning", w.Driver("Juan"), current);

        w.Bus("B02", retired: true);
        w.Bus("B03", outOfService: true);
        var booked = w.Bus("B04");
        w.Trip(Wednesday, "Morning", w.Driver("Pedro"), booked);
        var laterShift = w.Bus("B05");
        w.Trip(Wednesday, "Afternoon", w.Driver("Mario"), laterShift);

        var ranked = SchedulingRules.RankVehicles(trip, w.Snapshot()).Candidates;

        Assert.Equal(new[] { "B05" }, ranked.Select(c => c.VehicleId));
    }

    [Fact]
    public void Inside_a_tier_enough_seats_come_first_then_fewest_trips_then_id()
    {
        var w = new World();
        var current = w.Bus("B01", capacity: 50);
        var trip = w.Trip(Wednesday, "Morning", w.Driver("Juan"), current);
        var spareDriver = w.Driver("Spare");

        var small = w.Bus("B02", capacity: 30);
        var busy = w.Bus("B03", capacity: 60);
        w.Trip(new DateTime(2026, 9, 18), "Morning", spareDriver, busy);
        var b05 = w.Bus("B05", capacity: 50);
        var b04 = w.Bus("B04", capacity: 55);

        var ranked = SchedulingRules.RankVehicles(trip, w.Snapshot()).Candidates;

        Assert.Equal(new[] { "B04", "B05", "B03", "B02" }, ranked.Select(c => c.VehicleId));
        Assert.Contains("30 seats, fewer than B01", ranked[^1].Reason);
    }
}
