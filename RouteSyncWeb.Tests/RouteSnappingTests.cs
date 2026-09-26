using FleetWise.Services;

namespace RouteSyncWeb.Tests;

public class RouteSnappingTests
{
    private static readonly GeoPoint Origin = new(14.55, 121.04);

    /// <summary>A point this many metres north and east of another.</summary>
    private static GeoPoint Offset(GeoPoint p, double north, double east)
    {
        var phi = p.Lat * Math.PI / 180;
        var mLat = 111132.92 - 559.82 * Math.Cos(2 * phi) + 1.175 * Math.Cos(4 * phi);
        var mLng = 111412.84 * Math.Cos(phi) - 93.5 * Math.Cos(3 * phi);
        return new GeoPoint(p.Lat + north / mLat, p.Lng + east / mLng);
    }

    /// <summary>A road running 1 km due east from the origin.</summary>
    private static RouteLine EastRoad() =>
        RouteLine.From(Enumerable.Range(0, 11).Select(i => Offset(Origin, 0, i * 100)).ToList())!;

    /// <summary>
    /// A route that drives 1 km east and back along the same road, so every point on the
    /// road lies on the line twice, once for each direction.
    /// </summary>
    private static RouteLine OutAndBack() =>
        RouteLine.From(Enumerable.Range(0, 11).Select(i => Offset(Origin, 0, i * 100))
            .Concat(Enumerable.Range(0, 10).Select(i => Offset(Origin, 0, (9 - i) * 100)))
            .ToList())!;

    private static GpsReading Moving(GeoPoint p, double heading, double? accuracy = null) =>
        new(p, heading, 8, accuracy);

    private static GpsReading Stopped(GeoPoint p, double? accuracy = null) =>
        new(p, null, 0, accuracy);

    [Fact]
    public void A_reading_near_the_line_is_drawn_on_it()
    {
        var road = EastRoad();
        var r = RouteSnapper.Next(road, new SnapState(), Moving(Offset(Origin, 20, 400), 90));

        Assert.True(r.OnRoute);
        Assert.False(r.OffRoute);
        Assert.Equal(400, r.Along!.Value, 1);
        Assert.Equal(20, r.DistanceToRoute!.Value, 1);
        Assert.Equal(0, road.DistanceTo(r.Shown), 2);
        Assert.Equal(90, r.Bearing!.Value, 1);
        Assert.Equal(Offset(Origin, 20, 400), r.Raw);
    }

    [Fact]
    public void A_stopped_bus_has_no_direction_to_point()
    {
        var r = RouteSnapper.Next(EastRoad(), new SnapState(), Stopped(Offset(Origin, 5, 400)));

        Assert.True(r.OnRoute);
        Assert.Null(r.Bearing);
    }

    [Fact]
    public void One_reading_off_the_road_does_not_pull_the_bus_off_it()
    {
        var road = EastRoad();
        var state = new SnapState();
        var on = RouteSnapper.Next(road, state, Moving(Offset(Origin, 10, 400), 90));

        var first = RouteSnapper.Next(road, state, Moving(Offset(Origin, 80, 450), 90));
        Assert.True(first.Held);
        Assert.True(first.OnRoute);
        Assert.Equal(on.Shown, first.Shown);
        Assert.Equal(Offset(Origin, 80, 450), first.Raw);

        var second = RouteSnapper.Next(road, state, Moving(Offset(Origin, 90, 500), 90));
        Assert.True(second.OffRoute);
        Assert.False(second.OnRoute);
        Assert.Equal(second.Raw, second.Shown);
        Assert.Null(second.Along);
    }

    [Fact]
    public void A_phone_far_from_the_route_shows_where_it_really_is()
    {
        // Antipolo, some 15 km east of BGC. With nothing to hold the bus to, it is shown
        // where it is on the very first reading.
        var antipolo = new GeoPoint(14.5864, 121.1760);
        var r = RouteSnapper.Next(EastRoad(), new SnapState(), Moving(antipolo, 45));

        Assert.True(r.OffRoute);
        Assert.Equal(antipolo, r.Shown);
        Assert.Equal(45, r.Bearing!.Value, 1);
        Assert.True(r.DistanceToRoute > 10_000);
    }

    [Fact]
    public void Coming_back_within_the_radius_snaps_at_once()
    {
        var road = EastRoad();
        var state = new SnapState();
        RouteSnapper.Next(road, state, Moving(Offset(Origin, 200, 300), 90));
        RouteSnapper.Next(road, state, Moving(Offset(Origin, 200, 350), 90));

        var back = RouteSnapper.Next(road, state, Moving(Offset(Origin, 30, 400), 180));

        Assert.True(back.OnRoute);
        Assert.Equal(400, back.Along!.Value, 1);
    }

