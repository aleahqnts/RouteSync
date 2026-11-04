using System.ComponentModel.DataAnnotations;

namespace FleetWise.Models
{
    // The add-vehicle form. The identifier is not on it: bus numbers run in one
    // sequence, so the registry assigns the next one rather than asking an
    // administrator to remember which are taken.
    public class AddVehicleViewModel
    {
        [Required(ErrorMessage = "Enter a plate number."), StringLength(20, ErrorMessage = "Plate number can be up to 20 characters.")]
        [Display(Name = "Plate Number")]
        public string PlateNumber { get; set; } = string.Empty;

        [Required(ErrorMessage = "Select a route."), Range(1, int.MaxValue, ErrorMessage = "Select a route.")]
        [Display(Name = "Route")]
        public int RouteId { get; set; }

        // Seats decide which buses can stand in for which, so a wrong figure here shows
        // up later as a replacement the dispatcher is told is too small. Opens at the
        // size most of the fleet is, which is the answer more often than any other.
        [Required(ErrorMessage = "Enter a seat capacity between 1 and 120."), Range(1, 120, ErrorMessage = "Enter a seat capacity between 1 and 120.")]
        [Display(Name = "Seat Capacity")]
        public int Capacity { get; set; } = 50;
    }
}
