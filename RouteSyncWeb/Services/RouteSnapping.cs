using System.Collections.Concurrent;
using System.Text.Json;

namespace FleetWise.Services
{
    /// <summary>A position on the map.</summary>
    public readonly record struct GeoPoint(double Lat, double Lng);

    /// <summary>The nearest place on one stretch of a route line to a reading.</summary>
    /// <param name="Along">Metres from the start of the line.</param>
    /// <param name="Distance">Metres from the reading to this place.</param>
    /// <param name="Bearing">Compass bearing of the stretch, in the route's direction of travel.</param>
    public readonly record struct LinePlace(int Segment, double Along, double Distance, double Bearing, GeoPoint Point);

    /// <summary>A route's line, measured so a bus can be placed on it.</summary>
    /// <remarks>
    /// <para>The line is the route as the bus drives it, in its direction of travel. A road
    /// the route uses both ways appears twice, once for each direction, so the direction of
    /// each stretch is what tells the two apart.</para>
    ///
    /// <para>Distances are worked on a flat projection about the line's first point. A route
    /// is a few kilometres across, over which the error is a few centimetres, far below
    /// what a phone's GPS can tell apart.</para>
    /// </remarks>
    public sealed class RouteLine
    {
        /// <summary>How close the two ends must be for the line to count as a loop.</summary>
        private const double LoopClosure = 30;

        private readonly double _lat0, _lng0, _mLat, _mLng;
        private readonly double[] _x, _y;       // projected metres, east and north
        private readonly double[] _along;       // metres from the first point to each point
        private readonly double[] _bearing;     // compass bearing of each stretch

        /// <summary>The points of the line, in the direction of travel.</summary>
        public IReadOnlyList<GeoPoint> Points { get; }

        /// <summary>Length of the line in metres.</summary>
        public double Length => _along[^1];

        /// <summary>Whether the line ends where it starts, so progress runs round a loop.</summary>
        public bool IsLoop { get; }

        private RouteLine(IReadOnlyList<GeoPoint> points)
        {
            Points = points;
            _lat0 = points[0].Lat;
            _lng0 = points[0].Lng;

            // Metres in a degree at this latitude, from the standard series for the WGS 84
            // ellipsoid.
            var phi = _lat0 * Math.PI / 180;
            _mLat = 111132.92 - 559.82 * Math.Cos(2 * phi) + 1.175 * Math.Cos(4 * phi);
            _mLng = 111412.84 * Math.Cos(phi) - 93.5 * Math.Cos(3 * phi);

            var n = points.Count;
            _x = new double[n];
            _y = new double[n];
            _along = new double[n];
            _bearing = new double[n - 1];

            for (var i = 0; i < n; i++)
                (_x[i], _y[i]) = Project(points[i]);

            for (var i = 1; i < n; i++)
            {
                var dx = _x[i] - _x[i - 1];
                var dy = _y[i] - _y[i - 1];
                _along[i] = _along[i - 1] + Math.Sqrt(dx * dx + dy * dy);
                _bearing[i - 1] = CompassBearing(dx, dy);
            }

            IsLoop = Distance(_x[0], _y[0], _x[^1], _y[^1]) <= LoopClosure;
        }

        /// <summary>A line through these points, or null when there are too few to make one.</summary>
        public static RouteLine? From(IReadOnlyList<GeoPoint> points) =>
            points.Count < 2 ? null : new RouteLine(points);

        /// <summary>Reads a route's waypoints_json, or returns null when it holds no usable line.</summary>
        public static RouteLine? Parse(string? waypointsJson)
        {
            if (string.IsNullOrWhiteSpace(waypointsJson))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(waypointsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return null;

                var points = new List<GeoPoint>();
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.Object
                        && e.TryGetProperty("lat", out var lat) && lat.TryGetDouble(out var la)
                        && e.TryGetProperty("lng", out var lng) && lng.TryGetDouble(out var ln))
                        points.Add(new GeoPoint(la, ln));
                }
                return From(points);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>The nearest place on each stretch that lies within the radius of a point.</summary>
        public IEnumerable<LinePlace> Near(GeoPoint point, double radius)
        {
            var (px, py) = Project(point);
            for (var i = 0; i < _bearing.Length; i++)
            {
                var place = Nearest(i, px, py);
                if (place.Distance <= radius)
                    yield return place;
            }
        }

