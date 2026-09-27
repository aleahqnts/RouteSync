using FleetWise.Services;

namespace RouteSyncWeb.Tests;

public class GpsCheckTests
{
    private static readonly GeoPoint Origin = new(14.55, 121.04);
    private static readonly DateTime T0 = new(2026, 9, 26, 8, 0, 0);

    private static GeoPoint Offset(double north, double east)
    {
        var phi = Origin.Lat * Math.PI / 180;
        var mLat = 111132.92 - 559.82 * Math.Cos(2 * phi) + 1.175 * Math.Cos(4 * phi);
        var mLng = 111412.84 * Math.Cos(phi) - 93.5 * Math.Cos(3 * phi);
        return new GeoPoint(Origin.Lat + north / mLat, Origin.Lng + east / mLng);
    }

    /// <summary>A road running 1 km due east from the origin.</summary>
    private static RouteLine EastRoad() =>
        RouteLine.From(Enumerable.Range(0, 11).Select(i => Offset(0, i * 100)).ToList())!;

    private static (DateTime, long, GpsReading) At(int second, double north, double east, double? accuracy = null) =>
        (T0.AddSeconds(second), second, new GpsReading(Offset(north, east), 90, 8, accuracy));

    [Fact]
    public void Each_reading_counts_once_under_what_the_map_did_with_it()
    {
        var check = GpsCheck.Replay(EastRoad(), new[]
        {
            At(0, 10, 100, accuracy: 8),     // on the road, 10 m off the line
            At(5, 30, 200, accuracy: 12),    // on the road, 30 m off
            At(10, 5, 300, accuracy: 120),   // too inaccurate to move the bus
            At(15, 90, 400, accuracy: 10),   // first reading beyond 50 m: held on the road
            At(20, 95, 450, accuracy: 10),   // second in a row: off route
            At(25, 20, 500, accuracy: 6),    // back on the road
        });

        Assert.Equal(6, check.Readings);
        Assert.Equal(3, check.OnRoad);
        Assert.Equal(1, check.Ignored);
        Assert.Equal(1, check.Held);
        Assert.Equal(1, check.OffRoute);
        Assert.Equal(0.5, check.OnRoadShare!.Value, 3);
        Assert.Equal(20, check.MeanToRoad!.Value, 1);
        Assert.Equal(30, check.WorstToRoad!.Value, 1);
        Assert.Equal(10, check.MedianAccuracy!.Value, 3);
        Assert.Equal(6, check.WithAccuracy);
    }

    [Fact]
    public void Readings_are_replayed_in_the_order_the_phone_took_them()
    {
        // Delivered in a different order after a dead zone. In order they are one far
        // reading between two on the road, which is held, not two far ones in a row.
        var check = GpsCheck.Replay(EastRoad(), new[]
        {
            At(10, 5, 300),
            At(0, 5, 100),
            At(5, 90, 200),
        });

        Assert.Equal(2, check.OnRoad);
        Assert.Equal(1, check.Held);
        Assert.Equal(0, check.OffRoute);
    }

    [Fact]
    public void A_trip_that_sent_no_accuracy_has_no_median_rather_than_zero()
    {
        var check = GpsCheck.Replay(EastRoad(), new[] { At(0, 5, 100), At(5, 5, 200) });

        Assert.Null(check.MedianAccuracy);
        Assert.Equal(0, check.WithAccuracy);
        Assert.Equal(2, check.OnRoad);
    }

    [Fact]
    public void A_started_trip_with_no_readings_is_all_empty()
    {
        var check = GpsCheck.Replay(EastRoad(), Array.Empty<(DateTime, long, GpsReading)>());

        Assert.Equal(0, check.Readings);
        Assert.Null(check.OnRoadShare);
        Assert.Null(check.MeanToRoad);
        Assert.Null(check.MedianAccuracy);
    }

    [Fact]
    public void A_route_with_no_line_counts_its_readings_under_nothing()
    {
        var check = GpsCheck.Replay(null, new[] { At(0, 5, 100), At(5, 500, 200) });

        Assert.Equal(2, check.Readings);
        Assert.Equal(0, check.OnRoad + check.Held + check.OffRoute + check.Ignored);
    }

    [Theory]
    [InlineData(new double[] { 7 }, 7)]
    [InlineData(new double[] { 9, 3, 5 }, 5)]
    [InlineData(new double[] { 4, 10, 2, 8 }, 6)]
    public void The_median_is_the_middle_value_or_the_mean_of_the_middle_two(double[] values, double expected)
    {
        Assert.Equal(expected, GpsCheck.Median(values)!.Value, 6);
    }

    [Fact]
    public void There_is_no_median_of_nothing()
    {
        Assert.Null(GpsCheck.Median(Array.Empty<double>()));
    }
}