    [Fact]
    public void An_inaccurate_fix_does_not_move_the_bus()
    {
        var road = EastRoad();
        var state = new SnapState();
        var at400 = RouteSnapper.Next(road, state, Moving(Offset(Origin, 5, 400), 90, accuracy: 8));

        var poor = RouteSnapper.Next(road, state, Moving(Offset(Origin, 5, 600), 90, accuracy: 120));
        Assert.True(poor.Ignored);
        Assert.Equal(at400.Shown, poor.Shown);
        Assert.Equal(120, poor.Accuracy);

        var good = RouteSnapper.Next(road, state, Moving(Offset(Origin, 5, 600), 90, accuracy: 10));
        Assert.False(good.Ignored);
        Assert.Equal(600, good.Along!.Value, 1);
    }

    [Fact]
    public void The_first_fix_of_a_trip_is_used_however_inaccurate()
    {
        var r = RouteSnapper.Next(EastRoad(), new SnapState(), Moving(Offset(Origin, 5, 400), 90, accuracy: 200));

        Assert.False(r.Ignored);
        Assert.True(r.OnRoute);
    }

    [Fact]
    public void A_fix_that_says_nothing_of_its_accuracy_counts_as_usable()
    {
        var road = EastRoad();
        var state = new SnapState();
        RouteSnapper.Next(road, state, Moving(Offset(Origin, 5, 400), 90));

        var r = RouteSnapper.Next(road, state, Moving(Offset(Origin, 5, 600), 90, accuracy: null));

        Assert.False(r.Ignored);
        Assert.Equal(600, r.Along!.Value, 1);
    }

    [Theory]
    [InlineData(90, 300)]    // heading east: the outbound half
    [InlineData(270, 1700)]  // heading west: the return half, 300 m before the end
    public void Heading_picks_the_side_of_a_two_way_road(double heading, double expectedAlong)
    {
        var r = RouteSnapper.Next(OutAndBack(), new SnapState(), Moving(Offset(Origin, 8, 300), heading));

        Assert.Equal(expectedAlong, r.Along!.Value, 1);
        Assert.Equal(heading, r.Bearing!.Value, 1);
    }

    [Fact]
    public void A_stopped_bus_stays_on_the_side_of_the_road_it_was_on()
    {
        var route = OutAndBack();
        var state = new SnapState();
        RouteSnapper.Next(route, state, Moving(Offset(Origin, 8, 320), 270));

        var halted = RouteSnapper.Next(route, state, Stopped(Offset(Origin, 8, 300)));

        Assert.Equal(1700, halted.Along!.Value, 1);
    }

    [Fact]
    public void A_heading_that_matches_no_stretch_is_disregarded()
    {
        // The phone's course is unreliable at low speed. One pointing across the road still
        // leaves the bus on it.
        var r = RouteSnapper.Next(EastRoad(), new SnapState(), Moving(Offset(Origin, 5, 400), 0));

        Assert.True(r.OnRoute);
        Assert.Equal(400, r.Along!.Value, 1);
    }

    [Fact]
    public void A_route_without_a_line_shows_the_reading_and_is_never_off_it()
    {
        var p = Offset(Origin, 300, 300);
        var r = RouteSnapper.Next(null, new SnapState(), Moving(p, 90));

        Assert.Equal(p, r.Shown);
        Assert.False(r.OnRoute);
        Assert.False(r.OffRoute);
        Assert.Null(r.DistanceToRoute);
    }

    [Fact]
    public void Progress_round_a_loop_is_measured_the_short_way()
    {
        var loop = RouteLine.From(new[]
        {
            Origin, Offset(Origin, 0, 500), Offset(Origin, 500, 500), Offset(Origin, 500, 0), Origin
        })!;

        Assert.True(loop.IsLoop);
        Assert.Equal(2000, loop.Length, 0);
        Assert.Equal(20, loop.Apart(10, loop.Length - 10), 1);
        Assert.False(EastRoad().IsLoop);
        Assert.Equal(980, EastRoad().Apart(10, 990), 1);
    }

    [Fact]
    public void The_point_at_a_distance_along_is_where_the_bus_was_drawn()
    {
        var road = EastRoad();
        var r = RouteSnapper.Next(road, new SnapState(), Moving(Offset(Origin, -12, 730), 90));

        var p = road.PointAt(r.Along!.Value);

        Assert.Equal(r.Shown.Lat, p.Lat, 7);
        Assert.Equal(r.Shown.Lng, p.Lng, 7);
        Assert.Equal(90, road.BearingAt(r.Along.Value), 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"lat\":1}")]
    [InlineData("[{\"lat\":14.55,\"lng\":121.04}]")]
    public void What_is_not_a_line_reads_as_no_line(string? json)
    {
        Assert.Null(RouteLine.Parse(json));
    }

    [Fact]
    public void A_line_reads_from_waypoints_json()
    {
        var line = RouteLine.Parse("[{\"lat\":14.55,\"lng\":121.04},{\"lat\":14.55,\"lng\":121.05},{\"oops\":1}]");

        Assert.NotNull(line);
        Assert.Equal(2, line.Points.Count);
        Assert.InRange(line.Length, 1070, 1085);
    }

