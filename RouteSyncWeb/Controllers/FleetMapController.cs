using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FleetWise.Models;
using FleetWise.Models.ViewModels;
using FleetWise.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace FleetWise.Controllers
{
    [Authorize]
    public class FleetMapController : Controller
    {
        private readonly Supabase.Client _supabase;
        private readonly FareCalculator _fareCalculator;
        private readonly IMemoryCache _cache;

        /// <summary>
        /// How long the fleet, route and staff lists are reused between position reads.
        /// </summary>
        /// <remarks>
        /// Only the trips and their positions change while a bus is running. Registering a
        /// vehicle, renaming a route or adding a driver is an operator action, and a minute
        /// of staleness on those is invisible on a map. Reusing them turns every poll from
        /// six reads of the database into two, which is what makes polling often enough to
        /// look live affordable at all.
        /// </remarks>
        private static readonly TimeSpan ReferenceLifetime = TimeSpan.FromSeconds(60);

        /// <summary>
        /// How recent a telemetry reading must be to count as live.
        /// </summary>
        /// <remarks>
        /// An older position means the bus is offline or in a dead zone, and it is shown
        /// parked. Bounding the read to this window avoids scanning the whole table on
        /// every poll. The value is generous enough to absorb the phone's own heartbeat
        /// interval and brief gaps without the map flickering.
        /// </remarks>
        private const int RecentTelemetryMinutes = 30;

        /// <summary>
        /// Where a parked bus is drawn when neither its route nor any other route has a
        /// terminal, stop or line to place it by: the EDSA-Ayala terminal, where the fleet
        /// starts its day.
        /// </summary>
        private static readonly RouteStops.Stop DefaultTerminal = new("EDSA-Ayala Terminal", 14.549272, 121.029103, true);

        private readonly RouteSnapTracker _snaps;
        private readonly TripAssignments _assignments;

        /// <summary>
        /// How long one read of the live positions is shared, just under the map's
        /// two-second poll.
        /// </summary>
        private static readonly TimeSpan LiveShared = TimeSpan.FromMilliseconds(1500);

        /// <summary>
        /// How long the stop-to-stop speeds learned from past trips are reused. They move
        /// with weeks of trips, not hours, and working them out reads every recent reading of
        /// the route.
        /// </summary>
        private static readonly TimeSpan SpeedsLifetime = TimeSpan.FromHours(6);

        /// <summary>Days of past trips the speeds are learned from, within the telemetry kept.</summary>
        private const int SpeedHistoryDays = 14;

        /// <summary>Most recent trips per route the speeds are learned from.</summary>
        private const int SpeedHistoryTrips = 60;

        /// <summary>Set once the database is found to have no fleetmap_live that takes p_seen.</summary>
        private static volatile bool _seenUnsupported;

        public FleetMapController(Supabase.Client supabase, FareCalculator fareCalculator, IMemoryCache cache,
            RouteSnapTracker snaps, TripAssignments assignments)
        {
            _supabase = supabase;
            _fareCalculator = fareCalculator;
            _cache = cache;
            _snaps = snaps;
            _assignments = assignments;
        }

        /// <summary>The active trips, their readings since the last one used, and the fare, as sent.</summary>
        private async Task<string> ReadLiveAsync(int? routeId)
        {
            var recentCutoff = PhClock.NowForDb.AddMinutes(-RecentTelemetryMinutes);
            var args = new Dictionary<string, object?>
            {
                ["p_op_day"] = PhClock.OperationalDay.ToString("yyyy-MM-dd"),
                ["p_route_id"] = routeId,
                ["p_since"] = recentCutoff.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            if (!_seenUnsupported)
                args["p_seen"] = _snaps.LastUsed();

            Postgrest.Responses.BaseResponse liveResponse;
            try
            {
                liveResponse = await _supabase.Rpc("fleetmap_live", args);
            }
            catch (Postgrest.Exceptions.PostgrestException ex)
                when (args.ContainsKey("p_seen") && RosterPublisher.DatabaseCode(ex.Content) is "PGRST202")
            {
                // A database without the p_seen version of the function: read the whole
                // window, as before, and stop offering it until the dashboard restarts.
                _seenUnsupported = true;
                args.Remove("p_seen");
                liveResponse = await _supabase.Rpc("fleetmap_live", args);
            }
            return liveResponse.Content ?? "";
        }

        /// <summary>
        /// A UTC instant as the map's scripts read it: the UTC digits with no zone marked,
        /// to which they add the "Z" themselves.
        /// </summary>
        private static DateTime ForMap(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

        /// <summary>Reads a reference list, reusing the last one for <see cref="ReferenceLifetime"/>.</summary>
        private async Task<List<T>> ReferenceAsync<T>(string key, Func<Task<List<T>>> read)
            where T : Postgrest.Models.BaseModel, new()
        {
            if (_cache.TryGetValue(key, out List<T>? cached) && cached is not null)
                return cached;

            var fresh = await read();
            _cache.Set(key, fresh, ReferenceLifetime);
            return fresh;
        }

        // Only the full map page requires the routes permission. The read-only endpoints
        // below stay available to any signed-in user, so the dashboard's map preview works
        // for roles that can see the dashboard but not the routes page.
        [RequirePermission("routes")]
        public async Task<IActionResult> Index()
        {
            var routesResponse = await _supabase.From<BusRoute>().Get();

            double? south = null, west = null, north = null, east = null;

            foreach (var route in routesResponse.Models)
            {
                if (string.IsNullOrWhiteSpace(route.WaypointsJson))
                    continue;

                var waypoints = JsonSerializer.Deserialize<List<WaypointDto>>(route.WaypointsJson);
                if (waypoints is null)
                    continue;

                foreach (var point in waypoints)
                {
                    south = south is null ? point.Lat : Math.Min(south.Value, point.Lat);
                    north = north is null ? point.Lat : Math.Max(north.Value, point.Lat);
                    west = west is null ? point.Lng : Math.Min(west.Value, point.Lng);
                    east = east is null ? point.Lng : Math.Max(east.Value, point.Lng);
                }
            }

            ViewBag.MapBounds = south is not null
                ? new[] { south.Value, west!.Value, north!.Value, east!.Value }
                : null;

            return View();
        }

        public async Task<IActionResult> Stops(int? routeId)
        {
            // In route order, as every list of routes on the map is. The database returns
            // rows in whatever order it last stored them.
            var routesResponse = await _supabase.From<BusRoute>()
                .Order("route_id", Postgrest.Constants.Ordering.Ascending)
                .Get();
            var stops = new List<StopDto>();

            foreach (var route in routesResponse.Models)
            {
                if (routeId.HasValue && route.RouteId != routeId.Value)
                    continue;

                stops.AddRange(RouteStops.Parse(route.StopsJson).Select(s => new StopDto
                {
                    Name = s.Name,
                    Lat = s.Lat,
                    Lng = s.Lng,
                    RouteName = route.RouteName
                }));
            }

            return Json(stops);
        }

        public async Task<IActionResult> Routes()
        {
            // In route order: the route filter and the legend list them as they come.
            var routesResponse = await _supabase.From<BusRoute>()
                .Order("route_id", Postgrest.Constants.Ordering.Ascending)
                .Get();
            var routeData = routesResponse.Models.Select(r => new
            {
                r.RouteId,
                r.RouteName,
                r.WaypointsJson
            }).ToList();

            return Json(routeData);
        }

        /// <summary>
        /// Live bus positions: the newest telemetry reading for each active trip, joined to
        /// its vehicle, route and driver.
        /// </summary>
        /// <remarks>Occupancy and revenue are calculated here rather than in the browser,
        /// so the markers, tooltips and side panel all show the same numbers.</remarks>
        public async Task<IActionResult> Positions(int? routeId, string? status)
        {
            // What changes from one poll to the next, in one request: the active trips, the
            // recent readings of those on the map, and the fare. The map asks every two
            // seconds and the API logs each request it answers, so these are read together.
            //
            // The readings are bounded to the trips shown, within the recent window, newest
            // first and no more than a thousand, rather than fetching the table and filtering
            // in memory on every poll. A reading is stamped with the driver's phone clock in
            // Philippine time and stored with those digits as though they were UTC, so the
            // cutoff is taken the same way. A cutoff from the UTC clock would sit eight hours
            // earlier and read eight and a half hours of readings rather than thirty minutes.
            //
            // Each trip the snapper has seen asks only for the last reading it used and the
            // ones after it. That reading is the marker's newest when nothing has arrived
            // since, so a poll carries one or two rows a bus rather than its whole window.
            //
            // Everyone watching the same route shares one read, so a second map open, on
            // the dashboard or on another desk, costs nothing more.
            var live = ReadBundle.Parse(await SharedRead.GetAsync(_cache,
                $"fleetmap:live:{routeId?.ToString() ?? "all"}", LiveShared, () => ReadLiveAsync(routeId)));

            var activeTrips = live.Rows<Trip>("trips");
            if (routeId.HasValue)
                activeTrips = activeTrips.Where(t => t.RouteId == routeId.Value).ToList();

            // Scoped to the current operational cycle. Trips dated today already cover it,
            // since a night shift carries its start day's date. A trip dated yesterday is
            // kept only when it genuinely started, meaning a real overnight run that has
            // not been ended.
            //
            // An active trip dated in the past with no start time is stale data left by an
            // older build against this shared database. The map has no date filter, so
            // without this check such rows would render. A background service deletes them,
            // and this keeps them invisible in the meantime.
            var opDay = PhClock.OperationalDay.Date;
            activeTrips = activeTrips.Where(t =>
                t.Date.Date == opDay
                || (t.Date.Date == opDay.AddDays(-1) && t.ActualStartTime is not null)).ToList();

            var activeTripIds = activeTrips.Select(t => t.TripId).ToHashSet();

            // Each list names the columns the map actually reads. Nothing else travels,
            // which matters most for the staff list, whose password hashes have no business
            // leaving the database to draw a driver's name, and for the maintenance log,
            // whose fault details are the largest column in the read and are never shown here.
            var vehicles = await ReferenceAsync("fleetmap:vehicles", async () =>
                (await _supabase.From<Vehicle>()
                    .Select("vehicle_id,plate_number,capacity,route_id,vehicle_status,out_of_service,retired_at")
                    .Get()).Models);

            var routes = await ReferenceAsync("fleetmap:routes", async () =>
                (await _supabase.From<BusRoute>().Get()).Models);

            var users = await ReferenceAsync("fleetmap:users", async () =>
                (await _supabase.From<UserModel>()
                    .Select("user_id,first_name,last_name")
                    .Get()).Models);

            var openLogs = await ReferenceAsync("fleetmap:openlogs", async () =>
                (await _supabase.From<MaintenanceLog>()
                    .Select("log_id,vehicle_id,resolved_at")
                    .Filter<object>("resolved_at", Postgrest.Constants.Operator.Is, null)
                    .Get()).Models);

            // Flagged means an open incident, the same definition the dashboard, dispatch
            // board and vehicle registry use.
            var flaggedVehicleIds = openLogs
                .Where(l => l.ResolvedAt == null && l.VehicleId != null)
                .Select(l => l.VehicleId)
                .ToHashSet();

            // Readings of the trips shown, newest first, so the grouping below takes the most
            // recent reading per trip. Every reading in the window goes to the snapper, which
            // uses each once and in order. The newest is still what the marker's details are
            // read from.
            var readingsByTrip = live.Rows<TelemetryData>("telemetry")
                .Where(t => activeTripIds.Contains(t.TripId))
                .GroupBy(t => t.TripId)
                .ToDictionary(g => g.Key, g => g.ToList());
            var latestByTrip = readingsByTrip
                .ToDictionary(g => g.Key, g => g.Value.OrderByDescending(t => t.Timestamp).First());

            var vehiclesById = vehicles
                .ToDictionary(v => v.VehicleId, v => v);
            var routesById = routes
                .ToDictionary(r => r.RouteId, r => r);
            var usersById = users
                .ToDictionary(u => u.UserId, u => u);

            // One fare per poll, shared across every bus below.
            var fareRate = _fareCalculator.RateFrom(live.Rows<FareConfig>("fare"));

            var positions = new List<BusPositionDto>();
            var movingVehicleIds = new HashSet<string>();

            // Each route's stops on its line, and how fast its buses usually go between them.
            var stopsByRoute = new Dictionary<int, (IReadOnlyList<StopPlace> Places, SegmentSpeeds Speeds)>();
            (IReadOnlyList<StopPlace> Places, SegmentSpeeds Speeds) StopsOf(int id, RouteLine line, BusRoute? route)
            {
                if (!stopsByRoute.TryGetValue(id, out var found))
                {
                    var places = StopEta.Place(line, RouteStops.Parse(route?.StopsJson));
                    stopsByRoute[id] = found = (places, SpeedsFor(id, line, places));
                }
                return found;
            }

            // Moving buses: one marker per vehicle, positioned by its newest reading. A
            // vehicle can appear on more than one active trip in inconsistent data, so only
            // the latest reading is used and the marker does not jump between positions.
            var movingByVehicle = new Dictionary<string, BusPositionDto>();
            foreach (var trip in activeTrips)
            {
                if (trip.VehicleId is null)
                    continue;
                if (!latestByTrip.TryGetValue(trip.TripId, out var telemetry))
                    continue; // no telemetry reported for this trip yet

                vehiclesById.TryGetValue(trip.VehicleId, out var vehicle);

                // Readings are stamped with the phone's Philippine clock.
                var readingAt = ForMap(StoredTimes.WallAsUtc(telemetry.Timestamp));
                if (movingByVehicle.TryGetValue(trip.VehicleId, out var existing) &&
                    existing.Timestamp >= readingAt)
                    continue; // an earlier trip already gave a newer position for this bus

                routesById.TryGetValue(trip.RouteId, out var route);
                usersById.TryGetValue(trip.DriverId, out var driver);

                // On its route line within the snap radius, where it really is otherwise.
                var line = _snaps.LineFor(trip.RouteId, route?.WaypointsJson);
                var snap = _snaps.Advance(trip.TripId, line,
                    readingsByTrip[trip.TripId].Select(r => (r.TelemetryId, r.Timestamp, ToReading(r))));
                var shown = snap?.Shown ?? new GeoPoint((double)telemetry.Latitude, (double)telemetry.Longitude);

                var capacity = vehicle?.Capacity ?? 0;

                // The next stop and the time to it, only while the bus is on its line. Off
                // it, the stop ahead is a guess.
                StopLeg? leg = null;
                IReadOnlyList<StopPlace>? stopPlaces = null;
                double? etaSeconds = null;
                if (line is not null && snap is { OnRoute: true, Along: double along })
                {
                    var (places, speeds) = StopsOf(trip.RouteId, line, route);
                    stopPlaces = places;
                    leg = StopEta.Locate(line, places, along);
                    if (leg is { } l)
                        etaSeconds = StopEta.Seconds(l.Metres,
                                l.Previous >= 0 ? speeds.For(l.Previous, PhClock.Now.Hour) : null,
                                telemetry.Speed is decimal sp ? (double)sp : null)
                            + StopEta.Delay(_snaps.Standing(trip.TripId));
                }

                // Two copies of one number. The counter phone writes the trip's figure
                // every few seconds; the driver app carries its own copy into telemetry and
                // learns the new figure only on its next refresh, so it trails. Nobody is
                // ever counted off a bus, so neither can fall and the higher is the newer.
                // Taking it here is what keeps the map within seconds of the doorway rather
                // than within a driver app refresh of it.
                var passengers = Math.Max(trip.TotalBoarded, telemetry.TotalPassengers);

                movingByVehicle[trip.VehicleId] = new BusPositionDto
                {
                    TripId = trip.TripId,
                    VehicleId = trip.VehicleId,
                    PlateNumber = vehicle?.PlateNumber ?? "—",
                    RouteId = trip.RouteId,
                    RouteName = route?.RouteName ?? "—",
                    Shift = FormatShift(trip),
                    DriverName = FormatDriverName(driver),
                    Status = "On Trip",
                    Lat = shown.Lat,
                    Lng = shown.Lng,
                    RawLat = snap?.Raw.Lat ?? (double)telemetry.Latitude,
                    RawLng = snap?.Raw.Lng ?? (double)telemetry.Longitude,
                    Accuracy = snap?.Accuracy,
                    OnRoute = snap?.OnRoute ?? false,
                    OffRoute = snap?.OffRoute ?? false,
                    Along = snap?.Along,
                    Bearing = snap?.Bearing,
                    Heading = telemetry.Heading ?? 0,
                    Speed = (double)(telemetry.Speed ?? 0),
                    PreviousStop = leg is { Previous: >= 0 } p ? stopPlaces![p.Previous].Name : null,
                    NextStop = leg is { } n ? stopPlaces![n.Next].Name : null,
                    NextStopSeconds = etaSeconds is double e ? (int)Math.Round(e) : null,
                    Passengers = passengers,
                    Capacity = capacity,
                    EstimatedRevenue = _fareCalculator.Estimate(passengers, fareRate),
                    Timestamp = readingAt,
                    OnBreakUntil = BreakSlots.IsOnBreak(trip, PhClock.Now) && BreakSlots.WindowOf(trip) is { } breakWindow
                        ? BreakSlots.Clock(breakWindow.End.TimeOfDay)
                        : null
                };
            }

            positions.AddRange(movingByVehicle.Values);
            foreach (var id in movingByVehicle.Keys)
                movingVehicleIds.Add(id);

            // Each route's terminal comes from its own stops, so a route added later parks
            // its buses at its own terminal without a change here. A bus with no route, or
            // a route with nothing to place it by, parks at the first route that has one.
            var terminals = routes
                .OrderBy(r => r.RouteId)
                .Select(r => (r.RouteId, Stop: RouteStops.Terminal(r.StopsJson, r.WaypointsJson)))
                .Where(t => t.Stop is not null)
                .ToList();
            RouteStops.Stop TerminalFor(int? routeId) =>
                terminals.FirstOrDefault(t => t.RouteId == routeId).Stop
                ?? terminals.FirstOrDefault().Stop
                ?? DefaultTerminal;

            // Parked buses: every vehicle not on a trip, shown stationary at its terminal.
            foreach (var vehicle in vehicles)
            {
                // A retired bus is out of the fleet. The registry leaves it out of its
                // counts, and a map showing one more bus than the registry has is read as
                // a bus nobody can account for.
                if (vehicle.RetiredAt != null)
                    continue;
                if (movingVehicleIds.Contains(vehicle.VehicleId))
                    continue;
                if (routeId.HasValue && vehicle.RouteId != routeId.Value)
                    continue;

                // Grounded takes precedence over a flag, otherwise the operational status
                // applies. These are the vehicle registry's rules.
                var vehicleStatus = vehicle.OutOfService ? "Out of Service"
                    : flaggedVehicleIds.Contains(vehicle.VehicleId) ? "Flagged"
                    : NormalizeParked(vehicle.VehicleStatus);

                routesById.TryGetValue(vehicle.RouteId ?? -1, out var route);
                var terminal = TerminalFor(vehicle.RouteId);

                positions.Add(new BusPositionDto
                {
                    TripId = null,
                    VehicleId = vehicle.VehicleId,
                    PlateNumber = vehicle.PlateNumber ?? "—",
                    RouteId = vehicle.RouteId ?? 0,
                    RouteName = route?.RouteName ?? "—",
                    Shift = "—",
                    DriverName = "Unassigned",
                    Status = vehicleStatus,
                    TerminalName = terminal.Name,
                    Lat = terminal.Lat,
                    Lng = terminal.Lng,
                    RawLat = terminal.Lat,
                    RawLng = terminal.Lng,
                    Heading = 0,
                    Speed = 0,
                    Passengers = 0,
                    Capacity = vehicle.Capacity,
                    EstimatedRevenue = 0,
                    // The moment the board was drawn, written the way a reading's is.
                    Timestamp = ForMap(DateTime.UtcNow)
                });
            }

            if (!string.IsNullOrWhiteSpace(status))
                positions = positions.Where(p =>
                    string.Equals(p.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();

            return Json(positions);
        }

        /// <summary>
        /// The route's learned stop-to-stop speeds, or none while they are first being worked
        /// out. A poll never waits for them: the first after they expire starts the read and
        /// the polls after it pick up the answer.
        /// </summary>
        private SegmentSpeeds SpeedsFor(int routeId, RouteLine line, IReadOnlyList<StopPlace> places)
        {
            var read = SharedRead.GetAsync(_cache, $"fleetmap:speeds:{routeId}", SpeedsLifetime,
                () => LearnSpeedsAsync(routeId, line, places));
            return read.IsCompletedSuccessfully ? read.Result : SegmentSpeeds.None;
        }

        /// <summary>Learns a route's stop-to-stop speeds from its recent trips' readings.</summary>
        private async Task<SegmentSpeeds> LearnSpeedsAsync(int routeId, RouteLine line, IReadOnlyList<StopPlace> places)
        {
            // ponytail: reads every reading of the route's last 60 trips each time the speeds
            // expire. Keep a running tally per stretch instead if that read ever slows the map.
            if (places.Count < 2)
                return SegmentSpeeds.None;

            var since = PhClock.OperationalDay.AddDays(-SpeedHistoryDays).ToString("yyyy-MM-dd");
            var trips = (await _supabase.From<Trip>()
                    .Select("trip_id,date,actual_start_time")
                    .Filter("route_id", Postgrest.Constants.Operator.Equals, routeId.ToString())
                    .Filter("date", Postgrest.Constants.Operator.GreaterThanOrEqual, since)
                    .Order("date", Postgrest.Constants.Ordering.Descending)
                    .Get()).Models
                .Where(t => t.ActualStartTime is not null)
                .Take(SpeedHistoryTrips)
                .ToList();

            var readings = new List<TelemetryData>();
            foreach (var group in trips.Select(t => t.TripId).Chunk(20))
            {
                var ids = group.Cast<object>().ToList();
                readings.AddRange(await PagedRead.AllAsync(() => _supabase.From<TelemetryData>()
                    .Select("telemetry_id,trip_id,latitude,longitude,speed,heading,accuracy,timestamp")
                    .Filter("trip_id", Postgrest.Constants.Operator.In, ids)
                    .Order("telemetry_id", Postgrest.Constants.Ordering.Ascending)));
            }

            // Each trip replayed through the map's own snapping, keeping where it was on the
            // line and when, on the Philippine clock its readings are stamped with.
            var paths = readings
                .GroupBy(r => r.TripId)
                .Select(g =>
                {
                    var state = new SnapState();
                    var path = new List<(DateTime At, double Along)>();
                    foreach (var r in g.OrderBy(r => r.Timestamp).ThenBy(r => r.TelemetryId))
                    {
                        var snap = RouteSnapper.Next(line, state, ToReading(r));
                        if (snap is { OnRoute: true, Along: double along })
                            path.Add((StoredTimes.FromWall(r.Timestamp), along));
                    }
                    return (IReadOnlyList<(DateTime At, double Along)>)path;
                });

            return StopEta.Learn(line, places, paths);
        }

        /// <summary>
        /// A parked bus's day: its trips this operational day with each one's status, its
        /// last inspection, and its open faults.
        /// </summary>
        /// <remarks>
        /// Trip status comes from <see cref="TripStatus.Resolve"/> with the inputs the dispatch
        /// board gives it, leave included, so the two never disagree.
        /// </remarks>
        [RequirePermission("routes")]
        public async Task<IActionResult> BusDetail(string vehicleId)
        {
            if (string.IsNullOrWhiteSpace(vehicleId))
                return BadRequest();

            var day = PhClock.OperationalDay;
            var tripsTask = _supabase.From<Trip>()
                .Filter("date", Postgrest.Constants.Operator.Equals, day.ToString("yyyy-MM-dd"))
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, vehicleId)
                .Get();
            var vehicleTask = _supabase.From<Vehicle>()
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, vehicleId)
                .Get();
            // The newest few, put in order below by when each was really submitted: the
            // column holds two clock conventions, so its own order can be eight hours out.
            var checklistsTask = _supabase.From<BusChecklist>()
                .Select("checklist_id,trip_id,vehicle_id,submitted_at,checklist_status")
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, vehicleId)
                .Order("checklist_id", Postgrest.Constants.Ordering.Descending)
                .Limit(10)
                .Get();
            var faultsTask = _supabase.From<MaintenanceLog>()
                .Select("log_id,checklist_id,vehicle_id,issue_details,created_at,resolved_at")
                .Filter("vehicle_id", Postgrest.Constants.Operator.Equals, vehicleId)
                .Filter<object>("resolved_at", Postgrest.Constants.Operator.Is, null)
                .Get();
            await Task.WhenAll(tripsTask, vehicleTask, checklistsTask, faultsTask);

            var trips = tripsTask.Result.Models.Where(t => t.Date.Date == day).ToList();
            var vehicle = vehicleTask.Result.Models.FirstOrDefault();
            var faults = faultsTask.Result.Models.Where(l => l.ResolvedAt == null).ToList();

            // What TripStatus needs about each trip: its driver, their availability and leave,
            // and the trip's own inspection.
            var driverIds = trips.Select(t => (object)t.DriverId.ToString()).Distinct().ToList();
            var tripIds = trips.Select(t => (object)t.TripId).ToList();
            var drivers = driverIds.Count == 0 ? new List<UserModel>()
                : (await _supabase.From<UserModel>()
                    .Select("user_id,first_name,last_name,account_status")
                    .Filter("user_id", Postgrest.Constants.Operator.In, driverIds)
                    .Get()).Models;
            var availability = driverIds.Count == 0 ? new List<DriverAvailability>()
                : (await _supabase.From<DriverAvailability>()
                    .Filter("user_id", Postgrest.Constants.Operator.In, driverIds)
                    .Get()).Models;
            var tripChecklists = tripIds.Count == 0 ? new List<BusChecklist>()
                : (await _supabase.From<BusChecklist>()
                    .Select("checklist_id,trip_id,vehicle_id,submitted_at,checklist_status")
                    .Filter("trip_id", Postgrest.Constants.Operator.In, tripIds)
                    .Get()).Models;

            var availabilityById = await _assignments.WithLeaveAsync(
                availability.ToDictionary(a => a.UserId, a => a.AvailabilityStatus), day);
            var driversById = drivers.ToDictionary(d => d.UserId);
            var checklistByTrip = tripChecklists
                .GroupBy(c => c.TripId)
                .ToDictionary(g => g.Key, g => g.MaxBy(c => c.SubmittedAt));
            var now = PhClock.Now;

            var dayTrips = trips
                .OrderBy(t => t.ShiftStartTime)
                .Select(t =>
                {
                    driversById.TryGetValue(t.DriverId, out var driver);
                    var view = TripStatus.Resolve(t, vehicle, driver,
                        availabilityById.TryGetValue(t.DriverId, out var a) ? a : null,
                        checklistByTrip.GetValueOrDefault(t.TripId), faults.Count > 0, now);
                    return new
                    {
                        shift = t.ShiftType,
                        window = FormatShift(t),
                        status = view.TripStatus,
                        boarded = t.TotalBoarded,
                    };
                })
                .ToList();

            // A skipped checklist records that no inspection took place, and a pending one
            // that it has not yet; neither says how the bus last fared.
            var last = checklistsTask.Result.Models
                .Where(c => c.ChecklistStatus is not ("Skipped" or "Pending"))
                .MaxBy(StoredTimes.Inspected);

            return Json(new
            {
                seats = vehicle?.Capacity ?? 0,
                trips = dayTrips,
                inspection = last is null ? null : new
                {
                    result = last.ChecklistStatus,
                    at = WhenSaid(StoredTimes.Inspected(last), now),
                },
                faults = faults
                    .OrderByDescending(StoredTimes.Opened)
                    .SelectMany(l => l.IssueDetails?.Issues ?? new List<string>())
                    .Where(i => !string.IsNullOrWhiteSpace(i))
                    .Distinct()
                    .ToList(),
                faultCount = faults.Count,
            });
        }

        /// <summary>A Philippine clock time as "6:12 AM", with the day once it is not today.</summary>
        private static string WhenSaid(DateTime ph, DateTime now)
        {
            var time = ph.ToString("h:mm tt", CultureInfo.InvariantCulture);
            return ph.Date == now.Date ? time
                : ph.Date == now.Date.AddDays(-1) ? "Yesterday, " + time
                : ph.ToString("MMM d", CultureInfo.InvariantCulture) + ", " + time;
        }

        /// <summary>
        /// The GPS check: every trip of one day replayed through the map's own snapping, to
        /// show how the phones' GPS behaved on the real routes.
        /// </summary>
        [RequirePermission("routes")]
        public async Task<IActionResult> GpsCheck(DateTime? date) =>
            View(await BuildGpsCheckAsync(date));

        /// <summary>The GPS check for one day as a spreadsheet, one row per trip and a total.</summary>
        [RequirePermission("routes")]
        public async Task<IActionResult> GpsCheckCsv(DateTime? date)
        {
            var model = await BuildGpsCheckAsync(date);
            var inv = CultureInfo.InvariantCulture;
            static string Csv(string? s) =>
                s is null ? "" : s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
            string Num(double? v, string format) => v is double d ? d.ToString(format, inv) : "";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Trip ID,Date,Route,Bus,Driver,Shift,Status,Readings,On road,On road %,Held,Off route,Ignored,Average to road (m),Worst on road (m),Median accuracy (m),Readings with accuracy");
            foreach (var t in model.Trips)
            {
                var c = t.Check;
                sb.AppendLine(string.Join(",",
                    Csv(t.TripId), model.Date.ToString("yyyy-MM-dd", inv), Csv(t.RouteName), Csv(t.VehicleId),
                    Csv(t.DriverName), Csv(t.Shift), Csv(t.Status),
                    c.Readings, c.OnRoad, Num(c.OnRoadShare * 100, "0.0"), c.Held, c.OffRoute, c.Ignored,
                    Num(c.MeanToRoad, "0.0"), Num(c.WorstToRoad, "0.0"), Num(c.MedianAccuracy, "0.0"), c.WithAccuracy));
            }

            var tot = model.Totals;
            sb.AppendLine();
            sb.AppendLine(string.Join(",",
                "TOTAL", model.Date.ToString("yyyy-MM-dd", inv), "", "", "", "", $"{tot.Trips} trips",
                tot.Readings, tot.OnRoad, Num(tot.OnRoadShare * 100, "0.0"), tot.Held, tot.OffRoute, tot.Ignored,
                "", "", "", tot.WithAccuracy));

            return File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()), "text/csv",
                $"GpsCheck_{model.Date.ToString("yyyy-MM-dd", inv)}.csv");
        }

        /// <summary>Reads one day's trips and their readings, and replays each trip.</summary>
        /// <remarks>
        /// A trip counts when it has readings, or when it was started, since a started trip
        /// with none is itself something the check should show. The route line is the
        /// route's current one; a trip moved to another route mid-shift is measured against
        /// the route it ended on.
        /// </remarks>
        private async Task<GpsCheckViewModel> BuildGpsCheckAsync(DateTime? date)
        {
            var day = (date ?? PhClock.OperationalDay).Date;

            var trips = await PagedRead.AllAsync(() => _supabase.From<Trip>()
                .Filter("date", Postgrest.Constants.Operator.Equals, day.ToString("yyyy-MM-dd"))
                .Order("trip_id", Postgrest.Constants.Ordering.Ascending));

            // Read in groups, so the list of trips in any one request stays short.
            var readings = new List<TelemetryData>();
            foreach (var group in trips.Select(t => t.TripId).Chunk(40))
            {
                var ids = group.Cast<object>().ToList();
                readings.AddRange(await PagedRead.AllAsync(() => _supabase.From<TelemetryData>()
                    .Filter("trip_id", Postgrest.Constants.Operator.In, ids)
                    .Order("telemetry_id", Postgrest.Constants.Ordering.Ascending)));
            }
            var byTrip = readings.GroupBy(r => r.TripId).ToDictionary(g => g.Key, g => g.ToList());

            var routes = await ReferenceAsync("fleetmap:routes", async () =>
                (await _supabase.From<BusRoute>().Get()).Models);
            var users = await ReferenceAsync("fleetmap:users", async () =>
                (await _supabase.From<UserModel>()
                    .Select("user_id,first_name,last_name")
                    .Get()).Models);
            var routesById = routes.ToDictionary(r => r.RouteId, r => r);
            var usersById = users.ToDictionary(u => u.UserId, u => u);

            var model = new GpsCheckViewModel { Date = day };
            foreach (var trip in trips)
            {
                byTrip.TryGetValue(trip.TripId, out var tripReadings);
                if ((tripReadings is null || tripReadings.Count == 0) && trip.ActualStartTime is null)
                    continue;

                routesById.TryGetValue(trip.RouteId, out var route);
                usersById.TryGetValue(trip.DriverId, out var driver);

                var check = Services.GpsCheck.Replay(
                    _snaps.LineFor(trip.RouteId, route?.WaypointsJson),
                    (tripReadings ?? new List<TelemetryData>()).Select(r => (r.Timestamp, r.TelemetryId, ToReading(r))));

                model.Trips.Add(new GpsCheckRow
                {
                    TripId = trip.TripId,
                    RouteName = route?.RouteName ?? $"Route {trip.RouteId}",
                    VehicleId = trip.VehicleId ?? "",
                    DriverName = FormatDriverName(driver),
                    Shift = FormatShift(trip),
                    Status = trip.TripStatus ?? "",
                    Check = check
                });
            }

            var t = model.Totals;
            t.Trips = model.Trips.Count;
            foreach (var row in model.Trips)
            {
                t.Readings += row.Check.Readings;
                t.OnRoad += row.Check.OnRoad;
                t.Held += row.Check.Held;
                t.OffRoute += row.Check.OffRoute;
                t.Ignored += row.Check.Ignored;
                t.WithAccuracy += row.Check.WithAccuracy;
            }

            return model;
        }

        /// <summary>The trip's shift window as a short label.</summary>
        private static string FormatShift(Trip trip)
        {
            static string Fmt(TimeSpan t) =>
                DateTime.Today.Add(t).ToString("htt", CultureInfo.InvariantCulture);

            return $"{Fmt(trip.ShiftStartTime)} – {Fmt(trip.ShiftEndTime)}";
        }

        private static string FormatDriverName(UserModel? driver)
        {
            if (driver is null)
                return "Unassigned";

            var name = $"{driver.FirstName} {driver.LastName}".Trim();
            return string.IsNullOrEmpty(name) ? "Unassigned" : name;
        }

        /// <summary>
        /// A parked bus's status in the registry's vocabulary. Such a bus has no live trip,
        /// so a stored moving or flagged value is stale and reads as ready to deploy.
        /// </summary>
        private static string NormalizeParked(string? vehicleStatus)
        {
            var s = (vehicleStatus ?? "").Trim();
            if (s.Length == 0) return "Ready to Deploy";
            if (s.Equals("Pending", StringComparison.OrdinalIgnoreCase)) return "Pending";
            if (s.Equals("OnTrip", StringComparison.OrdinalIgnoreCase)
                || s.Equals("On Trip", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Active", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Flagged", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Ready", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Ready to Deploy", StringComparison.OrdinalIgnoreCase))
                return "Ready to Deploy";
            return s;
        }

        private static GpsReading ToReading(TelemetryData t) => new(
            new GeoPoint((double)t.Latitude, (double)t.Longitude),
            Heading: t.Heading,
            Speed: t.Speed is decimal s ? (double)s : null,
            Accuracy: t.Accuracy);

        private class WaypointDto
        {
            [JsonPropertyName("lat")]
            public double Lat { get; set; }

            [JsonPropertyName("lng")]
            public double Lng { get; set; }
        }
    }
}