        /// <summary>Metres from a point to the nearest place anywhere on the line.</summary>
        public double DistanceTo(GeoPoint point)
        {
            var (px, py) = Project(point);
            var best = double.MaxValue;
            for (var i = 0; i < _bearing.Length; i++)
                best = Math.Min(best, Nearest(i, px, py).Distance);
            return best;
        }

        /// <summary>The point this many metres along the line, clamped to its ends.</summary>
        public GeoPoint PointAt(double along)
        {
            var i = SegmentAt(along);
            var span = _along[i + 1] - _along[i];
            var t = span <= 0 ? 0 : Math.Clamp((along - _along[i]) / span, 0, 1);
            return Unproject(_x[i] + t * (_x[i + 1] - _x[i]), _y[i] + t * (_y[i + 1] - _y[i]));
        }

        /// <summary>The direction of travel this many metres along the line.</summary>
        public double BearingAt(double along) => _bearing[SegmentAt(along)];

        /// <summary>
        /// How far apart two places on the line are, going whichever way is shorter round a
        /// loop.
        /// </summary>
        public double Apart(double a, double b)
        {
            var d = Math.Abs(a - b);
            return IsLoop ? Math.Min(d, Length - d) : d;
        }

        /// <summary>
        /// How far ahead one place on the line is of another, going the shorter way round a
        /// loop. Negative when it is behind.
        /// </summary>
        public double Ahead(double from, double to)
        {
            var d = to - from;
            if (!IsLoop || Length <= 0)
                return d;
            d = (d % Length + Length) % Length;
            return d <= Length / 2 ? d : d - Length;
        }

        /// <summary>
        /// Where the line first passes within the radius of a point, or null when it never
        /// does. A loop's start and end are the same place, and this names it as the start.
        /// </summary>
        public double? FirstPass(GeoPoint point, double radius)
        {
            double? first = null;
            foreach (var place in Near(point, radius))
                if (first is not double f || place.Along < f)
                    first = place.Along;
            return first;
        }

        /// <summary>Metres between two points, on the same flat projection as the line.</summary>
        public double Between(GeoPoint a, GeoPoint b)
        {
            var (ax, ay) = Project(a);
            var (bx, by) = Project(b);
            return Distance(ax, ay, bx, by);
        }

        private LinePlace Nearest(int i, double px, double py)
        {
            double ax = _x[i], ay = _y[i], bx = _x[i + 1], by = _y[i + 1];
            double dx = bx - ax, dy = by - ay;
            var lengthSquared = dx * dx + dy * dy;
            var t = lengthSquared <= 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lengthSquared, 0, 1);
            double qx = ax + t * dx, qy = ay + t * dy;

            return new LinePlace(
                Segment: i,
                Along: _along[i] + t * (_along[i + 1] - _along[i]),
                Distance: Distance(px, py, qx, qy),
                Bearing: _bearing[i],
                Point: Unproject(qx, qy));
        }

        private int SegmentAt(double along)
        {
            var i = Array.BinarySearch(_along, along);
            if (i < 0) i = ~i - 1;
            return Math.Clamp(i, 0, _bearing.Length - 1);
        }

        private (double X, double Y) Project(GeoPoint p) =>
            ((p.Lng - _lng0) * _mLng, (p.Lat - _lat0) * _mLat);

        private GeoPoint Unproject(double x, double y) =>
            new(_lat0 + y / _mLat, _lng0 + x / _mLng);

        private static double Distance(double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double CompassBearing(double east, double north) =>
            (Math.Atan2(east, north) * 180 / Math.PI + 360) % 360;
    }

    /// <summary>One position the driver app reported.</summary>
    /// <param name="Heading">Compass course of travel, when the phone had one.</param>
    /// <param name="Speed">Metres per second.</param>
    /// <param name="Accuracy">Radius in metres the phone placed the fix within, when it sent one.</param>
    public readonly record struct GpsReading(GeoPoint Position, double? Heading, double? Speed, double? Accuracy);

