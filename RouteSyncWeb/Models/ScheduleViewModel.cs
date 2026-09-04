using System.ComponentModel.DataAnnotations;

namespace FleetWise.ViewModels
{
    /// <summary>The weekly planner grid.</summary>
    public class ScheduleViewModel
    {
        public DateTime WeekStart { get; set; }
        public DateTime WeekEnd { get; set; }
        public List<DateTime> Days { get; set; } = new();          // 7 dates
        public List<string> Shifts { get; set; } = new() { "Morning", "Afternoon", "Evening" };

        public List<RouteOption> Routes { get; set; } = new();
        public List<VehicleOption> Vehicles { get; set; } = new();
        public List<DriverOption> Drivers { get; set; } = new();

        // Keyed by route, shift and date, holding one or more trips, since several buses
        // can run the same route/shift/day).
        public Dictionary<string, List<ScheduleCell>> Cells { get; set; } = new();

        public string PrevWeekStart { get; set; }
        public string NextWeekStart { get; set; }

        /// <summary>
        /// Approved leave falling inside this week: the driver, then each day, then what
        /// kind of leave it is.
        /// </summary>
        /// <remarks>
        /// Spread out by day rather than left as a span, because the grid asks about one
        /// day at a time and a driver is off on some of a week's cells and not others.
        /// </remarks>
        public Dictionary<int, Dictionary<string, string>> LeaveDays { get; set; } = new();

        /// <summary>The operational day, when it falls inside the week on screen.</summary>
        /// <remarks>
        /// The driver availability flag has no date on it. It says a driver cannot work
        /// now, which is a statement about this day and about no other, so this is the only
        /// column the grid marks it on.
        /// </remarks>
        public string TodayInWeek { get; set; }

        /// <summary>Shift windows in this week that have already closed, as "Shift|date".</summary>
        /// <remarks>
        /// Keyed without the route, because a window belongs to the clock and not to a
        /// route. Held as its own set so an empty slot on a finished shift can be drawn
        /// read-only, which a cell carrying a trip could not answer for.
        /// </remarks>
        public HashSet<string> ClosedSlots { get; set; } = new();

        /// <summary>Whether every shift in the week has finished.</summary>
        /// <remarks>
        /// Such a week is a record rather than a plan, so the page withholds Save, Add bus
        /// and Copy last week rather than offering controls that can only be refused.
        /// </remarks>
        public bool WeekClosed { get; set; }

        /// <summary>What the week's save marker read when this grid was drawn.</summary>
        /// <remarks>
        /// Handed back with the save and compared against the marker as it stands then.
        /// Two dispatchers can hold the same week open, and a save rewrites the whole of
        /// it, so without this the second to save deletes the first one's work without
        /// either of them seeing it happen.
        ///
        /// Carried as the string it will be compared against rather than as a time.
        /// Nothing reads it: it either matches or the grid is out of date. Empty means the
        /// week had never been saved when it was drawn, which is a state worth telling
        /// apart from a week that had.
        /// </remarks>
        public string SavedAt { get; set; } = "";

        public bool SlotClosed(string shift, DateTime day) =>
            ClosedSlots.Contains($"{shift}|{day:yyyy-MM-dd}");
    }

    public class ScheduleCell
    {
        public string TripId { get; set; }
        public string VehicleId { get; set; }
        public int DriverId { get; set; }

        /// <summary>Whether the trip is beyond further change.</summary>
        /// <remarks>
        /// A trip that has started or finished, or one whose shift window has closed.
        /// Worked out where the clock is known rather than from the stored status, which
        /// says "Not Yet Started" for a shift that ended hours ago.
        /// </remarks>
        public bool Locked { get; set; }

        /// <summary>How the locked cell names its trip, derived the way the board does.</summary>
        public string StatusLabel { get; set; }
    }

    // Posted by POST /Schedule/Save
    public class SaveScheduleRequest
    {
        // [Required] on a list only checks it is present, and nothing walks into the items
        // unless the property itself carries a validator, so the cap below is also what
        // makes each cell get checked at all.
        [Required, MaxLength(500, ErrorMessage = "Too many cells in one save.")]
        public List<ScheduleCellInput> Cells { get; set; } = new();

        [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Week start must be yyyy-MM-dd.")]
        public string WeekStart { get; set; }

        [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Week end must be yyyy-MM-dd.")]
        public string WeekEnd { get; set; }

        /// <summary>The save marker the grid was loaded with, empty for a week that had none.</summary>
        [MaxLength(40, ErrorMessage = "That week could not be read.")]
        public string? SavedAt { get; set; }

        // Dispatcher acknowledged the conflict modal and chose to save anyway.
        public bool Override { get; set; }
    }

    public class ScheduleCellInput
    {
        // Empty means "new trip", so this one allows blank but still bounds the shape.
        //
        // Nullable on purpose. Under an enabled nullable context a plain string is treated
        // as required whether or not it is marked so, and a slot being filled for the first
        // time has no trip to name yet.
        [RegularExpression(@"^([A-Za-z0-9_-]{1,64})?$", ErrorMessage = "That is not a trip ID.")]
        public string? TripId { get; set; }    // null/empty = new trip to insert

        [Range(1, int.MaxValue, ErrorMessage = "A route is required.")]
        public int RouteId { get; set; }

        [Required, RegularExpression("^(Morning|Afternoon|Evening)$",
            ErrorMessage = "Shift must be Morning, Afternoon or Evening.")]
        public string Shift { get; set; }

        [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Date must be yyyy-MM-dd.")]
        public string Date { get; set; }       // yyyy-MM-dd

        // Blank clears the slot, and is nullable for the same reason as the trip.
        [RegularExpression(@"^([A-Za-z0-9-]{1,20})?$", ErrorMessage = "That is not a vehicle ID.")]
        public string? VehicleId { get; set; }

        // 0 clears the slot, so no lower bound of 1 here.
        [Range(0, int.MaxValue, ErrorMessage = "That is not a driver.")]
        public int DriverId { get; set; }
    }
}
