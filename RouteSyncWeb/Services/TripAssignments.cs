using FleetWise.Models;
using static Postgrest.Constants;

namespace FleetWise.Services
{
    /// <summary>A change to a trip's bus, driver or route. A null side is left as it is.</summary>
    /// <param name="Override">The dispatcher was shown a scheduling conflict and chose to save anyway.</param>
    public sealed record ReassignChange(
        string TripId, int? DriverId, string? VehicleId, int? RouteId, bool Override);

    public enum ReassignOutcome
    {
        Done,
        NotFound,

        /// <summary>Refused for good: the trip is finished or its shift is over.</summary>
        Refused,

        /// <summary>A scheduling conflict a dispatcher may confirm past.</summary>
        Conflict,
    }

    /// <param name="Message">What to tell the dispatcher when the change was not made.</param>
    /// <param name="Trip">The trip as saved, when it was.</param>
    public sealed record ReassignResult(ReassignOutcome Outcome, string? Message = null, Trip? Trip = null);

    /// <summary>A trip to add by hand: one bus and one driver on one shift of one day.</summary>
    /// <param name="Override">The dispatcher was shown a scheduling conflict and chose to save anyway.</param>
    public sealed record NewTrip(
        DateTime Date, string Shift, TimeSpan Start, TimeSpan End,
        int RouteId, string VehicleId, int DriverId, bool Override);

    /// <summary>
    /// Who drives what: the one path every reassignment goes through, and the assignment
    /// rules it enforces.
    /// </summary>
    /// <remarks>
    /// The dispatch board and the leave queue both move drivers between trips. Going
    /// through the same checks, the same conflict gate, the same notice and the same audit
    /// row is what makes a cover arranged from the leave queue indistinguishable from one
    /// made on the board.
    /// </remarks>
    public class TripAssignments
    {
        private readonly Supabase.Client _supabase;
        private readonly AuditLog _audit;
        private readonly SchedulingData _scheduling;

        public TripAssignments(Supabase.Client supabase, AuditLog audit, SchedulingData scheduling)
        {
            _supabase = supabase;
            _audit = audit;
            _scheduling = scheduling;
        }

        /// <summary>Moves a trip to another bus, driver or route.</summary>
        /// <param name="senderId">The signed-in dispatcher, named as the sender of any notice.</param>
        /// <param name="snapshot">
        /// What the change is measured against for the audit trail. Loaded for the trip's day
        /// when not given. A caller passes its own when the schedule it ranked against is not
        /// the one stored, such as leave about to be granted.
        /// </param>
        /// <param name="purpose">Appended to the audit summary, saying why the change was made.</param>
        /// <param name="syncStatuses">
        /// Whether to bring today's trip statuses up to date afterwards. A caller making
        /// several changes in a row does it once at the end instead.
        /// </param>
        public async Task<ReassignResult> ReassignAsync(
            ReassignChange change,
            int senderId,
            SchedulingSnapshot? snapshot = null,
            string? purpose = null,
            bool syncStatuses = true)
        {
            var tripResp = await _supabase.From<Trip>()
                .Filter("trip_id", Operator.Equals, change.TripId)
                .Get();
            var trip = tripResp.Models.FirstOrDefault();
            if (trip == null) return new(ReassignOutcome.NotFound, "Trip not found.");

            // A finished trip is history and a finished shift is history it never made. A
            // running one stays reassignable, which is most of what this is for: a driver
            // taken ill and a bus that has to come off the road both happen mid-shift.
            //
            // The board disables the button on the same two cases, and this is what makes
            // it true. The button is markup, and a page left open since the shift ended
            // still reaches this.
            if (string.Equals(trip.TripStatus, "Completed", StringComparison.OrdinalIgnoreCase))
                return new(ReassignOutcome.Refused, "That trip has finished and can no longer be reassigned.");

            if (TripStatus.Closed(trip, PhClock.Now))
                return new(ReassignOutcome.Refused, "That shift has finished, so the trip can no longer be reassigned.");

            // Captured before the update, because a reassignment is only meaningful
            // alongside what it moved away from.
            var wasVehicle = trip.VehicleId;
            var wasDriver = trip.DriverId;
            var wasRoute = trip.RouteId;

            // Where the chosen driver and bus sat among the suggestions for the trip as it
            // stood, recorded with the reassignment. Measured here rather than reported by
            // the page, and never allowed to stand in the way of the change it describes.
            ReassignmentTag? tag = null;
            try
            {
                snapshot ??= await _scheduling.LoadAsync(trip.Date, trip.Date);
                tag = SchedulingRules.TagReassignment(trip, change.DriverId, change.VehicleId, snapshot);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TripAssignments.Tag] {ex}");
            }