    /// <summary>Where a bus is drawn after a reading, and why.</summary>
    /// <param name="Shown">The position to draw: on the route when snapped, the reading itself when off it.</param>
    /// <param name="Raw">The reading as the phone reported it.</param>
    /// <param name="OnRoute">Drawn on the route line.</param>
    /// <param name="OffRoute">Drawn at the reading, and labelled off route.</param>
    /// <param name="Held">
    /// The reading was beyond the snap radius, but the bus stays on the route until a second
    /// reading in a row agrees, so one bad fix does not pull it off the road.
    /// </param>
    /// <param name="Ignored">The reading was too inaccurate to move the bus.</param>
    /// <param name="Along">Metres along the route, when drawn on it.</param>
    /// <param name="Bearing">Direction to point the marker, or null when the bus is stopped.</param>
    /// <param name="DistanceToRoute">Metres from the reading to the route line, or null with no line.</param>
    public sealed record SnapResult(
        GeoPoint Shown,
        GeoPoint Raw,
        double? Accuracy,
        bool OnRoute,
        bool OffRoute,
        bool Held,
        bool Ignored,
        double? Along,
        double? Bearing,
        double? DistanceToRoute);

    /// <summary>What the snapper remembers about one trip between its readings.</summary>
    public sealed class SnapState
    {
        /// <summary>The last result, which a held or ignored reading repeats.</summary>
        public SnapResult? Last { get; internal set; }

        /// <summary>Where on the route the bus last was, while it is on it.</summary>
        internal double? Along { get; set; }

        /// <summary>Readings in a row beyond the snap radius.</summary>
        internal int OffStreak { get; set; }

        /// <summary>Readings in a row that placed the bus well behind where it already was.</summary>
        internal int BehindStreak { get; set; }

        /// <summary>
        /// The terminal the trip starts from, if its route has one. A bus standing near it
        /// on the trip's first reading is placed at it.
        /// </summary>
        public GeoPoint? Terminal { get; set; }
    }

    /// <summary>Places a bus on its route line, the way a navigation app does.</summary>
    /// <remarks>
    /// <para>A reading within <see cref="SnapRadius"/> of the line is drawn on the line.
    /// Between BGC's towers a phone's fix commonly lands 10 to 40 metres off the road, and
    /// drawn where it lands the bus drives through buildings.</para>
    ///
    /// <para>A road the route drives both ways offers two places on the line at the same
    /// distance. A moving bus takes the one whose direction matches its own heading; a
    /// stopped one has no useful heading and stays on the stretch it was last on.</para>
    ///
    /// <para>A bus leaves the line only after <see cref="OffRouteAfter"/> readings in a row
    /// beyond the radius, so a single fix bounced off a building does not pull it off the
    /// road. Once off, it is drawn where it really is, which is also what a phone far from
    /// any route shows, such as one being tested outside BGC.</para>
    ///
    /// <para>A reading less accurate than <see cref="PoorAccuracy"/>, or one with no speed,
    /// leaves the bus where it was. A phone's GPS always reports a speed, zero when standing;
    /// a fix without one came from cell towers and wifi, which can place a bus tens of metres
    /// from where it is while claiming to be accurate. The first reading of a trip is used
    /// whatever it is, since the bus has to be drawn somewhere.</para>
    ///
    /// <para>Progress along the line only goes forward. A reading that places the bus behind
    /// where it was leaves it there; only <see cref="ReverseAfter"/> readings in a row well
    /// behind it, a bus that really did turn back, move it backwards.</para>
    ///
    /// <para>A trip whose first reading is a bus standing within <see cref="TerminalReach"/>
    /// of its route's terminal starts at the terminal, so the stop it last passed is the
    /// terminal rather than the stop before it round a loop.</para>
    /// </remarks>
    public static class RouteSnapper
    {
        /// <summary>Metres within which a reading is drawn on the route line.</summary>
        public const double SnapRadius = 50;

