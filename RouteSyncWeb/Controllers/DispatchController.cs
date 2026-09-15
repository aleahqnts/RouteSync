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
        private readonly SchedulingData _scheduling;
        private readonly TripAssignments _assignments;

        public DispatchController(
            Supabase.Client supabase, AuditLog audit, SchedulingData scheduling, TripAssignments assignments)
        {
            _supabase = supabase;
            _audit = audit;
            _scheduling = scheduling;
            _assignments = assignments;
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
            var checklists = checklistsTask.Result.Models;

            // The availability flag carries no date, so it speaks for the operational day it
            // is read on and no other. Applied to a later board, one sick call would turn
            // every trip that driver has for the rest of the month into an assignment issue.
            // Leave has dates, and is folded in below for whichever day is shown.
            var availability = selected == PhClock.OperationalDay
                ? availabilityTask.Result.Models
                : new List<DriverAvailability>();

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
            availabilityDict = await _assignments.WithLeaveAsync(availabilityDict, selected);

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

            // A replacement for every trip that cannot run as assigned, and for every running
            // trip whose driver needs relieving. Only a pick that breaks nothing is offered
            // from the board: one that costs a rest day or a rest rule is left to the reassign
            // modal, where the cost is spelled out beside it.
            var suggestions = new Dictionary<string, (DriverCandidate? Driver, bool NoDriver, VehicleCandidate? Vehicle, bool NoVehicle)>();
            var needsHelp = trips
                .Where(t => resolved[t.TripId].TripStatus == "Assignment Issue"
                         || (resolved[t.TripId].TripStatus == "Active" && resolved[t.TripId].DriverStatus == "Unavailable"))
                .ToList();

            if (needsHelp.Count > 0)
            {
                try
                {
                    var snapshot = await _scheduling.LoadAsync(selected, selected);
                    foreach (var trip in needsHelp)
                    {
                        var issues = SchedulingRules.IssuesOf(trip, snapshot);
                        var driverSide = SchedulingRules.IsDriverIssue(issues);
                        var vehicleSide = SchedulingRules.IsVehicleIssue(issues);

                        var driver = driverSide
                            ? SchedulingRules.RankDrivers(trip, snapshot).Candidates.FirstOrDefault(c => c.Tier <= 2)
                            : null;
                        var vehicle = vehicleSide
                            ? SchedulingRules.RankVehicles(trip, snapshot).FirstOrDefault(c => c.Tier <= 2)
                            : null;

                        suggestions[trip.TripId] = (driver, driverSide && driver is null,
                                                    vehicle, vehicleSide && vehicle is null);
                    }
                }
                catch (Exception ex)
                {
                    // A suggestion is help, not the board. A failed reading leaves the issues
                    // showing exactly as they did before suggestions existed.
                    System.Diagnostics.Debug.WriteLine($"[Dispatch.Suggestions] {ex}");
                }
            }

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
                        suggestions.TryGetValue(trip.TripId, out var suggestion);

                        shift.Trips.Add(new TripRow
                        {
                            SuggestedDriver = suggestion.Driver,
                            NoDriverSuggestion = suggestion.NoDriver,
                            SuggestedVehicle = suggestion.Vehicle,
                            NoVehicleSuggestion = suggestion.NoVehicle,
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
            // The route's other buses on this shift, so the break picker can say how many
            // already park in each slot.
            var alongsideTask = _supabase.From<Trip>()
                                    .Filter("date", Operator.Equals, trip.Date.ToString("yyyy-MM-dd"))
                                    .Filter("route_id", Operator.Equals, trip.RouteId.ToString())
                                    .Filter("shift_type", Operator.Equals, trip.ShiftType).Get();

            await Task.WhenAll(vehicleTask, driverTask, routeTask, availabilityTask, checklistTask, maintTask, alongsideTask);

            var vehicle = vehicleTask.Result.Models.FirstOrDefault();
            var driver = driverTask.Result.Models.FirstOrDefault();
            var route = routeTask.Result.Models.FirstOrDefault();
            // Read for the trip's own day only, the same as the board: the flag has no date.
            var availability = trip.Date.Date == PhClock.OperationalDay
                ? availabilityTask.Result.Models.FirstOrDefault()
                : null;
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
                ShiftStartTime = TripAssignments.ShiftWindow(trip).Start,
                ShiftEndTime = TripAssignments.ShiftWindow(trip).End,
                RouteName = route?.RouteName ?? "—",

                BreakStart = trip.BreakStart?.ToString(@"hh\:mm"),
                BreakLabel = trip.BreakStart is TimeSpan b ? BreakSlots.Label(b) : null,
                OnBreak = trip.TripStatus == "Active" && BreakSlots.IsOnBreak(trip, PhClock.Now),
                // A running trip's break can still move; the bus may be needed on the road
                // at the hour it was due to park.
                BreakEditable = trip.TripStatus != "Completed" && !TripStatus.Closed(trip, PhClock.Now),
                BreakOptions = BreakSlots.For(trip.ShiftStartTime).Select(slot => new BreakOptionViewModel
                {
                    Value = slot.ToString(@"hh\:mm"),
                    Label = BreakSlots.Label(slot),
                    Others = alongsideTask.Result.Models.Count(t => t.TripId != trip.TripId && t.BreakStart == slot),
                }).ToList(),

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
            availability = await _assignments.WithLeaveAsync(availability, PhClock.OperationalDay);

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
                var conflict = await _assignments.ValidateAssignmentAsync(PhClock.OperationalDay, req.ShiftType, req.VehicleId, req.DriverId, null);
                if (conflict != null) return Conflict(new { conflict });
            }

            // The break fits around the buses already on this route and shift today.
            var alongside = (await _supabase.From<Trip>()
                .Filter("date", Operator.Equals, PhClock.OperationalDay.ToString("yyyy-MM-dd"))
                .Filter("route_id", Operator.Equals, req.RouteId.ToString())
                .Filter("shift_type", Operator.Equals, req.ShiftType)
                .Get()).Models;

            var newTrip = new Trip
            {
                Date = PhClock.OperationalDay,
                ShiftType = req.ShiftType,
                ShiftStartTime = startTime,
                ShiftEndTime = endTime,
                BreakStart = BreakSlots.LeastUsed(startTime, alongside.Select(t => t.BreakStart)),
                RouteId = req.RouteId,
                VehicleId = req.VehicleId,
                DriverId = req.DriverId,
                TripStatus = "Not Yet Started",
                EstimatedRevenue = 0
            };

            var insertResult = await _supabase.From<Trip>().Insert(newTrip);
            var inserted = insertResult.Models.FirstOrDefault();
            await _assignments.SyncTripStatusesAsync();

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

            // Ranked against the trip's own day. Every route is listed, not only the one this
            // trip is on, because a bus can be moved to another one from here.
            var snapshot = await _scheduling.LoadAsync(trip.Date, trip.Date);
            var routes = snapshot.RouteNames.OrderBy(r => r.Key).ToList();
            var issues = SchedulingRules.IssuesOf(trip, snapshot);
            var driverRanking = SchedulingRules.RankDrivers(trip, snapshot);
            var vehicleRanking = SchedulingRules.RankVehicles(trip, snapshot);

            var currentVehicle = snapshot.Vehicles.FirstOrDefault(v =>
                string.Equals(v.VehicleId, trip.VehicleId, StringComparison.OrdinalIgnoreCase));
            var currentDriver = snapshot.Drivers.FirstOrDefault(d => d.UserId == trip.DriverId);

            static object Driver(DriverCandidate c) => new
            {
                driverId = c.DriverId,
                driverName = c.Name,
                rank = c.Rank,
                tier = c.Tier,
                reason = c.Reason,
                warning = c.Warning,
            };

            return Json(new
            {
                tripInfo = new
                {
                    tripId = trip.TripId,
                    shiftType = trip.ShiftType,
                    shiftStart = TripAssignments.ShiftWindow(trip).Start,
                    shiftEnd = TripAssignments.ShiftWindow(trip).End,
                    routeName = snapshot.RouteNames.GetValueOrDefault(trip.RouteId) ?? "",
                    tripStatus = trip.TripStatus,
                    currentVehicleId = trip.VehicleId,
                    currentDriverId = trip.DriverId,
                    currentRouteId = trip.RouteId
                },
                routes = routes.Select(r => new { routeId = r.Key, routeName = r.Value }),

                // What is wrong with the assignment as it stands. The modal preselects the
                // best replacement only on the side that has a problem, so opening it over
                // a healthy trip and pressing save never changes anything by surprise.
                issues,
                driverIssue = SchedulingRules.IsDriverIssue(issues),
                vehicleIssue = SchedulingRules.IsVehicleIssue(issues),

                // The current assignment always stays choosable, whatever the ranking says
                // about it, so a change to one side can leave the other where it is.
                currentVehicle = new
                {
                    vehicleId = trip.VehicleId,
                    plateNumber = currentVehicle?.PlateNumber ?? "",
                },
                currentDriver = new
                {
                    driverId = trip.DriverId,
                    driverName = currentDriver is null
                        ? $"Driver {trip.DriverId}"
                        : $"{currentDriver.FirstName} {currentDriver.LastName}".Trim(),
                },
                vehicles = vehicleRanking.Select(c => new
                {
                    vehicleId = c.VehicleId,
                    plateNumber = c.PlateNumber,
                    rank = c.Rank,
                    tier = c.Tier,
                    reason = c.Reason,
                    warning = c.Warning,
                }),
                drivers = driverRanking.Candidates.Select(Driver),
                driversOnLeave = driverRanking.OnLeave.Select(Driver),
            });
        }

        // POST reassign trip. The checks, the write, the notice and the audit row live in
        // TripAssignments, which the leave queue's cover path goes through as well.
        [HttpPost]
        public async Task<IActionResult> ReassignTrip([FromBody] ReassignTripRequest req)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState.FirstError());
            if (req == null || string.IsNullOrEmpty(req.TripId))
                return BadRequest("Trip ID is required.");

            var senderIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            int.TryParse(senderIdClaim, out var senderId);

            var result = await _assignments.ReassignAsync(
                new ReassignChange(req.TripId, req.DriverId, req.VehicleId, req.RouteId, req.Override),
                senderId);

            // A 409 marks a conflict the dispatcher can confirm past; a 400 is final.
            return result.Outcome switch
            {
                ReassignOutcome.NotFound => NotFound(result.Message),
                ReassignOutcome.Refused => BadRequest(result.Message),
                ReassignOutcome.Conflict => Conflict(new { conflict = result.Message }),
                _ => Ok(new { tripId = result.Trip!.TripId }),
            };
        }

        /// <summary>Moves a trip's break to another of its shift's slots.</summary>
        /// <remarks>
        /// Allowed while the trip is running, since the hour a bus was due to park can be the
        /// hour it is needed most. The driver is told, because the slot is what their app
        /// shows them and what the fleet map labels the bus with.
        /// </remarks>
        [HttpPost]
        public async Task<IActionResult> SetBreak([FromBody] SetBreakRequest req)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState.FirstError());

            var trip = (await _supabase.From<Trip>()
                .Filter("trip_id", Operator.Equals, req.TripId)
                .Get()).Models.FirstOrDefault();
            if (trip == null) return NotFound("Trip not found.");

            if (string.Equals(trip.TripStatus, "Completed", StringComparison.OrdinalIgnoreCase))
                return BadRequest("That trip has finished, so its break can no longer be moved.");

            if (TripStatus.Closed(trip, PhClock.Now))
                return BadRequest("That shift has finished, so its break can no longer be moved.");

            if (!TimeSpan.TryParseExact(req.BreakStart, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var slot)
                || !BreakSlots.IsSlot(trip.ShiftStartTime, slot))
                return BadRequest("That is not one of this shift's break slots.");

            if (trip.BreakStart == slot)
                return Ok(new { breakStart = req.BreakStart, label = BreakSlots.Label(slot) });

            var was = trip.BreakStart;

            await _supabase.From<Trip>()
                .Filter("trip_id", Operator.Equals, trip.TripId)
                .Set(t => t.BreakStart, slot)
                .Update();

            // Best effort and after the write, like every other notice from this board.
            try
            {
                var senderIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                int.TryParse(senderIdClaim, out var senderId);

                await _supabase.From<Message>().Insert(new Message
                {
                    SenderId = senderId,
                    TargetAudience = "Driver",
                    TargetId = trip.DriverId.ToString(),
                    Subject = $"Break changed: {trip.Date:MMM d}",
                    Body = $"Your break on trip {trip.TripId} ({trip.ShiftType} shift, {trip.Date:MMMM d}) "
                         + $"is now {BreakSlots.Label(slot)}. Thank you.",
                    Priority = "Normal",
                    CreatedAt = PhClock.NowForDb,
                });
            }
            catch (Exception ex)
            {
                await _audit.WriteAsync("trip_break_changed",
                    $"could not tell driver {trip.DriverId} that the break on trip {trip.TripId} moved: {ex.Message}",
                    "trips", trip.TripId, outcome: "failed");
            }

            await _audit.WriteAsync("trip_break_changed",
                $"moved the break on trip {trip.TripId} "
                    + (was is TimeSpan w ? $"from {BreakSlots.Clock(w)} " : "")
                    + $"to {BreakSlots.Clock(slot)}",
                "trips", trip.TripId);

            return Ok(new { breakStart = req.BreakStart, label = BreakSlots.Label(slot) });
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
                await _assignments.SyncTripStatusesAsync();
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
    }


}