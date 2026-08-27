using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FleetWise.Models;
using FleetWise.Models.ViewModels;
using FleetWise.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FleetWise.Controllers
{
    [Authorize]
    public class FleetMapController : Controller
    {
        private readonly Supabase.Client _supabase;
        private readonly FareCalculator _fareCalculator;

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
        /// Terminal positions per route, where buses that are not running are shown parked.
        /// A route without an entry uses the first terminal.
        /// </summary>
        /// <remarks>The map spreads each terminal's buses into a grid so their markers do
        /// not overlap.</remarks>
        private static readonly Dictionary<int, (double Lat, double Lng, string Name)> Terminals = new()
        {
            [1] = (14.5466, 121.0285, "EDSA–Ayala Terminal"),
            [2] = (14.5095, 121.0465, "Arca South Terminal"),
        };

        private static (double Lat, double Lng, string Name) TerminalFor(int? routeId) =>
            routeId is int r && Terminals.TryGetValue(r, out var t) ? t : Terminals[1];

        public FleetMapController(Supabase.Client supabase, FareCalculator fareCalculator)
        {
            _supabase = supabase;
            _fareCalculator = fareCalculator;
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

                if (string.IsNullOrWhiteSpace(route.StopsJson))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(route.StopsJson);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var stopElement in root.EnumerateArray())
                        {
                            if (stopElement.TryGetProperty("name", out var nameElement) &&
                                stopElement.TryGetProperty("lat", out var latElement) &&
                                stopElement.TryGetProperty("lng", out var lngElement) &&
                                latElement.TryGetDouble(out var lat) &&
                                lngElement.TryGetDouble(out var lng))
                            {
                                stops.Add(new StopDto
                                {
                                    Name = nameElement.GetString() ?? "Unknown Stop",
                                    Lat = lat,
                                    Lng = lng,
                                    RouteName = route.RouteName
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error parsing stops for route {route.RouteId}: {ex.Message}");
                }
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

            var vehiclesResponse = await _supabase.From<Vehicle>().Get();
            var routesResponse = await _supabase.From<BusRoute>().Get();
            var usersResponse = await _supabase.From<UserModel>().Get();
            var maintenanceResponse = await _supabase.From<MaintenanceLog>().Get();

            // Flagged means an open incident, the same definition the dashboard, dispatch
            // board and vehicle registry use.
            var flaggedVehicleIds = maintenanceResponse.Models
                .Where(l => l.ResolvedAt == null && l.VehicleId != null)
                .Select(l => l.VehicleId)
                .ToHashSet();

            // The telemetry read is bounded to rows belonging to currently active trips
            // within the recent window, rather than fetching the table and filtering in
            // memory on every poll. Ordered newest first, so the grouping below takes the
            // most recent reading per trip. Skipped when nothing is active, which would
            // otherwise build a filter against an empty set.
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

                latestByTrip = telemetryResponse.Models
                    .GroupBy(t => t.TripId)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.Timestamp).First());
            }

            var vehiclesById = vehiclesResponse.Models
                .ToDictionary(v => v.VehicleId, v => v);
            var routesById = routesResponse.Models
                .ToDictionary(r => r.RouteId, r => r);
            var usersById = usersResponse.Models
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

                var capacity = vehicle?.Capacity ?? 0;
                var passengers = telemetry.TotalPassengers;
                var occupancyPct = capacity > 0
                    ? (int)Math.Round(passengers * 100.0 / capacity)
                    : 0;

                // Revenue comes from everyone who boarded and paid, so it follows the
                // trip's cumulative total rather than current occupancy, which falls as
                // passengers leave.
                var boardedForRevenue = Math.Max(trip.TotalBoarded, passengers);

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
                    Lat = (double)telemetry.Latitude,
                    Lng = (double)telemetry.Longitude,
                    Heading = telemetry.Heading ?? 0,
                    Speed = (double)(telemetry.Speed ?? 0),
                    Passengers = passengers,
                    Capacity = capacity,
                    OccupancyPct = occupancyPct,
                    EstimatedRevenue = _fareCalculator.Estimate(boardedForRevenue, fareRate),
                    Timestamp = telemetry.Timestamp
                };
            }

            positions.AddRange(movingByVehicle.Values);
            foreach (var id in movingByVehicle.Keys)
                movingVehicleIds.Add(id);

            // Parked buses: every vehicle not on a trip, shown stationary at its terminal.
            foreach (var vehicle in vehiclesResponse.Models)
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
                    Heading = 0,
                    Speed = 0,
                    Passengers = 0,
                    Capacity = vehicle.Capacity,
                    OccupancyPct = 0,
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

        private class WaypointDto
        {
            [JsonPropertyName("lat")]
            public double Lat { get; set; }

            [JsonPropertyName("lng")]
            public double Lng { get; set; }
        }
    }
}