        /// <summary>Accuracy radius, in metres, beyond which a reading does not move the bus.</summary>
        public const double PoorAccuracy = 25;

        /// <summary>
        /// Metres behind where the bus was that count as GPS wander rather than the bus
        /// going back. Wander leaves the bus where it is without counting toward a reverse.
        /// </summary>
        public const double Wander = 20;

        /// <summary>Readings in a row well behind the bus before it is moved back to them.</summary>
        public const int ReverseAfter = 3;

        /// <summary>Metres from its terminal within which a trip's first reading starts it there.</summary>
        public const double TerminalReach = 100;

        /// <summary>Readings in a row beyond the snap radius before the bus is shown off route.</summary>
        public const int OffRouteAfter = 2;

        /// <summary>Metres per second below which a bus counts as stopped, about walking pace.</summary>
        public const double MovingSpeed = 1.0;

        /// <summary>Degrees a stretch may differ from the bus's heading and still count as its direction.</summary>
        public const double HeadingTolerance = 60;

        /// <summary>
        /// Weight, per metre, of how far a candidate place is from where the bus last was.
        /// Small against the distance to the reading over an ordinary move, and large against
        /// the far side of the loop, which is what keeps a stopped bus on its own side of a
        /// two-way road.
        /// </summary>
        private const double ContinuityWeight = 0.1;

        /// <summary>Takes the next reading of a trip and returns where to draw the bus.</summary>
        public static SnapResult Next(RouteLine? line, SnapState state, GpsReading reading)
        {
            var moving = reading.Speed is double speed && speed >= MovingSpeed;
            var heading = moving ? Course(reading.Heading) : null;

            if (line is null)
            {
                // A route with no line has nothing to snap to and nothing to be off.
                return state.Last = new SnapResult(reading.Position, reading.Position, reading.Accuracy,
                    OnRoute: false, OffRoute: false, Held: false, Ignored: false,
                    Along: null, Bearing: heading, DistanceToRoute: null);
            }

            var distance = line.DistanceTo(reading.Position);

            if (state.Last is null && !moving && state.Terminal is GeoPoint terminal
                && line.Between(reading.Position, terminal) <= TerminalReach
                && line.FirstPass(terminal, TerminalReach) is double start)
            {
                state.Along = start;
                return state.Last = new SnapResult(line.PointAt(start), reading.Position, reading.Accuracy,
                    OnRoute: true, OffRoute: false, Held: false, Ignored: false,
                    Along: start, Bearing: null, DistanceToRoute: distance);
            }

            if ((reading.Accuracy > PoorAccuracy || reading.Speed is null) && state.Last is not null)
            {
                return state.Last with
                {
                    Raw = reading.Position, Accuracy = reading.Accuracy,
                    Held = false, Ignored = true, DistanceToRoute = distance
                };
            }

            var places = line.Near(reading.Position, SnapRadius).ToList();
            if (places.Count > 0)
            {
                var pick = Choose(line, places, heading, state.Along);

                if (state.Along is double was && state.Last is { } shown)
                {
                    var ahead = line.Ahead(was, pick.Along);
                    state.BehindStreak = ahead < -Wander ? state.BehindStreak + 1 : 0;
                    if (ahead < 0 && (ahead >= -Wander || state.BehindStreak < ReverseAfter))
                    {
                        state.OffStreak = 0;
                        return state.Last = shown with
                        {
                            Raw = reading.Position, Accuracy = reading.Accuracy,
                            OnRoute = true, OffRoute = false, Held = false, Ignored = false,
                            Bearing = moving ? shown.Bearing : null, DistanceToRoute = distance
                        };
                    }
                }

                state.BehindStreak = 0;
                state.Along = pick.Along;
                state.OffStreak = 0;
                return state.Last = new SnapResult(pick.Point, reading.Position, reading.Accuracy,
                    OnRoute: true, OffRoute: false, Held: false, Ignored: false,
                    Along: pick.Along, Bearing: moving ? pick.Bearing : null, DistanceToRoute: distance);
            }

            state.OffStreak++;

            if (state.Last is { OnRoute: true } last && state.OffStreak < OffRouteAfter)
            {
                return last with
                {
                    Raw = reading.Position, Accuracy = reading.Accuracy,
                    Held = true, Ignored = false, DistanceToRoute = distance
                };
            }

            // Off the route. Where it last was on the line no longer says anything about where
            // it will rejoin it.
            state.Along = null;
            return state.Last = new SnapResult(reading.Position, reading.Position, reading.Accuracy,
                OnRoute: false, OffRoute: true, Held: false, Ignored: false,
                Along: null, Bearing: heading, DistanceToRoute: distance);
        }

