using FleetWise.Services;

namespace RouteSyncWeb.Tests;

// A real field test replayed through the snapper: two laps of a 780 m loop on a
// motorcycle at 13 to 20 km/h, as the driver app reported them every five seconds or so.
// The positions are moved by a fixed offset to an arbitrary spot, which keeps every
// distance and turn as recorded.
//
// The recording holds the faults that made the map's marker jump backwards: fixes from
// cell towers and wifi, with no speed and a claimed accuracy of 11 to 100 m, landing up
// to 65 m behind the bus. It also starts with the bus standing a few metres before its
// terminal, which is where the loop's line begins and ends.
public class RecordedTrackTests
{
    private static readonly RouteLine Loop = RouteLine.From(new GeoPoint[]
    {
        new(14.5500000, 121.0400000),
        new(14.5499805, 121.0402980),
        new(14.5499469, 121.0405916),
        new(14.5489822, 121.0404195),
        new(14.5482179, 121.0402085),
        new(14.5482287, 121.0398984),
        new(14.5482736, 121.0396415),
        new(14.5483288, 121.0392897),
        new(14.5486064, 121.0393524),
        new(14.5489198, 121.0394361),
        new(14.5490424, 121.0385109),
        new(14.5493489, 121.0385734),
        new(14.5495104, 121.0385946),
        new(14.5496579, 121.0386140),
        new(14.5497975, 121.0386294),
        new(14.5499884, 121.0386505),
        new(14.5499734, 121.0391991),
        new(14.5499987, 121.0395406),
        new(14.5500126, 121.0396741),
        new(14.5500000, 121.0400000),
    })!;

    private static readonly RouteStops.Stop[] Stops =
    {
        new("Terminal", 14.5500000, 121.0400000, true),
        new("Second stop", 14.5482179, 121.0402085, false),
        new("Third stop", 14.5490424, 121.0385109, false),
    };

    private const int Terminal = 0, Second = 1, Third = 2;

    // Seconds from the first reading, position, speed (m/s), heading, accuracy (m).
    private static readonly (double At, double Lat, double Lng, double? Speed, double? Heading, double Accuracy)[] Track =
    {
        (0.0, 14.5501220, 121.0398011, null, null, 100),
        (65.0, 14.5501220, 121.0398011, null, null, 100),
        (127.2, 14.5500728, 121.0398626, 1.58, 276.4, 4.8),
        (140.0, 14.5499135, 121.0407262, null, null, 72.725),
        (145.4, 14.5500161, 121.0399509, 0.16, 303.6, 1.4),
        (170.0, 14.5500227, 121.0404231, null, null, 67.764),
        (175.7, 14.5499828, 121.0399309, 0.02, 223.1, 2.1),
        (195.4, 14.5499144, 121.0402476, 2.73, 159.0, 2.1),
        (200.3, 14.5499526, 121.0406561, null, null, 14.878),
        (205.4, 14.5494761, 121.0403492, 4.26, 196.1, 2),
        (220.4, 14.5488294, 121.0403176, 4.68, 191.9, 1.8),
        (225.4, 14.5485878, 121.0402709, 4.75, 196.8, 1.5),
        (240.4, 14.5481361, 121.0401192, 3.05, 279.2, 1.2),
        (255.0, 14.5482660, 121.0399295, null, null, 48.109),
        (265.4, 14.5484028, 121.0393359, 3.50, 9.1, 1),
        (285.4, 14.5489061, 121.0392759, 3.52, 277.5, 1.2),
        (295.1, 14.5489667, 121.0390065, null, null, 17.764),
        (300.4, 14.5490061, 121.0386259, 4.58, 302.3, 1.1),
        (310.4, 14.5490428, 121.0385759, 0.98, 345.1, 1.1),
        (315.0, 14.5490996, 121.0385385, null, null, 66.165),
        (320.4, 14.5490278, 121.0385726, 0.00, 90.1, 1),
        (335.4, 14.5494211, 121.0386126, 4.58, 7.8, 1.1),
        (340.4, 14.5496711, 121.0386659, 5.43, 14.3, 1.2),
        (345.1, 14.5491086, 121.0385277, null, null, 11.507),
        (350.4, 14.5499061, 121.0388359, 3.58, 111.4, 1.2),
        (360.4, 14.5498961, 121.0392209, 3.80, 93.3, 1.1),
        (365.1, 14.5499937, 121.0387798, null, null, 16.974),
        (370.4, 14.5499378, 121.0395892, 4.03, 57.2, 1),
        (380.4, 14.5499578, 121.0399042, 3.40, 90.7, 1.2),
        (385.1, 14.5499586, 121.0396517, null, null, 13.065),
        (390.4, 14.5499528, 121.0399892, 4.09, 100.5, 1),
        (400.4, 14.5499078, 121.0403992, 4.32, 90.0, 1.1),
        (405.1, 14.5499593, 121.0399316, null, null, 26.49),
        (410.4, 14.5496844, 121.0405559, 3.67, 188.2, 1.1),
        (420.4, 14.5492761, 121.0405009, 4.50, 187.6, 1),
        (425.1, 14.5498152, 121.0405471, null, null, 19.742),
        (430.4, 14.5487844, 121.0403992, 5.58, 194.6, 1),
        (435.4, 14.5485261, 121.0403126, 5.71, 197.9, 1.4),
        (445.1, 14.5489354, 121.0404370, null, null, 23.23),
        (450.4, 14.5481528, 121.0401542, 2.29, 37.1, 1.2),
        (455.4, 14.5481528, 121.0401676, 0.67, 221.7, 1.1),
        (470.4, 14.5482028, 121.0398092, 4.20, 277.2, 1.3),
        (485.4, 14.5483511, 121.0393442, 3.66, 348.4, 1.2),
        (495.4, 14.5485778, 121.0392742, 3.02, 283.6, 1.4),
        (505.4, 14.5486244, 121.0388426, 4.26, 281.1, 1.4),
        (515.4, 14.5486711, 121.0385259, 3.54, 295.3, 1.4),
        (525.4, 14.5489711, 121.0385509, 3.57, 13.6, 1.5),
        (535.4, 14.5493178, 121.0386109, 4.09, 13.9, 1.3),
        (540.1, 14.5490865, 121.0385208, null, null, 13.749),
        (545.4, 14.5497078, 121.0386559, 4.40, 349.8, 1.3),
        (555.4, 14.5499978, 121.0386876, 3.41, 21.7, 1.5),
        (565.4, 14.5502428, 121.0388509, 4.01, 89.2, 1.4),
        (575.4, 14.5502844, 121.0392942, 4.75, 82.7, 1.5),
        (580.1, 14.5503950, 121.0389648, null, null, 16.537),
        (585.4, 14.5500711, 121.0395026, 3.36, 160.3, 1.4),
        (595.5, 14.5499294, 121.0396559, 2.13, 94.4, 1.3),
        (600.0, 14.5499888, 121.0396319, null, null, 84.375),
    };

