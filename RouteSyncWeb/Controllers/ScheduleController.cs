using FleetWise.Models;
using FleetWise.Services;
using FleetWise.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static Postgrest.Constants;

namespace FleetWise.Controllers
{
    [Authorize]
    public class ScheduleController : Controller
    {
        private readonly Supabase.Client _supabase;
        private readonly AuditLog _audit;

        public ScheduleController(Supabase.Client supabase, AuditLog audit)
        {
            _supabase = supabase;
            _audit = audit;
        }

        // Fixed shift windows, matching the times the add trip modal offers.
        private static readonly Dictionary<string, (TimeSpan start, TimeSpan end)> ShiftTimes = new()
        {
            ["Morning"] = (new(6, 0, 0), new(14, 0, 0)),
            ["Afternoon"] = (new(14, 0, 0), new(22, 0, 0)),
            ["Evening"] = (new(22, 0, 0), new(6, 0, 0)),
        };
        private static readonly string[] ShiftOrder = { "Morning", "Afternoon", "Evening" };

        /// <summary>The role a bus is driven by, which is the only one this page books.</summary>
        private const int DriverRoleId = 2;

        // GET weekly planner.
        public async Task<IActionResult> Index(string start)
        {
            // The week the chosen day falls in, always Monday to Sunday. A date picked
            // mid-week would otherwise start the grid on that day, so the same week reads
            // differently depending on which of its days was asked for, and Prev and Next
            // carry the offset along with them.
            var picked = (DateTime.TryParse(start, out var s) ? s : PhClock.Today).Date;
            var weekStart = picked.AddDays(-(((int)picked.DayOfWeek + 6) % 7));
            var weekEnd = weekStart.AddDays(6);

            var routesTask = _supabase.From<BusRoute>().Get();
            var vehiclesTask = _supabase.From<Vehicle>().Get();
            var driversTask = _supabase.From<UserModel>()
                                .Filter("role_id", Operator.Equals, "2")
                                .Filter("account_status", Operator.Equals, "Activated")
                                .Get();
            var availabilityTask = _supabase.From<DriverAvailability>().Get();
            var tripsTask = _supabase.From<Trip>()
                                .Filter("date", Operator.GreaterThanOrEqual, weekStart.ToString("yyyy-MM-dd"))
                                .Filter("date", Operator.LessThanOrEqual, weekEnd.ToString("yyyy-MM-dd"))
                                .Get();
            var leaveTask = _supabase.From<LeaveRequest>()
                                .Filter("status", Operator.Equals, "Approved")
                                .Filter("start_date", Operator.LessThanOrEqual, weekEnd.ToString("yyyy-MM-dd"))
                                .Filter("end_date", Operator.GreaterThanOrEqual, weekStart.ToString("yyyy-MM-dd"))
                                .Get();

            await Task.WhenAll(routesTask, vehiclesTask, driversTask, availabilityTask, tripsTask, leaveTask);

            // A bus or a driver already holding a slot this week stays on its list even when
            // it can no longer be booked. Dropping it would leave that slot showing nothing,
            // and a slot showing nothing is read as cleared and deleted on the next save.
            var trips = tripsTask.Result.Models;
            var bookedVehicles = trips.Select(t => t.VehicleId).ToHashSet();

            var unavailable = availabilityTask.Result.Models
                .Where(a => string.Equals(a.AvailabilityStatus, "Unavailable", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.UserId)
                .ToHashSet();

            // Which days of this week each driver has leave for, and of what kind. A
            // request spans dates, and the grid asks its question one cell at a time, so it
            // is spread out here into the days it actually covers.
            var leaveDays = new Dictionary<int, Dictionary<string, string>>();
            foreach (var request in leaveTask.Result.Models)
            {
                if (!leaveDays.TryGetValue(request.UserId, out var days))
                    leaveDays[request.UserId] = days = new Dictionary<string, string>();

                for (var day = weekStart; day <= weekEnd; day = day.AddDays(1))
                    if (request.StartDate.Date <= day && request.EndDate.Date >= day)
                        days[day.ToString("yyyy-MM-dd")] = request.LeaveType;
            }

            var vm = new ScheduleViewModel
            {
                WeekStart = weekStart,
                WeekEnd = weekEnd,
                Days = Enumerable.Range(0, 7).Select(i => weekStart.AddDays(i)).ToList(),
                PrevWeekStart = weekStart.AddDays(-7).ToString("yyyy-MM-dd"),
                NextWeekStart = weekStart.AddDays(7).ToString("yyyy-MM-dd"),
                Routes = routesTask.Result.Models.OrderBy(r => r.RouteId)
                    .Select(r => new RouteOption { RouteId = r.RouteId, RouteName = r.RouteName }).ToList(),
                Vehicles = vehiclesTask.Result.Models
                    // A flag is advisory and the bus stays schedulable. A grounded bus is
                    // withheld, and one that has left the fleet is not a candidate at all.
                    .Where(v => (!v.OutOfService && v.RetiredAt == null)
                             || bookedVehicles.Contains(v.VehicleId))
                    .OrderBy(v => v.VehicleId)
                    .Select(v => new VehicleOption
                    {
                        VehicleId = v.VehicleId,
                        PlateNumber = v.PlateNumber,
                        Offered = !v.OutOfService && v.RetiredAt == null,
                    }).ToList(),
                // Everyone on the roster, marked rather than withheld. A driver is off on
                // particular days, not for the week, and the two things that take them off
                // are read differently: leave names its dates, while the availability flag
                // names none and so speaks only for the day it was set on. Withholding a
                // driver on either count took them out of Thursday's planning because they
                // called in sick on Monday, and left the grid unable to say why.
                Drivers = driversTask.Result.Models
                    .OrderBy(d => d.FirstName)
                    .Select(d => new DriverOption
                    {
                        DriverId = d.UserId,
                        DriverName = $"{d.FirstName} {d.LastName}",
                        Offered = !unavailable.Contains(d.UserId),
                    }).ToList(),
                LeaveDays = leaveDays,
                TodayInWeek = PhClock.OperationalDay.Date >= weekStart && PhClock.OperationalDay.Date <= weekEnd
                    ? PhClock.OperationalDay.ToString("yyyy-MM-dd")
                    : null,
            };

            foreach (var t in trips.OrderBy(t => t.VehicleId))
            {
                var key = $"{t.RouteId}|{t.ShiftType}|{t.Date:yyyy-MM-dd}";
                if (!vm.Cells.TryGetValue(key, out var list))
                    vm.Cells[key] = list = new List<ScheduleCell>();
                list.Add(new ScheduleCell
                {
                    TripId = t.TripId,
                    VehicleId = t.VehicleId,
                    DriverId = t.DriverId,
                    TripStatus = t.TripStatus
                });
            }

            return View(vm);
        }

        /// <summary>One week's trips, laid out the way the planner draws them.</summary>
        /// <remarks>
        /// The planner loads its own week and no other, so filling one from another needs
        /// a way to read the other. Lanes are numbered here exactly as Index numbers them,
        /// by vehicle, or a row copied forward would land in a different lane than the one
        /// it came from.
        /// </remarks>
        [HttpGet]
        public async Task<IActionResult> Week(string start)
        {
            if (!DateTime.TryParse(start, out var parsed))
                return BadRequest(new { message = "That week could not be read." });

            var weekStart = parsed.Date;
            var weekEnd = weekStart.AddDays(6);

            var trips = (await _supabase.From<Trip>()
                .Filter("date", Operator.GreaterThanOrEqual, weekStart.ToString("yyyy-MM-dd"))
                .Filter("date", Operator.LessThanOrEqual, weekEnd.ToString("yyyy-MM-dd"))
                .Get()).Models;

            var lanes = new Dictionary<string, int>();
            var cells = new List<object>();

            foreach (var t in trips.OrderBy(t => t.VehicleId))
            {
                var key = $"{t.RouteId}|{t.ShiftType}|{t.Date:yyyy-MM-dd}";
                lanes.TryGetValue(key, out var lane);
                lanes[key] = lane + 1;

                cells.Add(new
                {
                    routeId = t.RouteId,
                    shift = t.ShiftType,
                    dayIndex = (int)(t.Date.Date - weekStart).TotalDays,
                    lane,
                    vehicleId = t.VehicleId,
                    driverId = t.DriverId,
                });
            }

            return Json(new
            {
                weekStart = weekStart.ToString("yyyy-MM-dd"),
                weekEnd = weekEnd.ToString("yyyy-MM-dd"),
                cells,
            });
        }

        // POST bulk save.
        [HttpPost]
        public async Task<IActionResult> Save([FromBody] SaveScheduleRequest req)
        {
            // Every failure answers with the same shape, { message } and optionally
            // { conflicts }, so the planner can always say what went wrong rather than
            // falling back to a sentence that names nothing.
            if (!ModelState.IsValid) return BadRequest(new { message = ModelState.FirstError() });
            if (req == null || !DateTime.TryParse(req.WeekStart, out var weekStart)
                            || !DateTime.TryParse(req.WeekEnd, out var weekEnd))
                return BadRequest(new { message = "That week could not be read. Reload the planner and try again." });

            var cells = req.Cells ?? new();

            // Existing trips in the range. Also needed to validate against locked trips,
            // which the grid does not always resend.
            var existingResp = await _supabase.From<Trip>()
                .Filter("date", Operator.GreaterThanOrEqual, weekStart.ToString("yyyy-MM-dd"))
                .Filter("date", Operator.LessThanOrEqual, weekEnd.ToString("yyyy-MM-dd"))
                .Get();
            var existing = existingResp.Models;
            var existingById = existing.ToDictionary(t => t.TripId);

            // Conflicts are validated against the schedule as it will be after this save:
            // the submitted cells, plus any locked trip the grid did not resend, since those
            // still occupy their driver, vehicle and shift.
            //
            // A clash between two locked trips is history the dispatcher cannot change, so
            // it never blocks a save. A conflict is reported only when at least one side is
            // editable, and only the editable cells come back for highlighting.
            //
            // A resent cell identical to its stored trip is unchanged by this save, so it
            // counts as locked. Otherwise it could re-raise a conflict the dispatcher has
            // already overridden. Only a genuinely new or changed cell is editable.
            var submittedIds = cells.Where(c => !string.IsNullOrEmpty(c.TripId)).Select(c => c.TripId).ToHashSet();
            var effective = cells.Select(c =>
            {
                bool unchanged = !string.IsNullOrEmpty(c.TripId)
                    && existingById.TryGetValue(c.TripId, out var et)
                    && et.VehicleId == c.VehicleId && et.DriverId == c.DriverId;
                return (cell: c, locked: unchanged);
            }).ToList();
            effective.AddRange(existing
                .Where(t => !submittedIds.Contains(t.TripId)
                         && (t.TripStatus == "Active" || t.TripStatus == "Completed"))
                .Select(t => (cell: new ScheduleCellInput
                {
                    TripId = t.TripId,
                    VehicleId = t.VehicleId,
                    DriverId = t.DriverId,
                    Shift = t.ShiftType,
                    RouteId = t.RouteId,
                    Date = t.Date.ToString("yyyy-MM-dd"),
                }, locked: true)));

            // A bus or a driver can stop being usable while the planner sits open, and the
            // grid is a picture of the fleet as it was when it loaded. Anything newly booked
            // is therefore checked against the fleet as it stands now.
            //
            // Cells left as they were are not checked. A trip already run on a bus since
            // grounded is history, and re-checking it would leave the week unsavable.
            var refusal = await RefuseUnusableAsync(effective);
            if (refusal.Count > 0)
            {
                return BadRequest(new
                {
                    message = "Some of this cannot be booked.",
                    conflicts = refusal,
                });
            }

            // Conflicts are advisory and can be overridden from the confirmation modal, so
            // the save is blocked only until that acknowledgement arrives. Answered with a
            // 409 rather than a 400, the same way dispatch does, because the two mean
            // different things to the planner: one it may force past, the other it may not.
            var conflicts = FindConflicts(effective);
            conflicts.AddRange(await DriverAwayConflictsAsync(effective));
            if (!req.Override && conflicts.Count > 0)
                return Conflict(new { message = "This schedule breaks a booking rule.", conflicts });

            // A write that fails partway leaves the week half-rewritten, so the planner is
            // told plainly rather than being handed a page it cannot read. The detail goes
            // to the audit trail, where it can be looked up without being shown here.
            try
            {
                // Trips that survive this save, whether kept as they are or updated.
                var keptIds = new HashSet<string>();
                int added = 0, changed = 0, deleted = 0;   // counted for the audit line

                foreach (var c in cells)
                {
                    if (string.IsNullOrEmpty(c.VehicleId) || c.DriverId == 0
                     || string.IsNullOrEmpty(c.Shift) || !DateTime.TryParse(c.Date, out var date))
                        continue;
                    if (!ShiftTimes.TryGetValue(c.Shift, out var window)) continue;

                    if (!string.IsNullOrEmpty(c.TripId) && existingById.TryGetValue(c.TripId, out var trip))
                    {
                        keptIds.Add(trip.TripId);
                        // A trip that has started is locked and never modified.
                        if (trip.TripStatus == "Active" || trip.TripStatus == "Completed") continue;
                        if (trip.VehicleId == c.VehicleId && trip.DriverId == c.DriverId) continue; // unchanged

                        await _supabase.From<Trip>()
                            .Filter("trip_id", Operator.Equals, trip.TripId)
                            .Set(t => t.VehicleId, c.VehicleId)
                            .Set(t => t.DriverId, c.DriverId)
                            .Update();
                        changed++;
                    }
                    else
                    {
                        // A new bus for this route, shift and day.
                        await _supabase.From<Trip>().Insert(new Trip
                        {
                            Date = DateTime.SpecifyKind(date, DateTimeKind.Utc),
                            ShiftType = c.Shift,
                            ShiftStartTime = window.start,
                            ShiftEndTime = window.end,
                            RouteId = c.RouteId,
                            VehicleId = c.VehicleId,
                            DriverId = c.DriverId,
                            TripStatus = "Not Yet Started",
                            EstimatedRevenue = 0
                        });
                        added++;
                    }
                }

                // Trips no longer present in the grid are deleted, except those already started.
                foreach (var t in existing)
                {
                    if (keptIds.Contains(t.TripId)) continue;
                    if (t.TripStatus == "Active" || t.TripStatus == "Completed") continue;

                    await _supabase.From<Trip>()
                        .Filter("trip_id", Operator.Equals, t.TripId)
                        .Delete();
                    deleted++;
                }

                // One planner save can rewrite a whole week, so the audit entry carries the
                // counts. A save that changed nothing is not recorded.
                if (added + changed + deleted > 0)
                {
                    await _audit.WriteAsync("schedule_saved",
                        $"saved the schedule for {weekStart:MMM d} to {weekEnd:MMM d}: "
                            + $"{added} added, {changed} changed, {deleted} removed"
                            + (req.Override ? ", overriding a conflict warning" : ""),
                        "trips");
                }

            }
            catch (Exception ex)
            {
                await _audit.WriteAsync("schedule_saved",
                    $"failed to save the schedule for {weekStart:MMM d} to {weekEnd:MMM d}: {ex.Message}",
                    "trips", outcome: "error");

                return StatusCode(500, new
                {
                    message = "The schedule could not be saved. Reload the planner to see "
                            + "what was written, then try again."
                });
            }

            return Ok();
        }

        /// <summary>
        /// Refuses cells booking a bus or a driver that cannot take the work.
        /// </summary>
        /// <remarks>
        /// The dropdowns already withhold a grounded or retired bus, and a driver whose
        /// account is closed, but they were filled when the page loaded. This is the same
        /// question asked of the fleet as it stands at the moment of saving.
        ///
        /// Unlike a double booking, none of this can be forced past: the answer is not that
        /// the schedule is awkward but that the bus or the driver is unavailable.
        /// </remarks>
        private async Task<List<object>> RefuseUnusableAsync(
            List<(ScheduleCellInput cell, bool locked)> effective)
        {
            var booked = effective
                .Where(e => !e.locked
                         && !string.IsNullOrEmpty(e.cell.VehicleId)
                         && e.cell.DriverId != 0)
                .Select(e => e.cell)
                .ToList();

            var refused = new List<object>();
            if (booked.Count == 0) return refused;

            var vehicleIds = booked.Select(c => c.VehicleId).Distinct().Cast<object>().ToList();
            var driverIds = booked.Select(c => c.DriverId).Distinct().Cast<object>().ToList();

            var vehiclesTask = _supabase.From<Vehicle>()
                .Filter("vehicle_id", Operator.In, vehicleIds).Get();
            var driversTask = _supabase.From<UserModel>()
                .Filter("user_id", Operator.In, driverIds).Get();

            await Task.WhenAll(vehiclesTask, driversTask);

            var vehicles = vehiclesTask.Result.Models.ToDictionary(v => v.VehicleId);
            var drivers = driversTask.Result.Models.ToDictionary(d => d.UserId);

            void Refuse(string message, ScheduleCellInput c) => refused.Add(new
            {
                message,
                cells = new[] { new { routeId = c.RouteId, shift = c.Shift, date = c.Date } },
            });

            const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

            foreach (var c in booked)
            {
                if (!vehicles.TryGetValue(c.VehicleId, out var vehicle))
                    Refuse($"Bus {c.VehicleId} is no longer in the fleet.", c);
                else if (vehicle.RetiredAt != null)
                    Refuse($"Bus {c.VehicleId} has been retired.", c);
                else if (vehicle.OutOfService)
                    Refuse($"Bus {c.VehicleId} is out of service and cannot be booked.", c);

                if (!drivers.TryGetValue(c.DriverId, out var driver) || driver.RoleId != DriverRoleId)
                {
                    Refuse("That driver is no longer on the roster.", c);
                    continue;
                }

                var name = $"{driver.FirstName} {driver.LastName}".Trim();
                if (name.Length == 0) name = $"Driver {driver.UserId}";

                // Being marked unavailable is not refused here. It is a flag with no date
                // on it, so it says nothing about a day other than the one it was set on,
                // and it is reported as a conflict for that day instead.
                if (!string.Equals(driver.AccountStatus, "Activated", OIC))
                    Refuse($"{name}'s account is no longer active.", c);
            }

            return refused;
        }

        /// <summary>
        /// Finds conflicts in the schedule as it will be after the save.
        /// </summary>
        /// <remarks>
        /// A conflict is reported only when at least one cell involved is editable. Two
        /// locked trips clashing is history that cannot be fixed, so it never blocks a
        /// save. Each conflict carries the editable cells so the planner can highlight
        /// exactly what needs changing.
        /// </remarks>
        /// <summary>
        /// Cells booking a driver who is away: on approved leave for that day, or marked
        /// unavailable now.
        /// </summary>
        /// <remarks>
        /// Conflicts rather than refusals. Three days' notice is asked for and never
        /// required, allocation is the dispatcher's to make, and a driver on leave who
        /// offers to cover a sick call is a thing that happens. So this is said plainly
        /// and can be saved past, like a driver working two shifts back to back.
        ///
        /// The two are read differently because they say different things. Leave carries
        /// its dates and is checked against the day each cell sits on. The availability
        /// flag carries none: it says a driver cannot work now, so it is read against the
        /// operational day and no other.
        /// </remarks>
        private async Task<List<ConflictDto>> DriverAwayConflictsAsync(
            List<(ScheduleCellInput cell, bool locked)> effective)
        {
            var results = new List<ConflictDto>();

            var editable = effective
                .Where(e => !e.locked && e.cell.DriverId != 0 && !string.IsNullOrEmpty(e.cell.Date))
                .ToList();
            if (editable.Count == 0) return results;

            var days = editable
                .Select(e => DateTime.TryParse(e.cell.Date, out var d) ? d.Date : (DateTime?)null)
                .Where(d => d.HasValue).Select(d => d!.Value).ToList();
            if (days.Count == 0) return results;

            var driverIds = editable.Select(e => e.cell.DriverId).Distinct().Cast<object>().ToList();
            var today = PhClock.OperationalDay.Date;

            // The week being planned, so a request outside it is never read.
            var leaveTask = _supabase.From<LeaveRequest>()
                    .Filter("user_id", Operator.In, driverIds)
                    .Filter("status", Operator.Equals, "Approved")
                    .Filter("start_date", Operator.LessThanOrEqual, days.Max().ToString("yyyy-MM-dd"))
                    .Filter("end_date", Operator.GreaterThanOrEqual, days.Min().ToString("yyyy-MM-dd"))
                    .Get();
            var namesTask = _supabase.From<UserModel>()
                    .Filter("user_id", Operator.In, driverIds).Get();

            await Task.WhenAll(leaveTask, namesTask);

            // Worth asking only when the week being saved contains the day the flag speaks
            // for. Any other week cannot be affected by it.
            var unavailable = new HashSet<int>();
            if (days.Any(d => d == today))
            {
                var availability = (await _supabase.From<DriverAvailability>()
                    .Filter("user_id", Operator.In, driverIds).Get()).Models;

                foreach (var a in availability)
                    if (string.Equals(a.AvailabilityStatus, "Unavailable", StringComparison.OrdinalIgnoreCase))
                        unavailable.Add(a.UserId);
            }

            var leave = leaveTask.Result.Models;
            var names = namesTask.Result.Models
                .ToDictionary(u => u.UserId, u => $"{u.FirstName} {u.LastName}".Trim());

            foreach (var e in editable)
            {
                if (!DateTime.TryParse(e.cell.Date, out var day)) continue;

                var hit = leave.FirstOrDefault(l => l.UserId == e.cell.DriverId
                                                 && l.StartDate.Date <= day.Date
                                                 && l.EndDate.Date >= day.Date);

                string reason;
                if (hit is not null)
                    reason = $"on approved {hit.LeaveType.ToLowerInvariant()} leave";
                else if (day.Date == today && unavailable.Contains(e.cell.DriverId))
                    reason = "unable to drive";
                else
                    continue;

                var name = names.TryGetValue(e.cell.DriverId, out var n) && n.Length > 0
                    ? n
                    : $"Driver {e.cell.DriverId}";

                results.Add(new ConflictDto
                {
                    Message = $"{name} is {reason} on {FmtDate(e.cell.Date)}.",
                    Cells = new List<CellRef>
                    {
                        new() { RouteId = e.cell.RouteId, Shift = e.cell.Shift, Date = e.cell.Date },
                    },
                });
            }

            return results;
        }

        private static List<ConflictDto> FindConflicts(List<(ScheduleCellInput cell, bool locked)> all)
        {
            var results = new List<ConflictDto>();

            var valid = all.Where(x => !string.IsNullOrEmpty(x.cell.VehicleId) && x.cell.DriverId != 0
                                    && !string.IsNullOrEmpty(x.cell.Shift) && DateTime.TryParse(x.cell.Date, out _))
                           .ToList();

            static CellRef Ref((ScheduleCellInput cell, bool locked) x) =>
                new() { RouteId = x.cell.RouteId, Shift = x.cell.Shift, Date = x.cell.Date };

            // Within one day and shift: a driver or vehicle booked twice.
            foreach (var g in valid.GroupBy(x => new { x.cell.Date, x.cell.Shift }))
            {
                foreach (var dup in g.GroupBy(x => x.cell.DriverId).Where(x => x.Count() > 1))
                {
                    var editable = dup.Where(x => !x.locked).ToList();
                    if (editable.Count == 0) continue; // locked-vs-locked -> not the user's problem
                    results.Add(new ConflictDto
                    {
                        Message = $"This driver is already booked for the {g.Key.Shift} shift on {FmtDate(g.Key.Date)}.",
                        Cells = editable.Select(Ref).ToList()
                    });
                }

                foreach (var dup in g.GroupBy(x => x.cell.VehicleId).Where(x => x.Count() > 1))
                {
                    var editable = dup.Where(x => !x.locked).ToList();
                    if (editable.Count == 0) continue;
                    results.Add(new ConflictDto
                    {
                        Message = $"This bus is already booked for the {g.Key.Shift} shift on {FmtDate(g.Key.Date)}.",
                        Cells = editable.Select(Ref).ToList()
                    });
                }
            }

            // Per driver: no two consecutive shifts, whether adjacent on the same day or an
            // evening shift followed by the next morning.
            foreach (var g in valid.GroupBy(x => x.cell.DriverId))
            {
                var byDayShift = g.GroupBy(x => (Day: DateTime.Parse(x.cell.Date).Date, x.cell.Shift))
                                  .ToDictionary(k => k.Key, k => k.ToList());

                void Pair((DateTime Day, string Shift) a, (DateTime Day, string Shift) b, string message)
                {
                    if (!byDayShift.TryGetValue(a, out var la) || !byDayShift.TryGetValue(b, out var lb)) return;
                    var editable = la.Concat(lb).Where(x => !x.locked).ToList();
                    if (editable.Count == 0) return; // both shifts locked -> immutable, skip
                    results.Add(new ConflictDto { Message = message, Cells = editable.Select(Ref).ToList() });
                }

                foreach (var day in byDayShift.Keys.Select(k => k.Day).Distinct())
                {
                    Pair((day, "Morning"), (day, "Afternoon"),
                        $"This driver is assigned to consecutive Morning and Afternoon shifts on {FmtDate(day.ToString("yyyy-MM-dd"))}.");
                    Pair((day, "Afternoon"), (day, "Evening"),
                        $"This driver is assigned to consecutive Afternoon and Evening shifts on {FmtDate(day.ToString("yyyy-MM-dd"))}.");
                    Pair((day, "Evening"), (day.AddDays(1), "Morning"),
                        $"This driver finishes the Evening shift on {FmtDate(day.ToString("yyyy-MM-dd"))} and starts the Morning shift the next day.");
                }
            }

            return results;
        }

        private static string FmtDate(string isoDate) =>
            DateTime.TryParse(isoDate, out var d) ? d.ToString("MMM d") : isoDate;

        private sealed class ConflictDto
        {
            public string Message { get; set; } = "";
            public List<CellRef> Cells { get; set; } = new();
        }

        private sealed class CellRef
        {
            public int RouteId { get; set; }
            public string Shift { get; set; } = "";
            public string Date { get; set; } = "";
        }
    }
}
