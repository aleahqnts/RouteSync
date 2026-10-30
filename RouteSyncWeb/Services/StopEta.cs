namespace FleetWise.Services
{
    // A stop placed on its route line, in metres from the line's start.
    public readonly record struct StopPlace(string Name, double Along);

    // Where a bus is among its route's stops.
    // Leg: The stops either side of it, or null with no stop ahead.
    // AtStop: Index of the stop it is standing at, or -1.
    // StoodAt: Where along the line the stop it has stood at and not yet left is, if any.
    public readonly record struct StopState(StopLeg? Leg, int AtStop, double? StoodAt);

    // Where a bus is between two stops.
    // Previous: Index of the last stop passed, or -1 before the first stop of a line that is not a loop.
    // Next: Index of the next stop.
    // Metres: Metres along the route to the next stop.
    public readonly record struct StopLeg(int Previous, int Next, double Metres);

    // Median stop-to-stop speeds learned from past trips on one route, in metres per second,
    // by the stretch that starts at each stop and the Philippine hour the bus left it.
    public sealed class SegmentSpeeds
    {
        public static readonly SegmentSpeeds None = new(new(), new(), 0);

        private readonly Dictionary<(int Segment, int Hour), double> _byHour;
        private readonly Dictionary<int, double> _bySegment;

        // Stop-to-stop runs the medians were taken from.
        public int Samples { get; }

        public SegmentSpeeds(Dictionary<(int Segment, int Hour), double> byHour, Dictionary<int, double> bySegment, int samples)
        {
            _byHour = byHour;
            _bySegment = bySegment;
            Samples = samples;
        }

        // The usual speed over a stretch at this hour, else at any hour, else null.
        public double? For(int segment, int hour) =>
            _byHour.TryGetValue((segment, hour), out var v) ? v
            : _bySegment.TryGetValue(segment, out v) ? v
            : null;
    }

    // The next stop of a bus on its route, and roughly how long until it gets there.
    //
    // The time to the next stop is the distance left divided by a speed, taken from the first
    // of: how fast buses usually cover that stretch at this hour, how fast this bus has been
    // going, and a city bus's typical DefaultSpeed. The usual speed is timed
    // from stop to stop, so it already holds the waits at lights and at the stops themselves.
    public static class StopEta
    {
        // Metres within which a stop counts as on its route line.
        private const double StopRadius = 80;

        // About 20 km/h, a city bus's usual pace once stops and lights are counted.
        public const double DefaultSpeed = 20 / 3.6;

        // Stop-to-stop speeds outside this range are a bad reading or a parked bus, not a run.
        private const double SlowestRun = 0.5, FastestRun = 25;

        // Readings further apart than this leave a gap a run cannot be timed across.
        private static readonly TimeSpan LongestGap = TimeSpan.FromMinutes(5);

        // The route's stops placed on its line, in order along it. A stop further than
        // StopRadius from the line is left out.
        //
        // A road the route drives twice passes a stop twice. The stop is taken at the first
        // pass after the stop before it, since stops are listed in route order.
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

        // Metres before a stop, along the route, where a bus counts as at it.
        public const double ZoneBefore = 30;

        // Metres after a stop, along the route, where a bus still counts as at it.
        public const double ZoneAfter = 20;

        // The stop whose zone holds a place on the line, or -1. A stop is a stretch of road
        // rather than a point: a bus is twelve metres long, drivers stop a little short or
        // long, and GPS wanders by a few metres.
        public static int ZoneAt(RouteLine line, IReadOnlyList<StopPlace> places, double along)
        {
            var best = -1;
            var bestOff = double.MaxValue;
            for (var i = 0; i < places.Count; i++)
            {
                var off = line.Ahead(places[i].Along, along);
                if (off >= -ZoneBefore && off <= ZoneAfter && Math.Abs(off) < bestOff)
                    (best, bestOff) = (i, Math.Abs(off));
            }
            return best;
        }

        // Where a bus is among its stops after a reading. A bus standing in a stop's zone is
        // at that stop, and counts as having passed it from then on, until it leaves the zone.
        // standing: The bus is standing still.
        // stoodAt: What the last reading returned as StopState.StoodAt.
        public static StopState Follow(RouteLine line, IReadOnlyList<StopPlace> places, double along,
            bool standing, double? stoodAt)
        {
            var zone = ZoneAt(line, places, along);
            if (stoodAt is double stood && (zone < 0 || places[zone].Along != stood))
                stoodAt = null;

            var atStop = -1;
            if (zone >= 0 && standing)
            {
                atStop = zone;
                stoodAt = places[zone].Along;
            }
            return new StopState(Locate(line, places, along, stoodAt), atStop, stoodAt);
        }

        // The stops either side of a place on the line, or null when there is no stop ahead:
        // past the last stop of a line that is not a loop, or a route with no stops on it.
        //
        // A stop counts as passed once the bus leaves its zone, not when it crosses the
        // stop's point, or as soon as the bus has stood at it.
        // stoodAt: Where along the line the stop the bus has stood at is, if any.
        public static StopLeg? Locate(RouteLine line, IReadOnlyList<StopPlace> places, double along, double? stoodAt = null)
        {
            if (places.Count == 0)
                return null;

            for (var i = 0; i < places.Count; i++)
            {
                var passed = places[i].Along + ZoneAfter < along || places[i].Along == stoodAt;
                if (!passed)
                    return new StopLeg(i > 0 ? i - 1 : line.IsLoop ? places.Count - 1 : -1, i,
                        Math.Max(0, places[i].Along - along));
            }

            // Past the last stop. Round a loop the next is the first again, unless the bus is
            // standing at it, coming round the end of the line, when it is the second.
            if (!line.IsLoop)
                return null;
            var next = places[0].Along == stoodAt && places.Count > 1 ? 1 : 0;
            return new StopLeg(next == 0 ? places.Count - 1 : 0, next, line.Length - along + places[next].Along);
        }

        // How far the bus has come from the last stop toward the next, from 0 to 1, or null
        // before the first stop of a line that is not a loop. Inside the next stop's zone it
        // is 1: the bus is at that stop, not short of it.
        public static double? Progress(RouteLine line, IReadOnlyList<StopPlace> places, StopLeg leg)
        {
            if (leg.Previous < 0)
                return null;
            if (leg.Metres <= ZoneBefore)
                return 1;

            return Span(line, places, leg) is double span ? Math.Clamp(1 - leg.Metres / span, 0, 1) : null;
        }

        // Metres from the last stop to the next, or null before the first stop of a line that is not a loop.
        public static double? Span(RouteLine line, IReadOnlyList<StopPlace> places, StopLeg leg)
        {
            if (leg.Previous < 0)
                return null;
            var span = places[leg.Next].Along - places[leg.Previous].Along;
            if (span <= 0)
                span += line.Length; // round the end of a loop
            return span <= 0 ? null : span;
        }

        // Seconds to cover the distance, at the first speed known. A bus moving at under
        // half the usual speed is crawling in traffic, and the usual speed is cut to twice
        // its own: the slower it crawls, the longer the estimate, without one slow reading
        // at a corner doubling it.
        // usual: The stretch's learned speed at this hour, if any.
        // recent: This bus's own recent speed, used only while it is moving.
        public static double Seconds(double metres, double? usual, double? recent)
        {
            var moving = recent >= RouteSnapper.MovingSpeed ? recent : null;
            // ponytail: one reading's speed stands for the traffic ahead; average the last
            // few readings if single slow fixes make the estimate swing.
            var speed = usual is double u && moving is double m ? Math.Min(u, 2 * m) : usual ?? moving;
            return metres / (speed ?? DefaultSpeed);
        }

        // A normal wait at a stop or a light, already inside the usual speeds.
        public static readonly TimeSpan UsualWait = TimeSpan.FromSeconds(30);

        // The most a long standstill adds to the time to the next stop.
        public static readonly TimeSpan LongestDelay = TimeSpan.FromMinutes(10);

        // Seconds to add for a bus that has stood still longer than a normal wait: as long
        // again as it has stood past one, so the estimate grows while the bus is held up
        // instead of holding still.
        public static double Delay(TimeSpan standing)
        {
            // ponytail: a standstill is assumed to last as long again as it has so far. No
            // knowledge of why the bus stopped; learn typical hold-ups per stretch if needed.
            var extra = standing - UsualWait;
            return extra <= TimeSpan.Zero ? 0 : Math.Min(extra.TotalSeconds, LongestDelay.TotalSeconds);
        }

        // Learns the median speed of each stop-to-stop stretch from past trips.
        // trips:
        // Each trip's places on the line while it was on it, in order, with the Philippine
        // clock time of each.
        //
        // Each stop is timed by where the bus crossed it, between the two readings either
        // side, and each run is the time from one stop to the next. Round a loop, a drop of
        // more than half the line is the bus starting another lap.
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
