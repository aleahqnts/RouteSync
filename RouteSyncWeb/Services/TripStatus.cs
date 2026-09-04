using FleetWise.Models;

namespace FleetWise.Services
{
    /// <summary>How a trip and its assignment read right now.</summary>
    public sealed record TripStatusView(
        string VehicleStatus,
        string DriverStatus,
        string TripStatus,
        bool VehicleFlagged);

    /// <summary>
    /// The single place the dispatch board, the trip detail modal and the header
    /// counters work out what a trip looks like.
    /// </summary>
    /// <remarks>
    /// Derived per request. vehicle_status belongs to whichever shift wrote it last, so a
    /// stored value would show a missed trip as ready.
    ///
    /// Flagged means an unresolved maintenance incident, nothing else. Reading it from the
    /// checklist row would leave a bus flagged for good, since nothing on the dashboard
    /// rewrites an inspection.
    ///
    /// A flag is advisory. Only a grounded bus or an unavailable driver blocks assignment.
    /// </remarks>
    public static class TripStatus
    {
        /// <summary>The fixed windows a shift can be booked into.</summary>
        /// <remarks>
        /// One definition, because the planner, the dispatch board and the add trip modal
        /// all decide from it whether a shift is still open, and three copies of the same
        /// three times drift apart quietly.
        ///
        /// Evening ends before it starts, which is what marks it as running past midnight.
        /// </remarks>
        public static readonly IReadOnlyDictionary<string, (TimeSpan Start, TimeSpan End)> Windows =
            new Dictionary<string, (TimeSpan Start, TimeSpan End)>(StringComparer.OrdinalIgnoreCase)
            {
                ["Morning"] = (new(6, 0, 0), new(14, 0, 0)),
                ["Afternoon"] = (new(14, 0, 0), new(22, 0, 0)),
                ["Evening"] = (new(22, 0, 0), new(6, 0, 0)),
            };

        /// <summary>
        /// When the shift closes. A window whose end is not after its start runs past
        /// midnight and therefore closes on the following day.
        /// </summary>
        public static DateTime ShiftEndAt(Trip trip) =>
            EndOf(trip.Date, trip.ShiftStartTime, trip.ShiftEndTime);

        /// <summary>When a shift window closes, for a slot that holds no trip yet.</summary>
        public static DateTime EndOf(DateTime date, TimeSpan start, TimeSpan end) =>
            date.Date.Add(end).AddDays(end <= start ? 1 : 0);

        /// <summary>
        /// Whether a shift window has closed, and with it every action that could still
        /// have changed what ran.
        /// </summary>
        /// <remarks>
        /// The single test behind a closed window: the planner will not create, edit or
        /// delete across it, dispatch will not create or reassign across it, and an
        /// undecided leave request is not held up by a shift on the far side of it.
        ///
        /// Written against the clock rather than a stored column because "Missed" is
        /// derived and never written down. A guard reading trip_status sees a shift that
        /// closed hours ago as "Not Yet Started" and lets it through.
        ///
        /// A leave approval depends on this too. A closed shift that still blocked would
        /// demand the schedule be cleared first, which this same rule forbids, and the
        /// request could never be answered either way.
        /// </remarks>
        public static bool Closed(DateTime date, TimeSpan start, TimeSpan end, DateTime now) =>
            EndOf(date, start, end) < now;

        /// <inheritdoc cref="Closed(DateTime, TimeSpan, TimeSpan, DateTime)"/>
        public static bool Closed(Trip trip, DateTime now) =>
            ShiftEndAt(trip) < now;

        /// <summary>
        /// Whether a trip is beyond further change: it ran, is running, or its window has
        /// closed.
        /// </summary>
        /// <remarks>
        /// What the planner and the reassign path both mean by locked. A started trip is
        /// history in progress and a closed one is history, and neither is the schedule's
        /// to rewrite.
        /// </remarks>
        public static bool Locked(Trip trip, DateTime now) =>
            string.Equals(trip.TripStatus, "Active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trip.TripStatus, "Completed", StringComparison.OrdinalIgnoreCase)
            || Closed(trip, now);

        /// <param name="checklist">
        /// This trip's most recent inspection, or null when none was submitted.
        /// </param>
        /// <param name="vehicleFlagged">
        /// Whether the vehicle has an unresolved maintenance incident.
        /// </param>
        public static TripStatusView Resolve(
            Trip trip,
            Vehicle? vehicle,
            UserModel? driver,
            string? driverAvailability,
            BusChecklist? checklist,
            bool vehicleFlagged,
            DateTime now)
        {
            // An inspection recorded against a different bus belongs to a reassignment
            // that has already happened, so it says nothing about the bus on the trip
            // now. That bus needs its own inspection.
            if (checklist != null &&
                !string.Equals(checklist.VehicleId, trip.VehicleId, StringComparison.OrdinalIgnoreCase))
            {
                checklist = null;
            }

            // A running trip whose driver has reported they cannot drive. The trip is
            // still active and the bus is still out, so neither of those changes. What
            // changes is that the board can now say the driver needs relieving, which it
            // could not while every active trip reported its driver as on trip.
            if (trip.TripStatus == "Active")
                return new TripStatusView(
                    "On Trip",
                    string.Equals(driverAvailability, "Unavailable", StringComparison.OrdinalIgnoreCase)
                        ? "Unavailable"
                        : "On Trip",
                    "Active",
                    vehicleFlagged);

            if (trip.TripStatus == "Completed")
                return new TripStatusView("Completed", "Available", "Completed", vehicleFlagged);

            // Waiting to depart. Readiness comes from this trip's own inspection: none
            // yet is pending, and an open incident against the bus is a flag.
            var vehicleStatus = checklist == null ? "Pending"
                              : vehicleFlagged ? "Flagged"
                              : "Ready to Deploy";

            // A driver with no availability row counts as available.
            var driverStatus = driver == null ? "Unavailable"
                             : string.IsNullOrEmpty(driverAvailability) ? "Available"
                             : driverAvailability;

            // A shift that closed without the trip ever starting counts as missed, so a
            // past operational day shows missed trips rather than a stale "not yet
            // started".
            var tripStatus =
                ShiftEndAt(trip) < now ? "Missed"
                : (vehicle?.OutOfService == true
                   || driverStatus == "Unavailable"
                   // Approved leave for this day. A driver who is rostered off cannot run
                   // the trip, and the board has to say so while there is still time to
                   // put somebody else on it.
                   || driverStatus == "On Leave") ? "Assignment Issue"
                : vehicleStatus == "Pending" ? "Pending"
                : "Not Yet Started";

            return new TripStatusView(vehicleStatus, driverStatus, tripStatus, vehicleFlagged);
        }
    }
}
