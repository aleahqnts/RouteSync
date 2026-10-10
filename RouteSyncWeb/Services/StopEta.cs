namespace FleetWise.Services
{
    /// <summary>A stop placed on its route line, in metres from the line's start.</summary>
    public readonly record struct StopPlace(string Name, double Along);

    /// <summary>Where a bus is between two stops.</summary>
    /// <param name="Previous">Index of the last stop passed, or -1 before the first stop of a line that is not a loop.</param>
    /// <param name="Next">Index of the next stop.</param>
    /// <param name="Metres">Metres along the route to the next stop.</param>
    public readonly record struct StopLeg(int Previous, int Next, double Metres);

    /// <summary>
    /// Median stop-to-stop speeds learned from past trips on one route, in metres per second,
    /// by the stretch that starts at each stop and the Philippine hour the bus left it.
    /// </summary>
    public sealed class SegmentSpeeds
    {
        public static readonly SegmentSpeeds None = new(new(), new(), 0);

        private readonly Dictionary<(int Segment, int Hour), double> _byHour;
        private readonly Dictionary<int, double> _bySegment;

        /// <summary>Stop-to-stop runs the medians were taken from.</summary>
        public int Samples { get; }

        public SegmentSpeeds(Dictionary<(int Segment, int Hour), double> byHour, Dictionary<int, double> bySegment, int samples)
        {
            _byHour = byHour;
            _bySegment = bySegment;
            Samples = samples;
        }

        /// <summary>The usual speed over a stretch at this hour, else at any hour, else null.</summary>
        public double? For(int segment, int hour) =>
            _byHour.TryGetValue((segment, hour), out var v) ? v
            : _bySegment.TryGetValue(segment, out v) ? v
            : null;
    }

    /// <summary>The next stop of a bus on its route, and roughly how long until it gets there.</summary>
    /// <remarks>
    /// The time to the next stop is the distance left divided by a speed, taken from the first
    /// of: how fast buses usually cover that stretch at this hour, how fast this bus has been
    /// going, and a city bus's typical <see cref="DefaultSpeed"/>. The usual speed is timed
    /// from stop to stop, so it already holds the waits at lights and at the stops themselves.
    /// </remarks>
    public static class StopEta
    {
        /// <summary>Metres within which a stop counts as on its route line.</summary>
        private const double StopRadius = 80;

        /// <summary>About 20 km/h, a city bus's usual pace once stops and lights are counted.</summary>
        public const double DefaultSpeed = 20 / 3.6;

        /// <summary>Stop-to-stop speeds outside this range are a bad reading or a parked bus, not a run.</summary>
        private const double SlowestRun = 0.5, FastestRun = 25;

        /// <summary>Readings further apart than this leave a gap a run cannot be timed across.</summary>
        private static readonly TimeSpan LongestGap = TimeSpan.FromMinutes(5);

        /// <summary>
        /// The route's stops placed on its line, in order along it. A stop further than
        /// <see cref="StopRadius"/> from the line is left out.
        /// </summary>
        /// <remarks>
        /// A road the route drives twice passes a stop twice. The stop is taken at the first
        /// pass after the stop before it, since stops are listed in route order.
        /// </remarks>
        public static IReadOnlyList<StopPlace> Place(RouteLine line, IReadOnlyList<RouteStops.Stop> stops)
        {
            var places = new List<StopPlace>();
            var from = 0.0;
            foreach (var stop in stops)
            {
                var near = line.Near(new GeoPoint(stop.Lat, stop.Lng), StopRadius).ToList();
                if (near.Count == 0)
                    continue;

                var ahead = near.Where(p => p.Along >= from).ToList();
                LinePlace place;
                if (ahead.Count > 0)
                {
                    // The stretches of the first pass lie within a stop's reach of each other.
                    var first = ahead.Min(p => p.Along);
                    place = ahead.Where(p => p.Along <= first + 2 * StopRadius).MinBy(p => p.Distance);
                }
                else
                {
                    place = near.MinBy(p => p.Distance);
                }

                places.Add(new StopPlace(stop.Name, place.Along));
                from = place.Along;
            }
            return places.OrderBy(p => p.Along).ToList();
        }

        /// <summary>
        /// The stops either side of a place on the line, or null when there is no stop ahead:
        /// past the last stop of a line that is not a loop, or a route with no stops on it.
        /// </summary>
        public static StopLeg? Locate(RouteLine line, IReadOnlyList<StopPlace> places, double along)
        {
            if (places.Count == 0)
                return null;

            for (var i = 0; i < places.Count; i++)
            {
                if (places[i].Along > along)
                    return new StopLeg(i > 0 ? i - 1 : line.IsLoop ? places.Count - 1 : -1, i, places[i].Along - along);
            }

            // Past the last stop. Round a loop the next is the first again.
            return line.IsLoop
                ? new StopLeg(places.Count - 1, 0, line.Length - along + places[0].Along)
                : null;
        }

        /// <summary>Seconds to cover the distance, at the first speed known.</summary>
        /// <param name="usual">The stretch's learned speed at this hour, if any.</param>
        /// <param name="recent">This bus's own recent speed, used only while it is moving.</param>
        public static double Seconds(double metres, double? usual, double? recent) =>
            metres / (usual ?? (recent >= RouteSnapper.MovingSpeed ? recent : null) ?? DefaultSpeed);

        /// <summary>Learns the median speed of each stop-to-stop stretch from past trips.</summary>
        /// <param name="trips">
        /// Each trip's places on the line while it was on it, in order, with the Philippine
        /// clock time of each.
        /// </param>
        /// <remarks>
        /// Each stop is timed by where the bus crossed it, between the two readings either
        /// side, and each run is the time from one stop to the next. Round a loop, a drop of
        /// more than half the line is the bus starting another lap.
        /// </remarks>
        public static SegmentSpeeds Learn(RouteLine line, IReadOnlyList<StopPlace> places,
            IEnumerable<IReadOnlyList<(DateTime At, double Along)>> trips)
        {
        // ponytail: one median per stretch per hour of the day; weekday and weekend share it
        // and there is no live traffic. Split by day type once a few weeks of real trips exist.
            if (places.Count < 2)
                return SegmentSpeeds.None;

            var runs = new List<(int Segment, int Hour, double Speed)>();
            foreach (var trip in trips)
            {
                // Places along the line made continuous across laps.
                var path = new List<(DateTime At, double Along)>();
                double lap = 0, last = double.NaN;
                foreach (var (at, along) in trip)
                {
                    if (line.IsLoop && last - along > line.Length / 2)
                        lap += line.Length;
                    path.Add((at, along + lap));
                    last = along;
                }

                var crossings = new List<(int Stop, DateTime At, double Along)>();
                for (var k = 1; k < path.Count; k++)
                {
                    var (t0, a0) = path[k - 1];
                    var (t1, a1) = path[k];
                    if (a1 <= a0 || t1 <= t0 || t1 - t0 > LongestGap)
                        continue;

                    var laps = line.IsLoop ? (int)Math.Floor(a1 / line.Length) : 0;
                    for (var l = line.IsLoop ? (int)Math.Floor(a0 / line.Length) : 0; l <= laps; l++)
                    {
                        for (var s = 0; s < places.Count; s++)
                        {
                            var x = places[s].Along + l * line.Length;
                            if (x > a0 && x <= a1)
                                crossings.Add((s, t0 + (t1 - t0) * ((x - a0) / (a1 - a0)), x));
                        }
                    }
                }

                // Consecutive crossings of neighbouring stops make one timed run.
                for (var k = 1; k < crossings.Count; k++)
                {
                    var (from, leftAt, fromAlong) = crossings[k - 1];
                    var (to, reachedAt, toAlong) = crossings[k];
                    if (to != (from + 1) % places.Count)
                        continue;

                    var speed = (toAlong - fromAlong) / (reachedAt - leftAt).TotalSeconds;
                    if (speed is >= SlowestRun and <= FastestRun)
                        runs.Add((from, leftAt.Hour, speed));
                }
            }

            return new SegmentSpeeds(
                runs.GroupBy(r => (r.Segment, r.Hour)).ToDictionary(g => g.Key, g => Median(g.Select(r => r.Speed))),
                runs.GroupBy(r => r.Segment).ToDictionary(g => g.Key, g => Median(g.Select(r => r.Speed))),
                runs.Count);
        }

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            var mid = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }
    }
}
