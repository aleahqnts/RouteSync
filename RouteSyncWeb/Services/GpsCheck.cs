namespace FleetWise.Services
{
    /// <summary>How one trip's GPS behaved against its route line.</summary>
    /// <param name="Readings">Every reading the phone stored for the trip.</param>
    /// <param name="OnRoad">Readings drawn on the route line.</param>
    /// <param name="Held">Single readings beyond the snap radius that the bus was held on the road through.</param>
    /// <param name="OffRoute">Readings drawn off the route, where the phone was.</param>
    /// <param name="Ignored">Readings too inaccurate to move the bus.</param>
    /// <param name="MeanToRoad">Average metres from an on-road reading to the line, or null with none.</param>
    /// <param name="WorstToRoad">Largest such distance, at most the snap radius.</param>
    /// <param name="MedianAccuracy">Median accuracy radius the phone reported, or null when it sent none.</param>
    /// <param name="WithAccuracy">Readings that carried an accuracy radius.</param>
    public sealed record GpsTripCheck(
        int Readings,
        int OnRoad,
        int Held,
        int OffRoute,
        int Ignored,
        double? MeanToRoad,
        double? WorstToRoad,
        double? MedianAccuracy,
        int WithAccuracy)
    {
        /// <summary>Share of the readings drawn on the route line, from 0 to 1, or null with no readings.</summary>
        public double? OnRoadShare => Readings == 0 ? null : (double)OnRoad / Readings;
    }

    /// <summary>
    /// Replays a trip's stored readings through the fleet map's own snapping, to show how
    /// the GPS behaved on the real route.
    /// </summary>
    /// <remarks>
    /// The same <see cref="RouteSnapper"/> the live map uses, fed the readings in the order
    /// the phone took them from a fresh start, so the figures describe exactly what the map
    /// showed and cannot drift from it.
    ///
    /// Each reading counts once, under the first of: ignored, held, off route, on the road.
    /// A trip on a route with no line has nothing to measure against, and its readings count
    /// under none of them.
    /// </remarks>
    public static class GpsCheck
    {
        public static GpsTripCheck Replay(RouteLine? line, IEnumerable<(DateTime At, long Id, GpsReading Reading)> readings)
        {
            var state = new SnapState();
            int count = 0, onRoad = 0, held = 0, off = 0, ignored = 0;
            var toRoad = new List<double>();
            var accuracies = new List<double>();

            foreach (var (_, _, reading) in readings.OrderBy(r => r.At).ThenBy(r => r.Id))
            {
                count++;
                if (reading.Accuracy is double a)
                    accuracies.Add(a);

                var r = RouteSnapper.Next(line, state, reading);
                if (r.Ignored) ignored++;
                else if (r.Held) held++;
                else if (r.OffRoute) off++;
                else if (r.OnRoute)
                {
                    onRoad++;
                    if (r.DistanceToRoute is double d)
                        toRoad.Add(d);
                }
            }

            return new GpsTripCheck(
                count, onRoad, held, off, ignored,
                MeanToRoad: toRoad.Count == 0 ? null : toRoad.Average(),
                WorstToRoad: toRoad.Count == 0 ? null : toRoad.Max(),
                MedianAccuracy: Median(accuracies),
                WithAccuracy: accuracies.Count);
        }

        /// <summary>The middle value, or the mean of the two middle values; null when there are none.</summary>
        public static double? Median(IReadOnlyCollection<double> values)
        {
            if (values.Count == 0)
                return null;

            var sorted = values.OrderBy(v => v).ToArray();
            var mid = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }
    }
}
