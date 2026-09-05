using FleetWise.Models;
using FleetWise.Services;
using FleetWise.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static Postgrest.Constants;


namespace FleetWise.Controllers
{
    [Authorize]
    [RequirePermission("routes")]
    public class DispatchController : Controller
    {
        private readonly Supabase.Client _supabase;
        private readonly AuditLog _audit;

        public DispatchController(Supabase.Client supabase, AuditLog audit)
        {
            _supabase = supabase;
            _audit = audit;
        }

        public async Task<IActionResult> Index(string date)
        {
            // The board covers one operational day, 06:00 to 05:59 the next morning.
            // Before 6 AM that is still the previous calendar day. The header arrows move
            // this a day at a time.
            var selected = DateTime.TryParse(date, out var d) ? d.Date : PhClock.OperationalDay;
            var selStr = selected.ToString("yyyy-MM-dd");

            // A trip is dated by the day it starts. An overnight shift crosses midnight
            // but still belongs to its start day, so the board for a given day is exactly
            // the trips dated that day. Merging in the previous day would show an
            // overnight trip on two boards.
            var tripsTask = _supabase.From<Trip>()
                                       .Filter("date", Operator.Equals, selStr)
                                       .Get();
            var vehiclesTask = _supabase.From<Vehicle>().Get();
            var routesTask = _supabase.From<BusRoute>().Get();
            var driversTask = _supabase.From<UserModel>()
                                       .Filter("role_id", Operator.Equals, "2")
                                       .Filter("account_status", Operator.Equals, "Activated")
                                       .Get();
            var availabilityTask = _supabase.From<DriverAvailability>().Get();
            var checklistsTask = _supabase.From<BusChecklist>().Get();
            var maintTask = _supabase.From<MaintenanceLog>().Get();

            await Task.WhenAll(tripsTask, vehiclesTask, routesTask, driversTask, availabilityTask, checklistsTask, maintTask);

            // Trips for this operational day, including overnight ones, which carry today's date.
            var trips = tripsTask.Result.Models
                .Where(t => t.Date.Date == selected)
                .ToList();
            var vehicles = vehiclesTask.Result.Models;
            var routes = routesTask.Result.Models;
            var drivers = driversTask.Result.Models;
            var availability = availabilityTask.Result.Models;
            var checklists = checklistsTask.Result.Models;

            // A vehicle with an unresolved maintenance log is flagged regardless of any
            // trip, so the flag survives the bus going on trip and outlives the
            // vehicle_status column, which later shifts overwrite.
            var openIncidents = maintTask.Result.Models
                .Where(l => l.ResolvedAt == null && l.VehicleId != null)
                .GroupBy(l => l.VehicleId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.CreatedAt).First());

            var flaggedVehicleIds = openIncidents.Keys.ToHashSet();

            // Lookups keyed by id, used while resolving each trip below.
            var vehicleDict = vehicles.ToDictionary(v => v.VehicleId);
            var driverDict = drivers.ToDictionary(d => d.UserId);
            var availabilityDict = availability.ToDictionary(a => a.UserId, a => a.AvailabilityStatus);

            // What each of them said. Kept apart from the status, because leave is folded
            // into that below and carries no reason of this kind.
            var awayReasons = availability
                .Where(a => string.Equals(a.AvailabilityStatus, "Unavailable", StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(a.Reason))
                .ToDictionary(a => a.UserId, a => a.Reason.Trim());

            // Leave approved for the day this board is showing counts the same as being
            // unavailable, because on that day it is the same thing.
            availabilityDict = await WithLeaveAsync(availabilityDict, selected);

            // One checklist per trip. Where a bus was inspected more than once, the most
            // recent submission wins.
            var checklistDict = checklists
                .GroupBy(c => c.TripId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.SubmittedAt).First());

            // Status comes from TripStatus so this board, the trip detail modal and the
            // counters below cannot drift apart. See that class for the rules.
            (Vehicle Vehicle, UserModel Driver, string VehicleStatus, string DriverStatus, string TripStatus, bool Flagged, TimeSpan? Late) Resolve(Trip trip)
            {
                vehicleDict.TryGetValue(trip.VehicleId, out var vehicle);
                driverDict.TryGetValue(trip.DriverId, out var driver);
                var driverAvail = availabilityDict.TryGetValue(trip.DriverId, out var avail) ? avail : "Available";
                var cl = checklistDict.TryGetValue(trip.TripId, out var c0) ? c0 : null;

                var view = TripStatus.Resolve(
                    trip, vehicle, driver, driverAvail, cl,
                    flaggedVehicleIds.Contains(trip.VehicleId), PhClock.Now);

                return (vehicle, driver, view.VehicleStatus, view.DriverStatus, view.TripStatus, view.VehicleFlagged, view.Late);
            }

            var resolved = new Dictionary<string, (Vehicle Vehicle, UserModel Driver, string VehicleStatus, string DriverStatus, string TripStatus, bool Flagged, TimeSpan? Late)>();
            foreach (var trip in trips)
            {
                try { resolved[trip.TripId] = Resolve(trip); }
                catch { resolved[trip.TripId] = (null, null, "Pending", "Available", "Pending", false, null); }
            }

            // Header counters.
            int activeTrips = trips.Count(t => resolved[t.TripId].TripStatus == "Active");
            // Awaiting departure means not started, not finished, not already missed, and
            // not overdue. A missed trip is past its window, so nothing is awaiting it;
            // an overdue one is counted beside this figure rather than inside it, or the
            // one number that needs acting on hides in the one that does not.
            int notStarted = trips.Count(t =>
                resolved[t.TripId].TripStatus != "Active"
                && resolved[t.TripId].TripStatus != "Completed"
                && resolved[t.TripId].TripStatus != "Missed"
                && resolved[t.TripId].Late is null);

            int delayed = trips.Count(t => resolved[t.TripId].Late is not null);
            int unassigned = trips.Count(t => resolved[t.TripId].TripStatus == "Assignment Issue");
            // Counted from the day on screen rather than the whole fleet. A bus with an
            // open fault that nobody is scheduled to drive is the vehicles tab's business,
            // and reporting it here sends a dispatcher looking for a trip that does not exist.
            var vehiclesOnDuty = trips.Select(t => t.VehicleId).Where(v => v != null).ToHashSet();
            var driversOnDuty = trips.Select(t => t.DriverId).ToHashSet();

