using FleetWise.Services;
using static RouteSyncWeb.Tests.World;

namespace RouteSyncWeb.Tests;

public class TripStatusTests
{
    [Fact]
    public void A_deactivated_driver_is_an_assignment_issue_on_a_trip_still_to_run()
    {
        var w = new World();
        var gone = w.Driver("Gone", status: "Deactivated");
        var trip = w.Trip(Wednesday, "Morning", gone, w.Bus("B01"));

        var view = TripStatus.Resolve(trip, w.Vehicles[0], gone, null, null, false, Now);

        Assert.Equal("Deactivated", view.DriverStatus);
        Assert.Equal("Assignment Issue", view.TripStatus);
    }

    [Fact]
    public void A_deactivated_driver_keeps_a_missed_trip_without_it_reading_as_unassigned()
    {
        var w = new World();
        var gone = w.Driver("Gone", status: "Deactivated");
        var trip = w.Trip(Today.AddDays(-3), "Morning", gone, w.Bus("B01"));

        var view = TripStatus.Resolve(trip, w.Vehicles[0], gone, null, null, false, Now);

        Assert.Equal("Available", view.DriverStatus);
        Assert.Equal("Missed", view.TripStatus);
    }

    [Fact]
    public void A_deactivated_driver_does_not_change_a_completed_trip()
    {
        var w = new World();
        var gone = w.Driver("Gone", status: "Deactivated");
        var trip = w.Trip(Today.AddDays(-3), "Morning", gone, w.Bus("B01"), status: "Completed");

        var view = TripStatus.Resolve(trip, w.Vehicles[0], gone, null, null, false, Now);

        Assert.Equal("Completed", view.TripStatus);
    }

    [Fact]
    public void A_failed_inspection_keeps_the_bus_flagged_while_its_fault_is_open()
    {
        var w = new World();
        var bus = w.Bus("B01");
        var trip = w.Trip(Wednesday, "Morning", w.Driver("Ana"), bus);

        var view = TripStatus.Resolve(trip, bus, w.Drivers[0], null, Inspection(trip, "Failed"), true, Now);

        Assert.Equal("Flagged", view.VehicleStatus);
    }

    [Fact]
    public void A_repaired_bus_whose_inspection_failed_waits_for_a_new_one()
    {
        var w = new World();
        var bus = w.Bus("B01");
        var trip = w.Trip(Wednesday, "Morning", w.Driver("Ana"), bus);

        var view = TripStatus.Resolve(trip, bus, w.Drivers[0], null, Inspection(trip, "Failed"), false, Now);

        Assert.Equal("Pending", view.VehicleStatus);
    }

    [Theory]
    [InlineData("Passed")]
    [InlineData("Passed with Defects")]
    [InlineData("Skipped")]
    public void A_cleared_inspection_makes_the_bus_ready(string result)
    {
        var w = new World();
        var bus = w.Bus("B01");
        var trip = w.Trip(Wednesday, "Morning", w.Driver("Ana"), bus);

        var view = TripStatus.Resolve(trip, bus, w.Drivers[0], null, Inspection(trip, result), false, Now);

        Assert.Equal("Ready to Deploy", view.VehicleStatus);
    }

    private static FleetWise.Models.BusChecklist Inspection(FleetWise.Models.Trip trip, string result) => new()
    {
        TripId = trip.TripId,
        VehicleId = trip.VehicleId,
        ChecklistStatus = result,
    };
}