            // Only fields that were explicitly changed are written.
            if (!string.IsNullOrEmpty(change.VehicleId))
                trip.VehicleId = change.VehicleId;

            if (change.DriverId.HasValue && change.DriverId.Value > 0)
                trip.DriverId = change.DriverId.Value;

            if (change.RouteId.HasValue && change.RouteId.Value > 0)
                trip.RouteId = change.RouteId.Value;

            // The same overridable conflict gate the create path uses. The check excludes
            // the trip being edited, or an already double-booked trip could never be
            // saved: it would re-detect its own existing conflict and block even an
            // unrelated change. A conflict lets the dispatcher confirm and proceed.
            if (!change.Override)
            {
                var conflict = await ValidateAssignmentAsync(trip.Date, trip.ShiftType, trip.VehicleId, trip.DriverId, trip.TripId);
                if (conflict != null) return new(ReassignOutcome.Conflict, conflict);
            }

            // Filtered update rather than an upsert, which would insert a duplicate row.
            await _supabase.From<Trip>()
                .Filter("trip_id", Operator.Equals, change.TripId)
                .Set(t => t.VehicleId, trip.VehicleId)
                .Set(t => t.DriverId, trip.DriverId)
                .Set(t => t.RouteId, trip.RouteId)
                .Update();

            if (syncStatuses) await SyncTripStatusesAsync();

            // Told plainly, and marked urgent, because a driver who does not read this runs
            // the shift on the route they were given this morning. Best effort and after
            // the write: a notice that fails must not report the reassignment failed and
            // leave the dispatcher looking at a trip the database has already moved.
            if (wasRoute != trip.RouteId)
            {
                try { await NotifyRouteChangeAsync(trip, wasRoute, wasVehicle, senderId); }
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
                    + (change.Override ? ", overriding a scheduling conflict" : "")
                    + (string.IsNullOrWhiteSpace(purpose) ? "" : $", {purpose}"),
                "trips", trip.TripId,
                changes: tag?.ToAuditChanges());

            return new(ReassignOutcome.Done, Trip: trip);
        }

        /// <summary>Adds a trip by hand, after the same checks every booking goes through.</summary>
        /// <param name="purpose">Appended to the audit summary, saying why the trip was added.</param>
        /// <remarks>
        /// A shift that has finished cannot be booked into, and that is not overridable:
        /// confirming it would not put the shift back. A scheduling conflict can be confirmed
        /// past. The break takes the least-used slot among the route's buses already on that
        /// shift that day.
        ///
        /// The trip carries no roster month, so a re-publish of the roster treats it as made
        /// by hand and builds around it.
        /// </remarks>
        public async Task<ReassignResult> CreateAsync(NewTrip t, int senderId, string? purpose = null)
        {
            var day = t.Date.Date;

            if (TripStatus.Closed(day, t.Start, t.End, PhClock.Now))
                return new(ReassignOutcome.Refused, day == PhClock.OperationalDay
                    ? $"The {t.Shift} shift has already finished. Pick a shift that is still running."
                    : $"The {t.Shift} shift on {day:MMMM d} has already finished.");

            if (!t.Override)
            {
                var conflict = await ValidateAssignmentAsync(day, t.Shift, t.VehicleId, t.DriverId, null);
                if (conflict != null) return new(ReassignOutcome.Conflict, conflict);
            }

            var alongside = (await _supabase.From<Trip>()
                .Filter("date", Operator.Equals, day.ToString("yyyy-MM-dd"))
                .Filter("route_id", Operator.Equals, t.RouteId.ToString())
                .Filter("shift_type", Operator.Equals, t.Shift)
                .Get()).Models;

            var inserted = (await _supabase.From<Trip>().Insert(new Trip
            {
                Date = day,
                ShiftType = t.Shift,
                ShiftStartTime = t.Start,
                ShiftEndTime = t.End,
                BreakStart = BreakSlots.LeastUsed(t.Start, alongside.Select(x => x.BreakStart)),
                RouteId = t.RouteId,
                VehicleId = t.VehicleId,
                DriverId = t.DriverId,
                TripStatus = "Not Yet Started",
                EstimatedRevenue = 0
            })).Models.FirstOrDefault();

            if (day == PhClock.OperationalDay) await SyncTripStatusesAsync();

            // An override records that the dispatcher was warned about a clash and
            // proceeded, which is the part of the decision worth auditing.
            await _audit.WriteAsync("trip_created",
                $"created a {t.Shift} trip for bus {t.VehicleId} with driver {t.DriverId}"
                    + (day == PhClock.OperationalDay ? "" : $" on {day:MMM d}")
                    + (t.Override ? ", overriding a scheduling conflict" : "")
                    + (string.IsNullOrWhiteSpace(purpose) ? "" : $", {purpose}"),
                "trips", inserted?.TripId);

            return new(ReassignOutcome.Done, Trip: inserted);
        }