            int flaggedVehicles = vehiclesOnDuty.Count(v => flaggedVehicleIds.Contains(v));
            int unavailableDrivers = availability.Count(a =>
                a.AvailabilityStatus == "Unavailable" && driversOnDuty.Contains(a.UserId));

            // Trips grouped by route, then by shift.
            var vm = new DispatchViewModel
            {
                ScheduleDate = selected,
                PrevDate = selected.AddDays(-1).ToString("yyyy-MM-dd"),
                NextDate = selected.AddDays(1).ToString("yyyy-MM-dd"),
                IsToday = selected == PhClock.OperationalDay,
                ActiveTrips = activeTrips,
                TripsNotStarted = notStarted,
                DelayedTrips = delayed,
                UnassignedTrips = unassigned,
                FlaggedVehicles = flaggedVehicles,
                UnavailableDrivers = unavailableDrivers
            };

            foreach (var route in routes.OrderBy(r => r.RouteId))
            {
                var routeTrips = trips.Where(t => t.RouteId == route.RouteId).ToList();

                var routeGroup = new RouteDispatchGroup
                {
                    RouteId = route.RouteId,
                    RouteName = route.RouteName,
                    NeedsAssignment = routeTrips.Any(t => resolved[t.TripId].TripStatus == "Assignment Issue")
                };

                if (!routeTrips.Any())
                {
                    // No trips on this route today. The view renders an empty state for
                    // the route card.
                    vm.Routes.Add(routeGroup);
                    continue;
                }

                // Grouped by shift.
                var shiftGroups = routeTrips
                    .GroupBy(t => new { t.ShiftType, t.ShiftStartTime, t.ShiftEndTime })
                    .OrderBy(g => g.Key.ShiftStartTime);

                foreach (var shiftGroup in shiftGroups)
                {
                    // The shift window is built on the selected operational day, so an
                    // overnight shift ends on the following morning. The flag lets the view
                    // mark it, rather than appearing to end the same morning it started.
                    var startTs = shiftGroup.Key.ShiftStartTime;
                    var endTs = shiftGroup.Key.ShiftEndTime;
                    bool overnight = endTs <= startTs;
                    var startDt = selected.Add(startTs);
                    var endDt = selected.Add(endTs).AddDays(overnight ? 1 : 0);

                    var shift = new ShiftGroup
                    {
                        ShiftType = shiftGroup.Key.ShiftType,
                        ShiftStartTime = startDt.ToString("h:mm tt"),
                        ShiftEndTime = endDt.ToString("h:mm tt"),
                        IsOvernight = overnight
                    };

                    foreach (var trip in shiftGroup.OrderBy(t => t.VehicleId))
                    {
                        var r = resolved[trip.TripId];

                        shift.Trips.Add(new TripRow
                        {
                            TripId = trip.TripId,
                            VehicleId = trip.VehicleId,
                            PlateNumber = r.Vehicle?.PlateNumber ?? "—",
                            VehicleStatus = r.VehicleStatus,
                            DriverName = r.Driver != null
                                ? $"{r.Driver.FirstName} {r.Driver.LastName}"
                                : "Unassigned",
                            DriverId = trip.DriverId,
                            DriverStatus = r.DriverStatus,
                            TripStatus = r.TripStatus,
                            Flagged = r.Flagged,
                            NeedsRelief = r.TripStatus == "Active" && r.DriverStatus == "Unavailable",
                            LateBy = r.Late,
                            AssignmentIssueReason =
                                r.TripStatus == "Assignment Issue"
                                    ? BuildIssueReason(r.Vehicle, r.DriverStatus,
                                        openIncidents.GetValueOrDefault(trip.VehicleId),
                                        awayReasons.GetValueOrDefault(trip.DriverId))
                                : r.TripStatus == "Active" && r.DriverStatus == "Unavailable"
                                    ? "Driver reported they cannot drive"
                                        + (awayReasons.TryGetValue(trip.DriverId, out var why) ? $": {why}" : "")
                                        + ". Send a relief driver."
                                // The bus did not run, so the trip is still missed and still
                                // counts as service that was not delivered. What changes is
                                // that the driver is not carrying it unexplained: leave can
                                // be granted for a day already past, and without this the
                                // record reads the same as a driver who simply failed to
                                // appear.
                                : r.TripStatus == "Missed" && r.DriverStatus == "On Leave"
                                    ? "The driver was on approved leave for this day."
                                    : null
                        });
                    }

                    routeGroup.Shifts.Add(shift);
                }

                vm.Routes.Add(routeGroup);
            }

            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> GetTripDetails(string id)
        {
            if (string.IsNullOrEmpty(id))
                return BadRequest("Trip ID is required.");

            var tripResult = await _supabase.From<Trip>()
                .Filter("trip_id", Operator.Equals, id)
                .Get();
            var trip = tripResult.Models.FirstOrDefault();

            if (trip == null)
                return NotFound();

            var vehicleTask = _supabase.From<Vehicle>()
                                    .Filter("vehicle_id", Operator.Equals, trip.VehicleId).Get();
            var driverTask = _supabase.From<UserModel>()
                                    .Filter("user_id", Operator.Equals, trip.DriverId.ToString()).Get();
            var routeTask = _supabase.From<BusRoute>()
                                    .Filter("route_id", Operator.Equals, trip.RouteId.ToString()).Get();
            var availabilityTask = _supabase.From<DriverAvailability>()
                                    .Filter("user_id", Operator.Equals, trip.DriverId.ToString()).Get();
            var checklistTask = _supabase.From<BusChecklist>()
                                    .Filter("trip_id", Operator.Equals, id).Get();
            // The flag lives on the incident, not the inspection, so this modal has to
            // read the same maintenance log the board and the vehicles tab read.
            var maintTask = _supabase.From<MaintenanceLog>()
                                    .Filter("vehicle_id", Operator.Equals, trip.VehicleId).Get();

            await Task.WhenAll(vehicleTask, driverTask, routeTask, availabilityTask, checklistTask, maintTask);

            var vehicle = vehicleTask.Result.Models.FirstOrDefault();
            var driver = driverTask.Result.Models.FirstOrDefault();
            var route = routeTask.Result.Models.FirstOrDefault();
            var availability = availabilityTask.Result.Models.FirstOrDefault();
            // Only the inspection of the bus currently on this trip. After a reassignment
            // an earlier checklist describes a different bus.
            var checklist = checklistTask.Result.Models
                .Where(c => string.Equals(c.VehicleId, trip.VehicleId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.SubmittedAt)
                .FirstOrDefault();

            // Resolved through TripStatus, the same call the dispatch board makes, so
            // the badge here cannot disagree with the row behind it.
            var vehicleFlagged = maintTask.Result.Models.Any(l => l.ResolvedAt == null);

            var view = TripStatus.Resolve(
                trip, vehicle, driver, availability?.AvailabilityStatus, checklist,
                vehicleFlagged, PhClock.Now);

            var vehicleStatus = view.VehicleStatus;
            var driverStatus = view.DriverStatus;
            var resolvedTripStatus = view.TripStatus;

            var vm = new TripDetailViewModel
            {
                TripId = trip.TripId,
                TripStatus = resolvedTripStatus,
                ShiftType = trip.ShiftType,
                ShiftStartTime = FormatShiftWindow(trip).Start,
                ShiftEndTime = FormatShiftWindow(trip).End,
                RouteName = route?.RouteName ?? "—",
                VehicleId = trip.VehicleId,
                PlateNumber = vehicle?.PlateNumber ?? "—",
                VehicleStatus = vehicleStatus,
                DriverName = driver != null ? $"{driver.FirstName} {driver.LastName}" : "Unassigned",
                DriverId = trip.DriverId.ToString(),
                DriverStatus = driverStatus,

                IsCompleted = trip.TripStatus == "Completed",
                TotalBoarded = trip.TripStatus == "Completed" ? trip.TotalBoarded : null,
                EstimatedRevenue = trip.TripStatus == "Completed" ? trip.EstimatedRevenue : null,
                // The stored digits are already Philippine wall-clock time. Postgrest
                // deserializes the "+00:00" value as a local-kind DateTime, so formatting
                // it directly adds eight hours. Normalizing back to UTC prints the digits
                // as stored.
                ActualStartTime = trip.ActualStartTime?.ToUniversalTime().ToString("h:mm tt"),
                ActualEndTime = trip.ActualEndTime?.ToUniversalTime().ToString("h:mm tt"),

                Checklist = checklist != null ? new TripChecklistViewModel
                {
                    ChecklistId = checklist.ChecklistId,
                    SubmittedAt = checklist.SubmittedAt,
                    ChecklistStatus = checklist.ChecklistStatus,
                    Notes = checklist.Notes,
                    ExteriorInspection = checklist.ExteriorInspection ?? new(),
                    EngineCompartment = checklist.EngineCompartment ?? new(),
                    InteriorInspection = checklist.InteriorInspection ?? new(),
                    BrakeSafety = checklist.BrakeSafety ?? new(),
                    PassengerSystems = checklist.PassengerSystems ?? new(),
                } : null
            };

            if (checklist != null)
            {
                var logs = await _supabase.From<MaintenanceLog>()
                    .Filter("checklist_id", Operator.Equals, checklist.ChecklistId.ToString())
                    .Get();

                vm.MaintenanceLogs = logs.Models.Select(l => new TripMaintenanceLogViewModel
                {
                    LogId = l.LogId,
                    IssueDetails = l.IssueDetails?.Issues ?? new(),
                    IsCritical = l.IssueDetails?.IsCritical == true,
                    CriticalIssues = l.IssueDetails?.CriticalIssues ?? new(),
                    MaintenanceStatus = l.MaintenanceStatus,
                    CreatedAt = l.CreatedAt,
                    ResolvedAt = l.ResolvedAt,
                    Remarks = l.Remarks
                }).ToList();
            }

            return Json(vm);
        }

        // GET options for Add Trip modal.
        [HttpGet]
        public async Task<IActionResult> GetAddTripOptions()
        {
            var today = PhClock.OperationalDay.ToString("yyyy-MM-dd");

            var tripsTask = _supabase.From<Trip>()
                                        .Filter("date", Operator.Equals, today)
                                        .Get();
            var vehiclesTask = _supabase.From<Vehicle>().Get();
            var routesTask = _supabase.From<BusRoute>().Get();
            var driversTask = _supabase.From<UserModel>()
                                        .Filter("role_id", Operator.Equals, "2")
                                        .Filter("account_status", Operator.Equals, "Activated")
                                        .Get();
            var availTask = _supabase.From<DriverAvailability>().Get();

            await Task.WhenAll(tripsTask, vehiclesTask, routesTask, driversTask, availTask);

            var todayTrips = tripsTask.Result.Models;
            var vehicles = vehiclesTask.Result.Models.Where(v => v.RetiredAt == null).ToList();
            var routes = routesTask.Result.Models;
            var drivers = driversTask.Result.Models;
            var availability = availTask.Result.Models
                                        .ToDictionary(a => a.UserId, a => a.AvailabilityStatus);

            // A trip added here is for the operational day on screen, so leave approved for
            // that day counts the same as being unavailable. Without this the board reports
            // an assignment issue for a trip it just let the dispatcher create.
            availability = await WithLeaveAsync(availability, PhClock.OperationalDay);

            // Shifts each vehicle is already booked for today.
            var vehicleBookedShifts = todayTrips
                .GroupBy(t => t.VehicleId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(t => t.ShiftType).Distinct().ToList()
                );

            // Shifts each driver is already booked for today.
            var driverBookedShifts = todayTrips
                .GroupBy(t => t.DriverId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(t => t.ShiftType).Distinct().ToList()
                );

            var vm = new AddTripOptionsViewModel
            {
                // Marked rather than withheld, for the reason the driver list is: an option
                // that is simply gone reads as a fault in the page, and leaves the
                // dispatcher hunting for a shift they can see on the board behind them.
                ClosedShifts = TripStatus.Windows
                    .Where(w => TripStatus.Closed(PhClock.OperationalDay, w.Value.Start, w.Value.End, PhClock.Now))
                    .Select(w => w.Key)
                    .ToList(),

                Routes = routes
                    .OrderBy(r => r.RouteId)
                    .Select(r => new RouteOption
                    {
                        RouteId = r.RouteId,
                        RouteName = r.RouteName
                    }).ToList(),

                // A flag is advisory and the bus stays deployable. A grounded bus is
                // withheld, and one that has left the fleet is not a candidate at all.
                Vehicles = vehicles
                    .Where(v => !v.OutOfService && v.RetiredAt == null)
                    .OrderBy(v => v.VehicleId)
                    .Select(v => new VehicleOption
                    {
                        VehicleId = v.VehicleId,
                        PlateNumber = v.PlateNumber,
                        BookedShifts = vehicleBookedShifts.TryGetValue(v.VehicleId, out var vs)
                                        ? vs : new()
                    }).ToList(),

                // Everyone still on the roster. A driver who cannot be booked is listed
                // and marked rather than dropped: a name that is simply absent reads as
                // resigned, and leaves the dispatcher hunting for somebody who is standing
                // in front of them.
                Drivers = drivers
                    .OrderBy(d => d.FirstName)
                    .Select(d =>
                    {
                        availability.TryGetValue(d.UserId, out var status);
                        var reason = status switch
                        {
                            "On Leave" => "on approved leave",
                            "Unavailable" => "unable to drive",
                            _ => null,
                        };

                        return new DriverOption
                        {
                            DriverId = d.UserId,
                            DriverName = $"{d.FirstName} {d.LastName}",
                            Offered = reason is null,
                            Unavailable = reason,
                            BookedShifts = driverBookedShifts.TryGetValue(d.UserId, out var ds)
                                            ? ds : new()
                        };
                    }).ToList()
            };

            return Json(vm);
        }

        // POST create trip.
        [HttpPost]
        public async Task<IActionResult> CreateTrip([FromBody] CreateTripRequest req)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState.FirstError());
            if (req == null
             || string.IsNullOrEmpty(req.ShiftType)
             || string.IsNullOrEmpty(req.VehicleId)
             || req.RouteId == 0
             || req.DriverId == 0)
                return BadRequest("Missing required fields.");