        private static LinePlace Choose(RouteLine line, List<LinePlace> places, double? heading, double? lastAlong)
        {
            IEnumerable<LinePlace> pool = places;

            if (heading is double h)
            {
                // The phone's course can be wrong at low speed, so a heading that matches no
                // stretch is disregarded rather than trusted.
                var facing = places.Where(p => AngleBetween(p.Bearing, h) <= HeadingTolerance).ToList();
                if (facing.Count > 0)
                    pool = facing;
            }

            return pool.MinBy(p => p.Distance
                + (lastAlong is double a ? ContinuityWeight * line.Apart(a, p.Along) : 0));
        }

        private static double? Course(double? heading) =>
            heading is double h && double.IsFinite(h) ? (h % 360 + 360) % 360 : null;

        private static double AngleBetween(double a, double b) =>
            Math.Abs((a - b + 540) % 360 - 180);
    }

    /// <summary>
    /// Keeps each running trip's snapping state between the fleet map's position reads.
    /// </summary>
    /// <remarks>
    /// <para>The map asks for positions every two seconds, from every screen that has it
    /// open, and the phone reports about every five. Each reading is therefore fed to the
    /// snapper exactly once, in the order the phone took them, however many screens ask;
    /// later asks get the answer it already gave.</para>
    ///
    /// <para>A reading that arrives after a newer one has been used, from a phone flushing
    /// what it held in a dead zone, is skipped. The bus has already moved past it, and
    /// drawing it would send the marker backwards.</para>
    ///
    /// <para>State lives in memory. After a restart it is rebuilt from the readings the map
    /// already reads, the last half hour of each trip.</para>
    /// </remarks>
    public sealed class RouteSnapTracker
    {
        private static readonly TimeSpan Idle = TimeSpan.FromHours(2);

        private readonly ConcurrentDictionary<string, Entry> _trips = new();
        private readonly ConcurrentDictionary<int, (string Json, RouteLine? Line)> _lines = new();
        private DateTime _lastSweepUtc = DateTime.UtcNow;

        private sealed class Entry
        {
            public readonly object Gate = new();
            public SnapState State = new();
            public RouteLine? Line;
            public DateTime LastAt = DateTime.MinValue;
            public long LastId = long.MinValue;
            public SnapResult? Result;
            public DateTime TouchedUtc;
            public double? StillAlong;
            public DateTime StillSince;
            public double? StoodAt;
            public readonly Queue<double> Speeds = new();
            public int EtaStop = -1;
            public DateTime EtaFrom;
            public double EtaPredicted, EtaMetres;
        }

        /// <summary>How many of a moving bus's latest readings its speed is averaged over.</summary>
        private const int SpeedReadings = 4;

        /// <summary>Metres a bus may drift on the line, from GPS jitter alone, and still count as standing.</summary>
        private const double StandingRadius = 20;

        /// <summary>The measured line for a route, parsed again only when its data changes.</summary>
        public RouteLine? LineFor(int routeId, string? waypointsJson)
        {
            var json = waypointsJson ?? "";
            if (_lines.TryGetValue(routeId, out var known) && string.Equals(known.Json, json, StringComparison.Ordinal))
                return known.Line;

            var line = RouteLine.Parse(json);
            _lines[routeId] = (json, line);
            return line;
        }

