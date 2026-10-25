using FleetWise.Services;

namespace RouteSyncWeb.Tests;

public class StopEtaTests
{
    private static readonly GeoPoint Origin = new(14.55, 121.04);

    private static GeoPoint Offset(GeoPoint p, double north, double east)
    {
        var phi = p.Lat * Math.PI / 180;
        var mLat = 111132.92 - 559.82 * Math.Cos(2 * phi) + 1.175 * Math.Cos(4 * phi);
        var mLng = 111412.84 * Math.Cos(phi) - 93.5 * Math.Cos(3 * phi);
        return new GeoPoint(p.Lat + north / mLat, p.Lng + east / mLng);
    }

    private static RouteStops.Stop StopAt(string name, double east) =>
        new(name, Offset(Origin, 0, east).Lat, Offset(Origin, 0, east).Lng, false);

    /// <summary>A road running 1 km due east from the origin.</summary>
    private static RouteLine EastRoad() =>
        RouteLine.From(Enumerable.Range(0, 11).Select(i => Offset(Origin, 0, i * 100)).ToList())!;

    /// <summary>1 km east and back along the same road, ending where it starts.</summary>
    private static RouteLine OutAndBack() =>
        RouteLine.From(Enumerable.Range(0, 11).Select(i => Offset(Origin, 0, i * 100))
            .Concat(Enumerable.Range(0, 10).Select(i => Offset(Origin, 0, (9 - i) * 100)))
            .ToList())!;

    [Fact]
    public void Stops_are_placed_in_order_and_a_far_one_is_left_out()
    {
        var places = StopEta.Place(EastRoad(), new[]
        {
            StopAt("A", 0), StopAt("B", 400),
            new RouteStops.Stop("Far", Offset(Origin, 500, 500).Lat, Offset(Origin, 500, 500).Lng, false),
            StopAt("C", 900),
        });

        Assert.Equal(new[] { "A", "B", "C" }, places.Select(p => p.Name));
        Assert.Equal(400, places[1].Along, 0);
    }

    [Fact]
    public void A_road_driven_twice_places_a_stop_on_the_pass_after_the_stop_before_it()
    {
        var line = OutAndBack();
        var places = StopEta.Place(line, new[] { StopAt("Start", 0), StopAt("Turn", 1000), StopAt("Back", 400) });

        Assert.Equal(1600, places.Single(p => p.Name == "Back").Along, 0);
    }

    [Fact]
    public void The_next_stop_is_the_first_ahead_and_a_loop_wraps_to_its_first()
    {
        var line = OutAndBack();
        var places = StopEta.Place(line, new[] { StopAt("Start", 0), StopAt("Turn", 1000), StopAt("Back", 400) });

        var leg = StopEta.Locate(line, places, 700)!.Value;
        Assert.Equal(("Start", "Turn", 300.0), (places[leg.Previous].Name, places[leg.Next].Name, Math.Round(leg.Metres)));

        var wrap = StopEta.Locate(line, places, 1900)!.Value;
        Assert.Equal(("Back", "Start", 100.0), (places[wrap.Previous].Name, places[wrap.Next].Name, Math.Round(wrap.Metres)));
    }

    [Theory]
    [InlineData(369, -1)]
    [InlineData(371, 1)]
    [InlineData(400, 1)]
    [InlineData(419, 1)]
    [InlineData(421, -1)]
    public void A_stop_is_the_stretch_from_30_m_before_to_20_m_after_it(double along, int zone)
    {
        var road = EastRoad();
        var places = StopEta.Place(road, new[] { StopAt("A", 0), StopAt("B", 400), StopAt("C", 900) });

        Assert.Equal(zone, StopEta.ZoneAt(road, places, along));
    }

    [Fact]
    public void Driving_through_a_stop_passes_it_only_on_leaving_its_zone()
    {
        var road = EastRoad();
        var places = StopEta.Place(road, new[] { StopAt("A", 0), StopAt("B", 400), StopAt("C", 900) });

        var inZone = StopEta.Follow(road, places, 410, standing: false, stoodAt: null);
        Assert.Equal(-1, inZone.AtStop);
        Assert.Equal(("A", "B"), (places[inZone.Leg!.Value.Previous].Name, places[inZone.Leg!.Value.Next].Name));
        Assert.Equal(0, inZone.Leg!.Value.Metres);

        var past = StopEta.Follow(road, places, 425, standing: false, stoodAt: null);
        Assert.Equal(("B", "C"), (places[past.Leg!.Value.Previous].Name, places[past.Leg!.Value.Next].Name));
    }