            // Shift times.
            if (!TimeSpan.TryParse(req.ShiftStartTime, out var startTime)
             || !TimeSpan.TryParse(req.ShiftEndTime, out var endTime))
                return BadRequest("Invalid shift times.");

            // A shift that has finished cannot be booked into. Late is workable, since a
            // bus put on the road at seven still runs most of an evening; past is not,
            // because the trip would be missed the moment it was written.
            //
            // Not overridable. Confirming it would not put the shift back.
            if (TripStatus.Closed(PhClock.OperationalDay, startTime, endTime, PhClock.Now))
                return BadRequest($"The {req.ShiftType} shift has already finished. "
                                + "Pick a shift that is still running.");

            // Scheduling conflicts, whether a double booking or back-to-back shifts, can
            // be overridden by a dispatcher who confirms the warning. A 409 marks that
            // kind of conflict, as distinct from a 400, which is a validation failure the
            // client cannot bypass.
            if (!req.Override)
            {
                var conflict = await ValidateAssignmentAsync(PhClock.OperationalDay, req.ShiftType, req.VehicleId, req.DriverId, null);
                if (conflict != null) return Conflict(new { conflict });
            }

            var newTrip = new Trip
            {
                // Specified as UTC so this serializes to yyyy-MM-dd, matching the filter
                // the board uses.
                Date = DateTime.SpecifyKind(PhClock.OperationalDay, DateTimeKind.Utc),
                ShiftType = req.ShiftType,
                ShiftStartTime = startTime,
                ShiftEndTime = endTime,
                RouteId = req.RouteId,
                VehicleId = req.VehicleId,
                DriverId = req.DriverId,
                TripStatus = "Not Yet Started",
                EstimatedRevenue = 0
            };