        /// <summary>
        /// The ID of the last reading used for each trip, so a poll can ask for that reading
        /// and the ones after it rather than the whole recent window.
        /// </summary>
        public Dictionary<string, long> LastUsed()
        {
            var used = new Dictionary<string, long>();
            foreach (var pair in _trips)
                lock (pair.Value.Gate)
                    if (pair.Value.LastId != long.MinValue)
                        used[pair.Key] = pair.Value.LastId;
            return used;
        }

        /// <summary>
        /// Feeds a trip's readings to the snapper and returns where to draw the bus, or null
        /// when the trip has no reading yet.
        /// </summary>
        /// <param name="readings">Any readings of the trip; those already used are skipped.</param>
        /// <param name="terminal">The terminal the trip's route starts from, if it has one.</param>
        public SnapResult? Advance(string tripId, RouteLine? line,
            IEnumerable<(long Id, DateTime At, GpsReading Reading)> readings, GeoPoint? terminal = null)
        {
            var entry = _trips.GetOrAdd(tripId, _ => new Entry());

            lock (entry.Gate)
            {
                // The trip was moved to another route, or its route's line was replaced.
                // Progress along the old line means nothing on the new one.
                if (!ReferenceEquals(entry.Line, line))
                {
                    entry.Line = line;
                    entry.State = new SnapState();
                }
                if (entry.State.Last is null)
                    entry.State.Terminal = terminal;

                foreach (var (id, at, reading) in readings.OrderBy(r => r.At).ThenBy(r => r.Id))
                {
                    if (at < entry.LastAt || (at == entry.LastAt && id <= entry.LastId))
                        continue;

                    entry.Result = RouteSnapper.Next(line, entry.State, reading);
                    entry.LastAt = at;
                    entry.LastId = id;

                    // A stop empties the run of speeds, so a bus pulling away from a light
                    // is not taken for one crawling in traffic.
                    if (reading.Speed is double sp && sp >= RouteSnapper.MovingSpeed)
                    {
                        entry.Speeds.Enqueue(sp);
                        if (entry.Speeds.Count > SpeedReadings)
                            entry.Speeds.Dequeue();
                    }
                    else
                        entry.Speeds.Clear();

                    // Where the bus came to rest, kept until it moves on from there.
                    if (line is null || entry.Result is not { OnRoute: true, Along: double along })
                        entry.StillAlong = null;
                    else if (entry.StillAlong is not double still || line.Apart(along, still) > StandingRadius)
                        (entry.StillAlong, entry.StillSince) = (along, at);
                }

                entry.TouchedUtc = DateTime.UtcNow;
            }

            Sweep();
            return entry.Result;
        }

        /// <summary>
        /// How long the trip's bus has stood in one place on its line, up to its latest
        /// reading. Zero while it is moving or off the line.
        /// </summary>
        public TimeSpan Standing(string tripId)
        {
            if (!_trips.TryGetValue(tripId, out var entry))
                return TimeSpan.Zero;
            lock (entry.Gate)
                return entry.StillAlong is null ? TimeSpan.Zero : entry.LastAt - entry.StillSince;
        }

        /// <summary>
        /// When the trip's bus came to rest where it now stands, by the phone's clock, or
        /// null while it is moving or off the line.
        /// </summary>
        public DateTime? StandingSince(string tripId)
        {
            if (!_trips.TryGetValue(tripId, out var entry))
                return null;
            lock (entry.Gate)
                return entry.StillAlong is null ? null : entry.StillSince;
        }

        /// <summary>
        /// Where along the line the stop is that the trip's bus has stood at and not yet left
        /// the zone of, or null.
        /// </summary>
        public double? StoodAt(string tripId)
        {
            if (!_trips.TryGetValue(tripId, out var entry))
                return null;
            lock (entry.Gate)
                return entry.StoodAt;
        }

        /// <summary>
        /// The trip's bus's average speed over its latest moving readings, or null when its
        /// latest reading has it standing.
        /// </summary>
        public double? RecentSpeed(string tripId)
        {
            if (!_trips.TryGetValue(tripId, out var entry))
                return null;
            lock (entry.Gate)
                return entry.Speeds.Count == 0 ? null : entry.Speeds.Average();
        }