    [Fact]
    public void Standing_in_a_stop_zone_is_at_the_stop_and_has_passed_it_until_the_zone_is_left()
    {
        var road = EastRoad();
        var places = StopEta.Place(road, new[] { StopAt("A", 0), StopAt("B", 400), StopAt("C", 900) });

        var standing = StopEta.Follow(road, places, 385, standing: true, stoodAt: null);
        Assert.Equal(1, standing.AtStop);
        Assert.Equal(("B", "C"), (places[standing.Leg!.Value.Previous].Name, places[standing.Leg!.Value.Next].Name));

        var pulling = StopEta.Follow(road, places, 395, standing: false, standing.StoodAt);
        Assert.Equal(-1, pulling.AtStop);
        Assert.Equal("B", places[pulling.Leg!.Value.Previous].Name);

        var gone = StopEta.Follow(road, places, 500, standing: false, pulling.StoodAt);
        Assert.Null(gone.StoodAt);
    }

    [Fact]
    public void Progress_is_the_share_of_the_stretch_covered_including_round_the_end_of_a_loop()
    {
        var line = OutAndBack();
        var places = StopEta.Place(line, new[] { StopAt("Start", 0), StopAt("Turn", 1000), StopAt("Back", 400) });

        Assert.Equal(0.7, StopEta.Progress(line, places, StopEta.Locate(line, places, 700)!.Value)!.Value, 3);
        Assert.Equal(0.75, StopEta.Progress(line, places, StopEta.Locate(line, places, 1900)!.Value)!.Value, 3);

        var road = EastRoad();
        var two = StopEta.Place(road, new[] { StopAt("A", 100), StopAt("B", 500) });
        Assert.Null(StopEta.Progress(road, two, StopEta.Locate(road, two, 50)!.Value));
        // Inside the next stop's zone the bus is at it.
        Assert.Equal(1, StopEta.Progress(road, two, StopEta.Locate(road, two, 480)!.Value)!.Value, 3);
    }

    [Fact]
    public void Past_the_last_stop_of_a_line_that_is_not_a_loop_has_no_next_stop()
    {
        var line = EastRoad();
        var places = StopEta.Place(line, new[] { StopAt("A", 100), StopAt("B", 500) });

        Assert.Null(StopEta.Locate(line, places, 700));
        Assert.Equal(-1, StopEta.Locate(line, places, 50)!.Value.Previous);
    }

    [Fact]
    public void Time_uses_the_usual_speed_then_a_moving_bus_then_the_default()
    {
        Assert.Equal(100, StopEta.Seconds(1000, 10, 6));
        Assert.Equal(100, StopEta.Seconds(1000, 10, null));
        Assert.Equal(500, StopEta.Seconds(1000, null, 2));
        // Crawling at under half the usual speed: the usual is cut to twice the crawl.
        Assert.Equal(250, StopEta.Seconds(1000, 10, 2));
        // Standing still is the delay rule's job, not a crawl.
        Assert.Equal(100, StopEta.Seconds(1000, 10, 0.3));
        Assert.Equal(1000 / StopEta.DefaultSpeed, StopEta.Seconds(1000, null, 0.3), 6);
    }

    [Fact]
    public void Learned_speed_is_the_median_stop_to_stop_run_by_hour()
    {
        var line = EastRoad();
        var places = StopEta.Place(line, new[] { StopAt("A", 50), StopAt("B", 500), StopAt("C", 1000) });
        var eight = new DateTime(2026, 10, 1, 8, 0, 0);

        // Three trips crossing A to B at 5, 10 and 4 m/s, readings every 50 m.
        IReadOnlyList<(DateTime, double)> Trip(double speed) =>
            Enumerable.Range(0, 21).Select(i => (eight.AddSeconds(i * 50 / speed), i * 50.0)).ToList();
        var speeds = StopEta.Learn(line, places, new[] { Trip(5), Trip(10), Trip(4) });

        Assert.Equal(5, speeds.For(0, 8)!.Value, 6);
        Assert.Equal(5, speeds.For(0, 17)!.Value, 6);
        Assert.Null(speeds.For(2, 8));
    }