            var insertResult = await _supabase.From<Trip>().Insert(newTrip);
            var inserted = insertResult.Models.FirstOrDefault();
            await SyncTripStatuses();

            // An override records that the dispatcher was warned about a clash and
            // proceeded, which is the part of the decision worth auditing.
            await _audit.WriteAsync("trip_created",
                $"created a {req.ShiftType} trip for bus {req.VehicleId} with driver {req.DriverId}"
                    + (req.Override ? ", overriding a scheduling conflict" : ""),
                "trips", inserted?.TripId);

            return Ok(new { tripId = inserted?.TripId });
        }


        // GET options for Reassign Trip modal.
        [HttpGet]
        public async Task<IActionResult> GetReassignOptions(string tripId)
        {
            if (string.IsNullOrEmpty(tripId))
                return BadRequest("Trip ID is required.");

            var today = PhClock.OperationalDay.ToString("yyyy-MM-dd");

            // The trip being reassigned, needed for its shift.
            var tripResp = await _supabase.From<Trip>()
                .Filter("trip_id", Operator.Equals, tripId)
                .Get();
            var trip = tripResp.Models.FirstOrDefault();
            if (trip == null) return NotFound("Trip not found.");

            // Refused here as well as on the write, so the modal never opens over a trip
            // whose save can only be turned down.
            if (string.Equals(trip.TripStatus, "Completed", StringComparison.OrdinalIgnoreCase))
                return BadRequest("That trip has finished and can no longer be reassigned.");

            if (TripStatus.Closed(trip, PhClock.Now))
                return BadRequest("That shift has finished, so the trip can no longer be reassigned.");

            var tripsTask = _supabase.From<Trip>().Filter("date", Operator.Equals, today).Get();
            var vehiclesTask = _supabase.From<Vehicle>().Get();
            var driversTask = _supabase.From<UserModel>()
                                        .Filter("role_id", Operator.Equals, "2")
                                        .Filter("account_status", Operator.Equals, "Activated")
                                        .Get();
            var availTask = _supabase.From<DriverAvailability>().Get();
            // Every route, not only the one this trip is on. A bus can be moved to another
            // one from here.
            var routeTask = _supabase.From<BusRoute>().Get();

            await Task.WhenAll(tripsTask, vehiclesTask, driversTask, availTask, routeTask);

            var todayTrips = tripsTask.Result.Models;
            var vehicles = vehiclesTask.Result.Models.Where(v => v.RetiredAt == null).ToList();
            var drivers = driversTask.Result.Models;
            var availability = availTask.Result.Models.ToDictionary(a => a.UserId, a => a.AvailabilityStatus);
            var routes = routeTask.Result.Models.OrderBy(r => r.RouteId).ToList();
            var route = routes.FirstOrDefault(r => r.RouteId == trip.RouteId);

            // Vehicles already booked in this shift, excluding the trip being reassigned.
            var vehiclesInShift = todayTrips
                .Where(t => t.TripId != tripId && t.ShiftType == trip.ShiftType)
                .Select(t => t.VehicleId)
                .ToHashSet();

            // Drivers already booked in this shift, excluding the trip being reassigned.
            var driversInShift = todayTrips
                .Where(t => t.TripId != tripId && t.ShiftType == trip.ShiftType)
                .Select(t => t.DriverId)
                .ToHashSet();

            // Available vehicles have no open incident and are not already in this shift.
            // The trip's current vehicle is always included, so it can be shown as the
            // selected option.
            var availableVehicles = vehicles
                .Where(v => (!v.OutOfService || v.VehicleId == trip.VehicleId)
                         && (!vehiclesInShift.Contains(v.VehicleId) || v.VehicleId == trip.VehicleId))
                .OrderBy(v => v.VehicleId)
                .Select(v => new
                {
                    vehicleId = v.VehicleId,
                    plateNumber = v.PlateNumber,
                    status = v.VehicleStatus
                });

            // Available drivers are not marked unavailable and not already in this shift.
            // The trip's current driver is always included, so they can be shown as the
            // selected option.
            var availableDrivers = drivers
                .Where(d => (!availability.TryGetValue(d.UserId, out var s) || s != "Unavailable")
                         && (!driversInShift.Contains(d.UserId) || d.UserId == trip.DriverId))
                .OrderBy(d => d.LastName)
                .Select(d => new
                {
                    driverId = d.UserId,
                    driverName = $"{d.FirstName} {d.LastName}"
                });

            return Json(new
            {
                tripInfo = new
                {
                    tripId = trip.TripId,
                    shiftType = trip.ShiftType,
                    shiftStart = FormatShiftWindow(trip).Start,
                    shiftEnd = FormatShiftWindow(trip).End,
                    routeName = route?.RouteName ?? "—",
                    tripStatus = trip.TripStatus,
                    currentVehicleId = trip.VehicleId,
                    currentDriverId = trip.DriverId,
                    currentRouteId = trip.RouteId
                },
                routes = routes.Select(r => new { routeId = r.RouteId, routeName = r.RouteName }),
                vehicles = availableVehicles,
                drivers = availableDrivers
            });
        }

