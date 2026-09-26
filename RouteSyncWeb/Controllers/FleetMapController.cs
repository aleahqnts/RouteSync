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

        public FleetMapController(Supabase.Client supabase, FareCalculator fareCalculator, IMemoryCache cache,
            RouteSnapTracker snaps)
        {
            _supabase = supabase;
            _fareCalculator = fareCalculator;
            _cache = cache;
            _snaps = snaps;
        }

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
            var routesResponse = await _supabase.From<BusRoute>().Get();
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
            var routesResponse = await _supabase.From<BusRoute>().Get();
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
            var tripsResponse = await _supabase
                .From<Trip>()
                .Filter("trip_status", Postgrest.Constants.Operator.Equals, "Active")
                .Get();

            var activeTrips = tripsResponse.Models;
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
                    .Get()).Models);

            // Flagged means an open incident, the same definition the dashboard, dispatch
            // board and vehicle registry use.
            var flaggedVehicleIds = openLogs
                .Where(l => l.ResolvedAt == null && l.VehicleId != null)
                .Select(l => l.VehicleId)
                .ToHashSet();

            // The telemetry read is bounded to rows belonging to currently active trips
            // within the recent window, rather than fetching the table and filtering in
            // memory on every poll. Ordered newest first, so the grouping below takes the
            // most recent reading per trip. Skipped when nothing is active, which would
            // otherwise build a filter against an empty set.
            var readingsByTrip = new Dictionary<string, List<TelemetryData>>();
            var latestByTrip = new Dictionary<string, TelemetryData>();
            if (activeTripIds.Count > 0)
            {
                // The cutoff has to be UTC. These timestamps are real UTC instants and the
                // filter string is read as UTC, so a Philippine wall-clock value would sit
                // eight hours ahead and exclude every row.
                var recentCutoff = DateTime.UtcNow.AddMinutes(-RecentTelemetryMinutes);
                var telemetryResponse = await _supabase
                    .From<TelemetryData>()
                    .Filter("trip_id", Postgrest.Constants.Operator.In, activeTripIds.Cast<object>().ToList())
                    .Filter("timestamp", Postgrest.Constants.Operator.GreaterThanOrEqual,
                            recentCutoff.ToString("yyyy-MM-dd HH:mm:ss"))
                    .Order("timestamp", Postgrest.Constants.Ordering.Descending)
                    .Get();

                // Every reading in the window goes to the snapper, which uses each once and in
                // order. The newest is still what the marker's details are read from.
                readingsByTrip = telemetryResponse.Models
                    .GroupBy(t => t.TripId)
                    .ToDictionary(g => g.Key, g => g.ToList());
                latestByTrip = readingsByTrip
                    .ToDictionary(g => g.Key, g => g.Value.OrderByDescending(t => t.Timestamp).First());
            }

            var vehiclesById = vehicles
                .ToDictionary(v => v.VehicleId, v => v);
            var routesById = routes
                .ToDictionary(r => r.RouteId, r => r);
            var usersById = users
                .ToDictionary(u => u.UserId, u => u);

            // One fare lookup per poll, shared across every bus below.
            var fareRate = await _fareCalculator.GetRateAsync();

            var positions = new List<BusPositionDto>();
            var movingVehicleIds = new HashSet<string>();

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

                if (movingByVehicle.TryGetValue(trip.VehicleId, out var existing) &&
                    existing.Timestamp >= telemetry.Timestamp)
                    continue; // an earlier trip already gave a newer position for this bus

                routesById.TryGetValue(trip.RouteId, out var route);
                usersById.TryGetValue(trip.DriverId, out var driver);

                // On its route line within the snap radius, where it really is otherwise.
                var line = _snaps.LineFor(trip.RouteId, route?.WaypointsJson);
                var snap = _snaps.Advance(trip.TripId, line,
                    readingsByTrip[trip.TripId].Select(r => (r.TelemetryId, r.Timestamp, ToReading(r))));
                var shown = snap?.Shown ?? new GeoPoint((double)telemetry.Latitude, (double)telemetry.Longitude);

                var capacity = vehicle?.Capacity ?? 0;

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
                    Passengers = passengers,
                    Capacity = capacity,
                    EstimatedRevenue = _fareCalculator.Estimate(passengers, fareRate),
                    Timestamp = telemetry.Timestamp,
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
                    Timestamp = PhClock.Now
                });
            }

            if (!string.IsNullOrWhiteSpace(status))
                positions = positions.Where(p =>
                    string.Equals(p.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();

            return Json(positions);
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
