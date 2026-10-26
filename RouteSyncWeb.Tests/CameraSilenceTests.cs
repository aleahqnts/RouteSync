using FleetWise.Controllers;
using FleetWise.Models;

namespace RouteSyncWeb.Tests;

public class CameraSilenceTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 25, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTime PhNow = new(2026, 10, 25, 10, 0, 0);
    private static readonly Vehicle Bound = new() { VehicleId = "V027", CounterDeviceId = "dev-1" };

    private static (TimeSpan For, bool Seen)? Check(Trip trip, Vehicle vehicle) =>
        DispatchController.CameraSilence(trip, vehicle, UtcNow, PhNow);

    [Fact]
    public void A_counting_camera_is_not_listed()
    {
        Assert.Null(Check(new Trip { CountHeartbeat = UtcNow.AddSeconds(-20) }, Bound));
    }

    [Fact]
    public void A_camera_silent_past_a_minute_is_listed_with_how_long()
    {
        Assert.Equal((TimeSpan.FromMinutes(3), true), Check(new Trip { CountHeartbeat = UtcNow.AddMinutes(-3) }, Bound));
    }

    [Fact]
    public void A_bus_with_no_camera_bound_is_never_listed()
    {
        Assert.Null(Check(new Trip { CountHeartbeat = UtcNow.AddHours(-1) }, new Vehicle { VehicleId = "V001" }));
    }

    [Fact]
    public void A_camera_that_never_counted_gets_a_minute_from_the_trip_start()
    {
        var started = DateTime.SpecifyKind(PhNow, DateTimeKind.Utc);
        Assert.Null(Check(new Trip { ActualStartTime = started.AddSeconds(-30) }, Bound));
        Assert.Equal((TimeSpan.FromMinutes(2), false), Check(new Trip { ActualStartTime = started.AddMinutes(-2) }, Bound));
        Assert.Null(Check(new Trip(), Bound));
    }
}