        /// <summary>
        /// Follows the estimate for each stretch: the seconds predicted when the bus was first
        /// seen heading for a stop, against the seconds it took to reach that stop's zone.
        /// Returns the pair once, on arrival.
        /// </summary>
        /// <param name="inZone">The stop whose zone the bus is in, or -1.</param>
        /// <param name="next">The stop the bus is heading for, if any.</param>
        /// <param name="at">The reading's time.</param>
        /// <param name="seconds">The estimate to that stop at this reading.</param>
        /// <param name="metres">The distance to that stop at this reading.</param>
        public (int Stop, double Predicted, double Actual, double Metres)? NoteEta(string tripId, int inZone,
            int? next, DateTime at, double? seconds, double metres)
        {
            if (!_trips.TryGetValue(tripId, out var entry))
                return null;
            lock (entry.Gate)
            {
                (int, double, double, double)? arrived = null;
                if (inZone >= 0 && inZone == entry.EtaStop)
                {
                    arrived = (inZone, entry.EtaPredicted, (at - entry.EtaFrom).TotalSeconds, entry.EtaMetres);
                    entry.EtaStop = -1;
                }
                if (next is int n && n != entry.EtaStop && n != inZone && seconds is double s)
                    (entry.EtaStop, entry.EtaFrom, entry.EtaPredicted, entry.EtaMetres) = (n, at, s, metres);
                return arrived;
            }
        }

        /// <summary>Records the stop the trip's bus has stood at, or clears it with null.</summary>
        public void NoteStoodAt(string tripId, double? stopAlong)
        {
            if (_trips.TryGetValue(tripId, out var entry))
                lock (entry.Gate)
                    entry.StoodAt = stopAlong;
        }

        /// <summary>Forgets trips nobody has asked about for a while, which have ended.</summary>
        private void Sweep()
        {
            var now = DateTime.UtcNow;
            if (now - _lastSweepUtc < TimeSpan.FromMinutes(10))
                return;
            _lastSweepUtc = now;

            foreach (var pair in _trips)
                if (now - pair.Value.TouchedUtc > Idle)
                    _trips.TryRemove(pair.Key, out _);
        }
    }

    /// <summary>A route's stops, and the one its parked buses are drawn at.</summary>
    public static class RouteStops
    {
        /// <summary>One stop from a route's stops_json.</summary>
        public sealed record Stop(string Name, double Lat, double Lng, bool IsTerminal);

        /// <summary>The stops in a route's stops_json, in route order. Malformed entries are skipped.</summary>
        public static IReadOnlyList<Stop> Parse(string? stopsJson)
        {
            var stops = new List<Stop>();
            if (string.IsNullOrWhiteSpace(stopsJson))
                return stops;

            try
            {
                using var doc = JsonDocument.Parse(stopsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return stops;

                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object
                        || !e.TryGetProperty("lat", out var lat) || !lat.TryGetDouble(out var la)
                        || !e.TryGetProperty("lng", out var lng) || !lng.TryGetDouble(out var ln))
                        continue;

                    var name = e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() ?? "" : "";
                    var terminal = e.TryGetProperty("terminal", out var t) && t.ValueKind == JsonValueKind.True;

                    stops.Add(new Stop(name.Length > 0 ? name : "Unknown Stop", la, ln, terminal));
                }
            }
            catch (JsonException)
            {
                // A route whose stops cannot be read shows none, and parks at its line's start.
            }

            return stops;
        }

        /// <summary>
        /// Where a route's parked buses are drawn: the stop marked terminal, else its first
        /// stop, else the start of its line. Null when the route has neither.
        /// </summary>
        public static Stop? Terminal(string? stopsJson, string? waypointsJson)
        {
            var stops = Parse(stopsJson);
            var marked = stops.FirstOrDefault(s => s.IsTerminal) ?? stops.FirstOrDefault();
            if (marked is not null)
                return marked;

            var line = RouteLine.Parse(waypointsJson);
            return line is null ? null : new Stop("Terminal", line.Points[0].Lat, line.Points[0].Lng, true);
        }
    }
}
