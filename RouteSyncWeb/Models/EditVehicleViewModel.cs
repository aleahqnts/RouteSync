using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace FleetWise.Models
{
    // Backs the Edit Vehicle modal: the editable Vehicle Profile (Plate Number, Route, Seats) only.
    // The maintenance/incident lifecycle lives entirely in the View modal (resolve / schedule /
    // out-of-service + audit notes) so there's a single source of truth. Carries its own
    // dropdown data so the partial is self-sufficient whether fetched or re-rendered on a failed POST.
    public class EditVehicleViewModel
    {
        // Read-only: the primary key cannot change.
        [Display(Name = "Vehicle ID")]
        public string VehicleId { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter a plate number."), StringLength(20, ErrorMessage = "Plate number can be up to 20 characters.")]
        [Display(Name = "Plate Number")]
        public string PlateNumber { get; set; } = string.Empty;

        [Required(ErrorMessage = "Select a route."), Range(1, int.MaxValue, ErrorMessage = "Select a route.")]
        [Display(Name = "Route")]
        public int RouteId { get; set; }

        // Seats decide which buses can stand in for which, the same rule as when the bus
        // was added.
        [Required(ErrorMessage = "Enter a seat capacity between 1 and 120."), Range(1, 120, ErrorMessage = "Enter a seat capacity between 1 and 120.")]
        [Display(Name = "Seat Capacity")]
        public int Capacity { get; set; }

        // Dropdown data.
        public List<SelectListItem> RouteOptions { get; set; } = new();

        // Whether this bus has left the fleet, which decides the action offered.
        public bool Retired { get; set; }

        public string? RetiredReason { get; set; }
    }
}