        /// <summary>
        /// Tells a driver they have been given a shift they did not have.
        /// </summary>
        /// <remarks>
        /// For a shift days away, which nobody on dispatch is going to ring about. Says when,
        /// where and on which bus, and nothing about whose shift it was: why another driver
        /// is off is theirs to share.
        /// </remarks>
        public async Task NotifyNewShiftAsync(Trip trip, int senderId)
        {
            var routes = (await _supabase.From<BusRoute>().Get()).Models;
            var route = routes.FirstOrDefault(r => r.RouteId == trip.RouteId)?.RouteName ?? $"Route {trip.RouteId}";
            var (start, end) = ShiftWindow(trip);

            await _supabase.From<Message>().Insert(new Message
            {
                SenderId = senderId,
                TargetAudience = "Driver",
                TargetId = trip.DriverId.ToString(),
                Subject = $"Shift assigned: {trip.Date:MMM d}",
                Body = $"You have been assigned the {trip.ShiftType} shift on {trip.Date:dddd, MMMM d}, "
                     + $"{start} to {end}, on {route} with bus {trip.VehicleId} (trip {trip.TripId}). "
                     + "Thank you.",
                Priority = "Normal",
                CreatedAt = PhClock.NowForDb,
            });
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
        private async Task NotifyRouteChangeAsync(Trip trip, int wasRoute, string wasVehicle, int senderId)
        {
            var routes = (await _supabase.From<BusRoute>().Get()).Models;
            string Name(int id) => routes.FirstOrDefault(r => r.RouteId == id)?.RouteName ?? $"Route {id}";

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

        /// <summary>
        /// Formats a trip's shift window against its own date. An end at or before the
        /// start means an overnight shift, whose end rolls to the next morning and is
        /// marked so it cannot be read as ending the morning it began.
        /// </summary>
        public static (string Start, string End) ShiftWindow(Trip t)
        {
            bool overnight = t.ShiftEndTime <= t.ShiftStartTime;
            var s = t.Date.Date.Add(t.ShiftStartTime);
            var e = t.Date.Date.Add(t.ShiftEndTime).AddDays(overnight ? 1 : 0);
            return (s.ToString("h:mm tt"), overnight ? $"{e:h:mm tt} (+1)" : e.ToString("h:mm tt"));
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
        public async Task<string> ValidateAssignmentAsync(
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
        public async Task<Dictionary<int, string>> WithLeaveAsync(
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

        /// <summary>Recomputes the stored status of a day's trips from buses, availability and leave.</summary>
        public async Task SyncTripStatusesAsync(string date = null)
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
            {
                // The flag speaks for the operational day it is read on, so a day that is
                // not that one is synced as though nobody had raised it.
                if (syncDay.Date != PhClock.OperationalDay)
                    availabilityDict = availabilityDict.ToDictionary(
                        kv => kv.Key,
                        kv => string.Equals(kv.Value, "Unavailable", StringComparison.OrdinalIgnoreCase)
                            ? "Available" : kv.Value);
                availabilityDict = await WithLeaveAsync(availabilityDict, syncDay);
            }

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

                // Only the status is written, so a change another writer made to the row
                // since it was read is not overwritten with the copy read here.
                if (trip.TripStatus != newStatus)
                {
                    await _supabase.From<Trip>()
                        .Filter("trip_id", Operator.Equals, trip.TripId)
                        .Set(t => t.TripStatus, newStatus)
                        .Update();
                }
            }
        }
    }
}
