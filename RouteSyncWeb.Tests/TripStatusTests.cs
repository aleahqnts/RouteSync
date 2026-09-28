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
}
