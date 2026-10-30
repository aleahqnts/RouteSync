namespace FleetWise.Models
{
    public class VehicleListItemViewModel
    {
        public string VehicleId { get; set; } = string.Empty;

        public string PlateNumber { get; set; } = string.Empty;

        public string RouteName { get; set; } = string.Empty;

        // Display label: Ready to Deploy / On Trip / Pending / Flagged.
        public string Status { get; set; } = string.Empty;

        // Derived badge: No Issues / Needs Attention / Under Repair.
        public string Maintenance { get; set; } = string.Empty;
    }
}