    [Fact]
    public void Parked_buses_go_to_the_stop_marked_terminal()
    {
        const string stops = "[{\"name\":\"NutriAsia\",\"lat\":14.5517,\"lng\":121.0512},"
                           + "{\"name\":\"Market! Market!\",\"lat\":14.5489,\"lng\":121.0564,\"terminal\":true}]";

        var t = RouteStops.Terminal(stops, null);

        Assert.Equal("Market! Market!", t!.Name);
        Assert.True(t.IsTerminal);
    }

    [Fact]
    public void Without_a_marked_terminal_parked_buses_go_to_the_first_stop_then_the_line_start()
    {
        const string stops = "[{\"name\":\"NutriAsia\",\"lat\":14.5517,\"lng\":121.0512},"
                           + "{\"name\":\"HSBC\",\"lat\":14.5535,\"lng\":121.0485}]";
        const string line = "[{\"lat\":14.5493,\"lng\":121.0291},{\"lat\":14.5480,\"lng\":121.0330}]";

        Assert.Equal("NutriAsia", RouteStops.Terminal(stops, line)!.Name);
        Assert.Equal(14.5493, RouteStops.Terminal(null, line)!.Lat);
        Assert.Null(RouteStops.Terminal("not json", null));
    }

    [Fact]
    public void Stops_read_in_order_and_skip_what_they_cannot_place()
    {
        var stops = RouteStops.Parse("[{\"name\":\"A\",\"lat\":1,\"lng\":2},{\"name\":\"B\"},{\"lat\":3,\"lng\":4}]");

        Assert.Equal(new[] { "A", "Unknown Stop" }, stops.Select(s => s.Name));
    }

    private static readonly DateTime T0 = new(2026, 9, 26, 8, 0, 0);

    private static (long, DateTime, GpsReading) At(long id, int seconds, GpsReading r) =>
        (id, T0.AddSeconds(seconds), r);

    [Fact]
    public void Each_reading_is_used_once_however_often_the_map_asks()
    {
        var tracker = new RouteSnapTracker();
        var road = EastRoad();
        var readings = new[]
        {
            At(1, 0, Moving(Offset(Origin, 5, 400), 90)),
            At(2, 5, Moving(Offset(Origin, 90, 450), 90)),
        };

        // Several screens polling between the phone's readings see the same two readings
        // each time. The second is one far fix, which holds the bus on the road; counted
        // again on every ask it would push the bus off route after the second poll.
        for (var ask = 0; ask < 4; ask++)
        {
            var r = tracker.Advance("TRIP1", road, readings)!;
            Assert.True(r.Held);
            Assert.True(r.OnRoute);
        }
    }

    [Fact]
    public void A_reading_older_than_one_already_used_is_skipped()
    {
        var tracker = new RouteSnapTracker();
        var road = EastRoad();
        tracker.Advance("TRIP1", road, new[] { At(1, 10, Moving(Offset(Origin, 5, 600), 90)) });

        // Held on the phone through a dead zone and delivered late.
        var r = tracker.Advance("TRIP1", road, new[] { At(2, 5, Moving(Offset(Origin, 5, 200), 90)) })!;

        Assert.Equal(600, r.Along!.Value, 1);
    }

    [Fact]
    public void Readings_are_used_in_the_order_the_phone_took_them()
    {
        var tracker = new RouteSnapTracker();
        var road = EastRoad();

        var r = tracker.Advance("TRIP1", road, new[]
        {
            At(3, 10, Moving(Offset(Origin, 5, 700), 90)),
            At(1, 0, Moving(Offset(Origin, 5, 300), 90)),
            At(2, 5, Moving(Offset(Origin, 5, 500), 90)),
        })!;

        Assert.Equal(700, r.Along!.Value, 1);
    }

    [Fact]
    public void A_trip_with_no_reading_yet_has_nowhere_to_be_drawn()
    {
        var tracker = new RouteSnapTracker();

        Assert.Null(tracker.Advance("TRIP1", EastRoad(), Array.Empty<(long, DateTime, GpsReading)>()));
    }

    [Fact]
    public void A_route_line_is_measured_again_only_when_its_data_changes()
    {
        var tracker = new RouteSnapTracker();
        const string a = "[{\"lat\":14.55,\"lng\":121.04},{\"lat\":14.55,\"lng\":121.05}]";
        const string b = "[{\"lat\":14.55,\"lng\":121.04},{\"lat\":14.56,\"lng\":121.04}]";

        var first = tracker.LineFor(1, a);

        Assert.Same(first, tracker.LineFor(1, a));
        Assert.NotSame(first, tracker.LineFor(1, b));
        Assert.Null(tracker.LineFor(2, null));
    }
}