        // POST reassign trip.
        [HttpPost]
        public async Task<IActionResult> ReassignTrip([FromBody] ReassignTripRequest req)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState.FirstError());
            if (req == null || string.IsNullOrEmpty(req.TripId))
                return BadRequest("Trip ID is required.");

            var tripResp = await _supabase.From<Trip>()
                .Filter("trip_id", Operator.Equals, req.TripId)
                .Get();
            var trip = tripResp.Models.FirstOrDefault();
            if (trip == null) return NotFound("Trip not found.");

            // A finished trip is history and a finished shift is history it never made. A
            // running one stays reassignable, which is most of what this endpoint is for:
            // a driver taken ill and a bus that has to come off the road both happen
            // mid-shift.
            //
            // The board disables the button on the same two cases, and this is what makes
            // it true. The button is markup, and a page left open since the shift ended
            // still reaches this.
            if (string.Equals(trip.TripStatus, "Completed", StringComparison.OrdinalIgnoreCase))
                return BadRequest("That trip has finished and can no longer be reassigned.");

            if (TripStatus.Closed(trip, PhClock.Now))
                return BadRequest("That shift has finished, so the trip can no longer be reassigned.");

            // Captured before the update, because a reassignment is only meaningful
            // alongside what it moved away from.
            var wasVehicle = trip.VehicleId;
            var wasDriver = trip.DriverId;
            var wasRoute = trip.RouteId;

            // Only fields that were explicitly changed are written.
            if (!string.IsNullOrEmpty(req.VehicleId))
                trip.VehicleId = req.VehicleId;

            if (req.DriverId.HasValue && req.DriverId.Value > 0)
                trip.DriverId = req.DriverId.Value;

            if (req.RouteId.HasValue && req.RouteId.Value > 0)
                trip.RouteId = req.RouteId.Value;

            // The same overridable conflict gate the create path uses. The check excludes
            // the trip being edited, or an already double-booked trip could never be
            // saved: it would re-detect its own existing conflict and block even an
            // unrelated change. A 409 lets the dispatcher confirm and proceed.
            if (!req.Override)
            {
                var conflict = await ValidateAssignmentAsync(trip.Date, trip.ShiftType, trip.VehicleId, trip.DriverId, trip.TripId);
                if (conflict != null) return Conflict(new { conflict });
            }

            // Filtered update rather than an upsert, which would insert a duplicate row.
            await _supabase.From<Trip>()
                .Filter("trip_id", Operator.Equals, req.TripId)
                .Set(t => t.VehicleId, trip.VehicleId)
                .Set(t => t.DriverId, trip.DriverId)
                .Set(t => t.RouteId, trip.RouteId)
                .Update();

            await SyncTripStatuses();

            // Told plainly, and marked urgent, because a driver who does not read this runs
            // the shift on the route they were given this morning. Best effort and after
            // the write: a notice that fails must not report the reassignment failed and
            // leave the dispatcher looking at a trip the database has already moved.
            if (wasRoute != trip.RouteId)
            {
                try { await NotifyRouteChangeAsync(trip, wasRoute, wasVehicle); }
                catch (Exception ex)
                {
                    await _audit.WriteAsync("trip_reassigned",
                        $"could not tell driver {trip.DriverId} that trip {trip.TripId} moved route: {ex.Message}",
                        "trips", trip.TripId, outcome: "failed");
                }
            }

            var moved = new List<string>();
            if (wasVehicle != trip.VehicleId) moved.Add($"bus {wasVehicle} to {trip.VehicleId}");
            if (wasDriver != trip.DriverId) moved.Add($"driver {wasDriver} to {trip.DriverId}");
            if (wasRoute != trip.RouteId) moved.Add($"route {wasRoute} to {trip.RouteId}");

            await _audit.WriteAsync("trip_reassigned",
                $"reassigned trip {trip.TripId}"
                    + (moved.Count > 0 ? $": {string.Join(", ", moved)}" : " (no change)")
                    + (req.Override ? ", overriding a scheduling conflict" : ""),
                "trips", trip.TripId);

            return Ok(new { tripId = trip.TripId });
        }

        /// <summary>
        /// Tells the driver their shift has been moved to a different route.
        /// </summary>
        /// <remarks>
        /// Through the messages table, which is what the driver app's notifications page
        /// reads, so this arrives in the same place as everything else they are told. Sent
        /// at high priority: it is the difference between a bus running the route it is
        /// needed on and a bus running the one it was given at the start of the day.
        ///
        /// Addressed to the driver the trip now has. Where the driver was swapped in the
        /// same edit, that is the person who needs to know.
        /// </remarks>
        /// <param name="wasVehicle">
        /// The bus the trip held before this edit. Named only when it changed, since a
        /// route change and a bus change often arrive in the same edit and a driver told
        /// about one and not the other walks to the wrong bay.
        /// </param>
        private async Task NotifyRouteChangeAsync(Trip trip, int wasRoute, string wasVehicle)
        {
            var routes = (await _supabase.From<BusRoute>().Get()).Models;
            string Name(int id) => routes.FirstOrDefault(r => r.RouteId == id)?.RouteName ?? $"Route {id}";

            var senderIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            int.TryParse(senderIdClaim, out var senderId);

            // Written to be acted on rather than read through.
            //
            // Named by the trip rather than by the shift, so it matches the identifier on
            // the driver's own screen and answers which trip it is about without them
            // having to work it out. The route it came off is given as well: a driver who
            // has been running one all afternoon needs to know which one is ending, not
            // only which one is beginning.
            //
            // The shift window is left out. The driver was given it this morning and it has
            // not changed, and repeating it buried the one line that was new in three that
            // were not.
            var body = $"Your trip {trip.TripId} is now on {Name(trip.RouteId)}, "
                     + $"previously on {Name(wasRoute)}.";

            // Only when it changed in the same edit. A driver told about the route and not
            // the bus walks to the wrong bay.
            if (!string.Equals(wasVehicle, trip.VehicleId, StringComparison.OrdinalIgnoreCase))
                body += $" Your bus has changed to {trip.VehicleId}.";

            body += " Please update your route accordingly. Thank you.";

            await _supabase.From<Message>().Insert(new Message
            {
                SenderId = senderId,
                TargetAudience = "Driver",
                TargetId = trip.DriverId.ToString(),
                Subject = $"Route change: {Name(trip.RouteId)}",
                Body = body,
                Priority = "High",
                CreatedAt = PhClock.NowForDb
            });
        }

        // Removes a trip. Clearing both the bus and the driver in the reassign modal
        // deletes it, matching how clearing a cell works in the schedule planner. A trip
        // that has started or finished is never deleted.
        [HttpPost]
        public async Task<IActionResult> RemoveTrip([FromBody] RemoveTripRequest req)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState.FirstError());
            if (req == null || string.IsNullOrEmpty(req.TripId))
                return BadRequest("Trip ID is required.");

            var tripResp = await _supabase.From<Trip>()
                .Filter("trip_id", Operator.Equals, req.TripId)
                .Get();
            var trip = tripResp.Models.FirstOrDefault();
            if (trip == null) return NotFound("Trip not found.");

            if (trip.TripStatus == "Active" || trip.TripStatus == "Completed")
                return BadRequest("This trip has already started or completed and can't be removed.");

            await _supabase.From<Trip>()
                .Filter("trip_id", Operator.Equals, req.TripId)
                .Delete();

            // The row is deleted, so this audit entry is the only remaining record of it.
            await _audit.WriteAsync("trip_removed",
                $"removed the {trip.ShiftType} trip {trip.TripId} (bus {trip.VehicleId}, driver {trip.DriverId})",
                "trips", trip.TripId);

            return Ok();
        }

        // GET driver count (all routes, or filtered by routeId).
        [HttpGet]
        public async Task<IActionResult> GetDriverCount(int? routeId)
        {
            var today = PhClock.OperationalDay.ToString("yyyy-MM-dd");

            if (routeId.HasValue)
            {
                // Distinct drivers assigned to this route today.
                var trips = await _supabase.From<Trip>()
                    .Filter("date", Operator.Equals, today)
                    .Filter("route_id", Operator.Equals, routeId.Value.ToString())
                    .Get();

                var driverIds = trips.Models.Select(t => t.DriverId).Distinct().ToList();
                return Json(new { count = driverIds.Count });
            }
            else
            {
                var drivers = await _supabase.From<UserModel>()
                    .Filter("role_id", Operator.Equals, "2")
                    .Filter("account_status", Operator.Equals, "Activated")
                    .Get();

                return Json(new { count = drivers.Models.Count });
            }
        }

        // POST broadcast message to all drivers.
        [HttpPost]
        public async Task<IActionResult> BroadcastMessage([FromBody] BroadcastMessageRequest req)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState.FirstError());
            if (req == null || string.IsNullOrWhiteSpace(req.Body))
                return BadRequest("Message body is required.");

            var senderIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            int.TryParse(senderIdClaim, out var senderId);

            await _supabase.From<Message>().Insert(new Message
            {
                SenderId = senderId,
                TargetAudience = "All",
                TargetId = null,
                Subject = req.Subject?.Trim(),
                Body = req.Body.Trim(),
                Priority = req.Priority ?? "Normal",
                CreatedAt = PhClock.NowForDb
            });

            // The subject only. The body stays in the messages table, which anyone reading
            // this entry can open, so copying it here would widen the exposure of a leak
            // for no benefit.
            await _audit.WriteAsync("message_sent",
                $"broadcast a message to all drivers: {Topic(req.Subject)}",
                "messages");

            return Ok();
        }

        // POST route message (all drivers on a route).
        [HttpPost]
        public async Task<IActionResult> SendRouteMessage([FromBody] RouteMessageRequest req)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState.FirstError());
            if (req == null || string.IsNullOrWhiteSpace(req.Body) || req.RouteId == 0)
                return BadRequest("Route ID and message body are required.");

            var senderIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            int.TryParse(senderIdClaim, out var senderId);

            await _supabase.From<Message>().Insert(new Message
            {
                SenderId = senderId,
                TargetAudience = "Route",
                TargetId = req.RouteId.ToString(),
                Subject = req.Subject?.Trim(),
                Body = req.Body.Trim(),
                Priority = req.Priority ?? "Normal",
                CreatedAt = PhClock.NowForDb
            });

            await _audit.WriteAsync("message_sent",
                $"messaged every driver on route {req.RouteId}: {Topic(req.Subject)}",
                "messages", req.RouteId);

            return Ok();
        }

        // POST trip message (single driver on a trip).
        [HttpPost]
        public async Task<IActionResult> SendTripMessage([FromBody] TripMessageRequest req)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState.FirstError());
            if (req == null || string.IsNullOrWhiteSpace(req.Body) || string.IsNullOrEmpty(req.TripId))
                return BadRequest("Trip ID and message body are required.");

            // The driver is resolved from the trip.
            var tripResp = await _supabase.From<Trip>()
                .Filter("trip_id", Operator.Equals, req.TripId)
                .Get();
            var trip = tripResp.Models.FirstOrDefault();
            if (trip == null) return NotFound("Trip not found.");

            var senderIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            int.TryParse(senderIdClaim, out var senderId);

            await _supabase.From<Message>().Insert(new Message
            {
                SenderId = senderId,
                TargetAudience = "Driver",
                TargetId = trip.DriverId.ToString(),
                Subject = req.Subject?.Trim(),
                Body = req.Body.Trim(),
                Priority = req.Priority ?? "Normal",
                CreatedAt = PhClock.NowForDb
            });

            await _audit.WriteAsync("message_sent",
                $"messaged driver {trip.DriverId} on trip {trip.TripId}: {Topic(req.Subject)}",
                "messages", trip.DriverId);

            return Ok();
        }

        // Update driver availability.
        [HttpPost]
        public async Task<IActionResult> UpdateDriverAvailability(int userId, string status)
        {
            if (status != "Available" && status != "Unavailable")
                return BadRequest("Invalid status.");

            var existing = await _supabase
                .From<DriverAvailability>()
                .Filter("user_id", Operator.Equals, userId.ToString())
                .Single();

            if (existing != null)
            {
                existing.AvailabilityStatus = status;
                existing.UpdatedAt = PhClock.Now;
                await _supabase.From<DriverAvailability>().Upsert(existing);
                await SyncTripStatuses();
            }
            else
            {
                await _supabase.From<DriverAvailability>().Insert(new DriverAvailability
                {
                    UserId = userId,
                    AvailabilityStatus = status,
                    UpdatedAt = PhClock.Now
                });
            }

            await _audit.WriteAsync("driver_availability",
                $"marked driver {userId} as {status}",
                "driver_availability", userId);

            return Ok();
        }

        /// <summary>The message subject for an audit entry. The body is never recorded.</summary>
        private static string Topic(string? subject) =>
            string.IsNullOrWhiteSpace(subject) ? "no subject" : subject.Trim();

        /// <summary>
        /// Formats a trip's shift window against its own date. An end at or before the
        /// start means an overnight shift, whose end rolls to the next morning and is
        /// marked so it cannot be read as ending the morning it began.
        /// </summary>
        private static (string Start, string End) FormatShiftWindow(Trip t)
        {
            bool overnight = t.ShiftEndTime <= t.ShiftStartTime;
            var s = t.Date.Date.Add(t.ShiftStartTime);
            var e = t.Date.Date.Add(t.ShiftEndTime).AddDays(overnight ? 1 : 0);
            return (s.ToString("h:mm tt"), overnight ? $"{e:h:mm tt} (+1)" : e.ToString("h:mm tt"));
        }

        /// <summary>Short reason a trip is an assignment issue, shown in the badge tooltip.</summary>
        /// <remarks>
        /// A grounded bus is named along with what grounded it, because "out of service"
        /// alone leaves a dispatcher to go hunting through the vehicles tab to find out
        /// whether this is a reassignment or something being repaired.
        /// </remarks>
        private static string BuildIssueReason(Vehicle vehicle, string driverStatus,
            MaintenanceLog openIncident, string awayReason = null)
        {
            var parts = new List<string>();
            if (vehicle?.OutOfService == true)
            {
                var critical = openIncident?.IssueDetails?.IsCritical == true;
                parts.Add(critical
                    ? $"Bus grounded by inspection: {openIncident.IssueDetails.CriticalSummary}"
                    : "Bus is out of service");
            }
            if (driverStatus == "Unavailable")
                parts.Add("Driver reported they cannot drive"
                    + (string.IsNullOrWhiteSpace(awayReason) ? "" : $": {awayReason}"));
            if (driverStatus == "On Leave") parts.Add("Driver is on approved leave");
            return parts.Count > 0 ? string.Join(" · ", parts) : "Needs reassignment";
        }

        /// <summary>The shift that immediately follows this one on the same day.</summary>
        private static readonly Dictionary<string, string> NextShift = new()
        {
            ["Morning"] = "Afternoon",
            ["Afternoon"] = "Evening",
        };

        /// <summary>
        /// Checks a proposed vehicle and driver assignment against existing trips.
        /// </summary>
        /// <returns>A description of the clash, or null when the assignment is clear.</returns>
        /// <remarks>
        /// The rules match the schedule planner: no driver or vehicle twice in the same
        /// shift on the same day, and no driver in consecutive shifts, which includes an
        /// evening shift followed by the next morning.
        /// </remarks>
        private async Task<string> ValidateAssignmentAsync(
            DateTime date, string shift, string vehicleId, int driverId, string excludeTripId)
        {
            var prev = date.AddDays(-1).ToString("yyyy-MM-dd");
            var next = date.AddDays(1).ToString("yyyy-MM-dd");

            // The day before, the day itself and the day after, which covers every rule.
            var resp = await _supabase.From<Trip>()
                .Filter("date", Operator.GreaterThanOrEqual, prev)
                .Filter("date", Operator.LessThanOrEqual, next)
                .Get();
            var trips = resp.Models.Where(t => t.TripId != excludeTripId).ToList();

            string Fmt(DateTime d) => d.ToString("MMMM d, yyyy");

            // Leave approved for this day. A conflict rather than a refusal: notice is
            // asked for and never required, allocation is the dispatcher's to make, and a
            // driver on leave who offers to cover a sick call is a thing that happens. It
            // is said plainly and can be confirmed past, like a driver working two shifts
            // back to back.
            var onLeave = (await _supabase.From<LeaveRequest>()
                    .Filter("user_id", Operator.Equals, driverId.ToString())
                    .Filter("status", Operator.Equals, "Approved")
                    .Filter("start_date", Operator.LessThanOrEqual, date.ToString("yyyy-MM-dd"))
                    .Filter("end_date", Operator.GreaterThanOrEqual, date.ToString("yyyy-MM-dd"))
                    .Get()).Models.FirstOrDefault(l => LeaveEntitlement.CoversDay(l, date));

            if (onLeave is not null)
                return $"This driver is on approved {onLeave.LeaveType.ToLowerInvariant()} leave on {Fmt(date)}.";

            // Same shift on the same day: a duplicate driver or vehicle.
            foreach (var t in trips.Where(t => t.Date.Date == date.Date && t.ShiftType == shift))
            {
                if (t.DriverId == driverId)
                    return $"This driver is already booked for the {shift} shift on {Fmt(date)}.";
                if (t.VehicleId == vehicleId)
                    return $"This bus is already booked for the {shift} shift on {Fmt(date)}.";
            }

            // Consecutive shifts for the driver on the same day.
            var driverTrips = trips.Where(t => t.DriverId == driverId).ToList();
            foreach (var t in driverTrips.Where(t => t.Date.Date == date.Date))
            {
                if (NextShift.TryGetValue(shift, out var after) && t.ShiftType == after)
                    return $"This driver is assigned to consecutive {shift} and {after} shifts on {Fmt(date)}.";
                if (NextShift.TryGetValue(t.ShiftType, out var after2) && after2 == shift)
                    return $"This driver is assigned to consecutive {t.ShiftType} and {shift} shifts on {Fmt(date)}.";
            }

            // An evening shift and the following morning, checked in both directions.
            if (shift == "Evening" && driverTrips.Any(t => t.Date.Date == date.AddDays(1).Date && t.ShiftType == "Morning"))
                return $"This driver finishes the Evening shift on {Fmt(date)} and starts the Morning shift the next day.";
            if (shift == "Morning" && driverTrips.Any(t => t.Date.Date == date.AddDays(-1).Date && t.ShiftType == "Evening"))
                return $"This driver finishes the Evening shift the day before and starts the Morning shift on {Fmt(date)}.";

            return null;
        }

        /// <summary>
        /// Drivers whose approved leave covers a day, written over their availability.
        /// </summary>
        /// <remarks>
        /// Availability answers whether a driver can work right now and carries no date,
        /// so it cannot say that somebody is off next Tuesday. Leave can, and on the day
        /// itself the two mean the same thing to a board: this trip needs another driver.
        /// Folded in here rather than at each place that reads availability, so the board,
        /// the stored status and the reason shown all come from one rule.
        /// </remarks>
        private async Task<Dictionary<int, string>> WithLeaveAsync(
            Dictionary<int, string> availability, DateTime day)
        {
            var onLeave = (await _supabase.From<LeaveRequest>()
                    .Filter("status", Operator.Equals, "Approved")
                    .Filter("start_date", Operator.LessThanOrEqual, day.ToString("yyyy-MM-dd"))
                    .Filter("end_date", Operator.GreaterThanOrEqual, day.ToString("yyyy-MM-dd"))
                    .Get()).Models;

            foreach (var leave in onLeave.Where(l => LeaveEntitlement.CoversDay(l, day)))
                availability[leave.UserId] = "On Leave";

            return availability;
        }

        private async Task SyncTripStatuses(string date = null)
        {
            date ??= PhClock.Today.ToString("yyyy-MM-dd");

            var tripsTask = _supabase.From<Trip>()
                                       .Filter("date", Operator.Equals, date)
                                       .Get();
            var vehiclesTask = _supabase.From<Vehicle>().Get();
            var availabilityTask = _supabase.From<DriverAvailability>().Get();

            await Task.WhenAll(tripsTask, vehiclesTask, availabilityTask);

            var trips = tripsTask.Result.Models;
            var vehicleDict = vehiclesTask.Result.Models.ToDictionary(v => v.VehicleId);
            var availabilityDict = availabilityTask.Result.Models
                                    .ToDictionary(a => a.UserId, a => a.AvailabilityStatus);

            if (DateTime.TryParse(date, out var syncDay))
                availabilityDict = await WithLeaveAsync(availabilityDict, syncDay);

            foreach (var trip in trips)
            {
                if (trip.TripStatus == "Active" || trip.TripStatus == "Completed")
                    continue;

                vehicleDict.TryGetValue(trip.VehicleId, out var vehicle);
                availabilityDict.TryGetValue(trip.DriverId, out var driverAvail);

                string newStatus;

                // A grounded bus or an unavailable driver blocks the assignment. A flag
                // on its own does not.
                if (vehicle?.OutOfService == true
                    || driverAvail == "Unavailable"
                    || driverAvail == "On Leave")
                    newStatus = "Assignment Issue";
                else if (vehicle?.VehicleStatus == "Pending")
                    newStatus = "Pending";
                else if (vehicle?.VehicleStatus == "Ready to Deploy" && driverAvail == "Available")
                    newStatus = "Not Yet Started";
                else
                    continue;

                if (trip.TripStatus != newStatus)
                {
                    trip.TripStatus = newStatus;
                    await _supabase.From<Trip>().Upsert(trip);
                }
            }
        }
    }


}