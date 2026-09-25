using System.ComponentModel.DataAnnotations;

namespace FleetWise.ViewModels
{
    /// <summary>The Roster page for one month.</summary>
    public class RosterViewModel
    {
        public DateTime Month { get; set; }
        public string MonthKey { get; set; } = "";
        public string PrevMonthKey { get; set; } = "";
        public string NextMonthKey { get; set; } = "";

        /// <summary>Not started, Draft or Published.</summary>
        public string Status { get; set; } = "";

        /// <summary>The version this page was built on, sent back with a save. Zero for a month never saved.</summary>
        public int Version { get; set; }

        /// <summary>A month already over is a record and cannot be changed.</summary>
        public bool ReadOnly { get; set; }

        /// <summary>"Saved Sep 15, 3:40 PM by Ana Reyes", or null for a month never saved.</summary>
        public string? SavedLine { get; set; }

        public List<RosterRouteView> Routes { get; set; } = new();
        public List<RosterDriverOption> Drivers { get; set; } = new();
        public List<RosterSeatInput> Seats { get; set; } = new();

        /// <summary>Drivers held back from this month's roster automation.</summary>
        public List<int> Held { get; set; } = new();

        /// <summary>Active buses with no home route, which no route's crew can be put on.</summary>
        public List<string> UnroutedBuses { get; set; } = new();

        /// <summary>"Published Sep 15, 3:40 PM", or null for a month never published.</summary>
        public string? PublishedLine { get; set; }

        /// <summary>Saved since it was last published, so the trips do not yet show the latest roster.</summary>
        public bool ChangedSincePublish { get; set; }

        /// <summary>The month before has a roster to carry forward.</summary>
        public bool HasPreviousRoster { get; set; }

        /// <summary>The signed-in role may add trips, which filling a gap does.</summary>
        public bool CanFillGaps { get; set; }

        /// <summary>Slots the last publish could not fill and nothing has filled since.</summary>
        public List<RosterSlotView> Gaps { get; set; } = new();

        /// <summary>Slots kept empty on purpose.</summary>
        public List<RosterSlotView> Skips { get; set; } = new();
    }

    /// <summary>One bus shift on one day, as the gaps and skips lists show it.</summary>
    public class RosterSlotView
    {
        public string Date { get; set; } = "";
        public string DateLabel { get; set; } = "";
        public string Shift { get; set; } = "";
        public string VehicleId { get; set; } = "";
        public string RouteName { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    public class RosterRouteView
    {
        public int RouteId { get; set; }
        public string RouteName { get; set; } = "";
        public List<RosterBusView> Buses { get; set; } = new();
    }

    public class RosterBusView
    {
        public string VehicleId { get; set; } = "";
        public string PlateNumber { get; set; } = "";

        /// <summary>Why a bus the roster still holds can no longer be crewed here, or null.</summary>
        public string? Warning { get; set; }
    }

    public class RosterDriverOption
    {
        public int DriverId { get; set; }
        public string Label { get; set; } = "";
        public bool Active { get; set; }
    }

    /// <summary>One place on the roster as the page holds it.</summary>
    public class RosterSeatInput
    {
        public int? DriverId { get; set; }

        [Required, RegularExpression("^(Crew|Floater)$", ErrorMessage = "A place on the roster is crew or floater.")]
        public string Kind { get; set; } = "";

        [Range(1, int.MaxValue, ErrorMessage = "That is not a route.")]
        public int RouteId { get; set; }

        [RegularExpression(@"^([A-Za-z0-9-]{1,20})?$", ErrorMessage = "That is not a vehicle ID.")]
        public string? VehicleId { get; set; }

        [Required, RegularExpression("^(Morning|Afternoon|Evening)$", ErrorMessage = "That is not a shift.")]
        public string Shift { get; set; } = "";

        [Range(1, 7, ErrorMessage = "A rest day is a day of the week.")]
        public int? RestWeekday { get; set; }

        /// <summary>Why auto-fill filled or emptied this place, kept until a person changes it.</summary>
        [MaxLength(600, ErrorMessage = "That note about an auto-filled place is too long.")]
        public string? Suggested { get; set; }
    }

    public class RosterCheckInput
    {
        /// <summary>
        /// The month on the page, so the check can say what publishing it now would leave
        /// empty. Without it only the pattern is judged.
        /// </summary>
        [RegularExpression(@"^\d{4}-(0[1-9]|1[0-2])$", ErrorMessage = "That is not a month.")]
        public string? Month { get; set; }

        public List<RosterSeatInput> Seats { get; set; } = new();

        /// <summary>Drivers held back: auto-fill and the suggestions pass them over.</summary>
        public List<int> Held { get; set; } = new();
    }

    public class RosterSaveInput
    {
        [Required, RegularExpression(@"^\d{4}-(0[1-9]|1[0-2])$", ErrorMessage = "That is not a month.")]
        public string Month { get; set; } = "";

        [Range(0, int.MaxValue)]
        public int Version { get; set; }

        public List<RosterSeatInput> Seats { get; set; } = new();

        /// <summary>Drivers held back, saved with the roster. A driver placed on it is no longer held.</summary>
        public List<int> Held { get; set; } = new();
    }
}

namespace FleetWise.ViewModels
{
    /// <summary>A month and the roster version the page was built on.</summary>
    public class RosterMonthInput
    {
        [Required, RegularExpression(@"^\d{4}-(0[1-9]|1[0-2])$", ErrorMessage = "That is not a month.")]
        public string Month { get; set; } = "";

        [Range(0, int.MaxValue)]
        public int Version { get; set; }
    }

    /// <summary>One bus shift on one day.</summary>
    public class RosterSlotInput
    {
        [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "That is not a date.")]
        public string Date { get; set; } = "";

        [Required, RegularExpression(@"^[A-Za-z0-9-]{1,20}$", ErrorMessage = "That is not a vehicle ID.")]
        public string VehicleId { get; set; } = "";

        [Required, RegularExpression("^(Morning|Afternoon|Evening)$", ErrorMessage = "That is not a shift.")]
        public string Shift { get; set; } = "";
    }

    /// <summary>A driver chosen to fill a gap.</summary>
    public class RosterFillInput : RosterSlotInput
    {
        [Range(1, int.MaxValue, ErrorMessage = "That is not a driver.")]
        public int DriverId { get; set; }

        /// <summary>The dispatcher was shown a scheduling conflict and chose to book anyway.</summary>
        public bool Override { get; set; }
    }
}