    private static readonly DateTime Start = new(2026, 10, 17, 17, 35, 25);

    // Feeds the readings in as the map would see them arrive, one poll per reading.
    private static List<(GpsReading Reading, SnapResult Result)> Replay()
    {
        var tracker = new RouteSnapTracker();
        var terminal = new GeoPoint(Stops[Terminal].Lat, Stops[Terminal].Lng);
        var seen = new List<(long, DateTime, GpsReading)>();
        var results = new List<(GpsReading, SnapResult)>();
        foreach (var (row, i) in Track.Select((r, i) => (r, i)))
        {
            var reading = new GpsReading(new GeoPoint(row.Lat, row.Lng), row.Heading, row.Speed, row.Accuracy);
            seen.Add((i + 1, Start.AddSeconds(row.At), reading));
            results.Add((reading, tracker.Advance("TRIP", Loop, seen, terminal)!));
        }
        return results;
    }

    [Fact]
    public void Progress_never_goes_backwards()
    {
        var results = Replay();
        for (var i = 1; i < results.Count; i++)
        {
            var from = results[i - 1].Result.Along!.Value;
            var to = results[i].Result.Along!.Value;
            Assert.True(Loop.Ahead(from, to) >= 0,
                $"reading {i + 1} moved the bus back {Loop.Ahead(from, to):0} m");
        }
    }

    // Where the bus stood among its stops after each reading, as the map works it out.
    private static List<StopState> StopStates()
    {
        var places = StopEta.Place(Loop, Stops);
        var states = new List<StopState>();
        double? stoodAt = null;
        foreach (var (_, result) in Replay())
        {
            var state = StopEta.Follow(Loop, places, result.Along!.Value, result.Bearing is null, stoodAt);
            stoodAt = state.StoodAt;
            states.Add(state);
        }
        return states;
    }

    [Fact]
    public void The_trip_starts_at_its_terminal()
    {
        var first = Replay()[0].Result;
        var state = StopStates()[0];

        Assert.Equal(0, first.Along!.Value, 3);
        Assert.Equal(Terminal, state.AtStop);
        Assert.Equal(Terminal, state.Leg!.Value.Previous);
        Assert.Equal(Second, state.Leg!.Value.Next);
    }

    [Fact]
    public void Fixes_without_a_speed_never_move_the_bus()
    {
        var results = Replay();
        for (var i = 1; i < results.Count; i++)
            if (results[i].Reading.Speed is null)
                Assert.Equal(results[i - 1].Result.Along, results[i].Result.Along);
    }

    [Fact]
    public void The_stops_pass_in_route_order_round_both_laps()
    {
        var passed = new List<int>();
        foreach (var state in StopStates())
        {
            var previous = state.Leg!.Value.Previous;
            if (passed.Count == 0 || passed[^1] != previous)
                passed.Add(previous);
        }

        // The ride ended a few metres short of the terminal on the second lap.
        Assert.Equal(new[] { Terminal, Second, Third, Terminal, Second, Third }, passed);
    }

    [Fact]
    public void The_whole_ride_is_drawn_on_the_road()
    {
        Assert.All(Replay(), r => Assert.True(r.Result.OnRoute));
    }
}