    [Fact]
    public void A_standstill_past_a_normal_wait_adds_its_excess_up_to_a_cap()
    {
        Assert.Equal(0, StopEta.Delay(TimeSpan.FromSeconds(20)));
        Assert.Equal(60, StopEta.Delay(TimeSpan.FromSeconds(90)));
        Assert.Equal(StopEta.LongestDelay.TotalSeconds, StopEta.Delay(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void The_tracker_times_a_bus_standing_on_its_line_and_resets_when_it_moves()
    {
        var tracker = new RouteSnapTracker();
        var line = EastRoad();
        var t = new DateTime(2026, 10, 1, 8, 0, 0);
        GpsReading At(double east, double speed) => new(Offset(Origin, 0, east), 90, speed, 5);

        tracker.Advance("T", line, new[] { (1L, t, At(300, 0)), (2L, t.AddSeconds(40), At(305, 0)), (3L, t.AddSeconds(90), At(298, 0)) });
        Assert.Equal(TimeSpan.FromSeconds(90), tracker.Standing("T"));

        tracker.Advance("T", line, new[] { (4L, t.AddSeconds(100), At(400, 8)) });
        Assert.Equal(TimeSpan.Zero, tracker.Standing("T"));
    }

    [Fact]
    public void Standing_at_a_loops_first_stop_from_the_end_of_the_line_heads_for_the_second()
    {
        var line = OutAndBack();
        var places = StopEta.Place(line, new[] { StopAt("Start", 0), StopAt("Turn", 1000) });

        var state = StopEta.Follow(line, places, line.Length - 10, standing: true, stoodAt: null);

        Assert.Equal(0, state.AtStop);
        Assert.Equal(0, state.Leg!.Value.Previous);
        Assert.Equal(1, state.Leg!.Value.Next);
    }

    [Fact]
    public void The_tracker_averages_the_latest_moving_speeds_and_a_stop_starts_them_over()
    {
        var tracker = new RouteSnapTracker();
        var line = EastRoad();
        var t = new DateTime(2026, 10, 1, 8, 0, 0);
        GpsReading At(double east, double speed) => new(Offset(Origin, 0, east), 90, speed, 5);

        tracker.Advance("T", line, new[]
        {
            (1L, t, At(100, 2)), (2L, t.AddSeconds(5), At(110, 4)), (3L, t.AddSeconds(10), At(130, 6)),
            (4L, t.AddSeconds(15), At(160, 8)), (5L, t.AddSeconds(20), At(200, 10))
        });
        Assert.Equal(7, tracker.RecentSpeed("T")); // the last four

        tracker.Advance("T", line, new[] { (6L, t.AddSeconds(25), At(200, 0)) });
        Assert.Null(tracker.RecentSpeed("T"));
        tracker.Advance("T", line, new[] { (7L, t.AddSeconds(30), At(210, 9)) });
        Assert.Equal(9, tracker.RecentSpeed("T"));
    }

    [Fact]
    public void The_tracker_pairs_each_stretchs_first_estimate_with_the_time_taken()
    {
        var tracker = new RouteSnapTracker();
        var t = new DateTime(2026, 10, 1, 8, 0, 0);
        tracker.Advance("T", EastRoad(), new[] { (1L, t, new GpsReading(Offset(Origin, 0, 100), 90, 8, 5)) });

        Assert.Null(tracker.NoteEta("T", -1, 1, t, 60, 400));
        Assert.Null(tracker.NoteEta("T", -1, 1, t.AddSeconds(30), 25, 200)); // later estimates are not kept
        var check = tracker.NoteEta("T", 1, 1, t.AddSeconds(75), 0, 0);
        Assert.Equal((1, 60.0, 75.0, 400.0), check);
        Assert.Null(tracker.NoteEta("T", 1, 1, t.AddSeconds(80), 0, 0)); // once per arrival
    }

    [Fact]
    public void A_gap_in_the_readings_is_not_timed_as_a_slow_run()
    {
        var line = EastRoad();
        var places = StopEta.Place(line, new[] { StopAt("A", 50), StopAt("B", 500) });
        var t = new DateTime(2026, 10, 1, 8, 0, 0);

        var speeds = StopEta.Learn(line, places, new[]
        {
            (IReadOnlyList<(DateTime, double)>)new[] { (t, 0.0), (t.AddSeconds(10), 100.0), (t.AddMinutes(20), 600.0) },
        });

        Assert.Equal(0, speeds.Samples);
    }
}
