using Microsoft.AspNetCore.Mvc.Rendering;

namespace FleetWise.Models
{
    public class VehiclesIndexViewModel
    {
        // Empty on the first render. The rows load separately through their own
        // partial view.
        public List<VehicleListItemViewModel> Rows { get; set; } = new();

        // Summary cards (computed over ALL vehicles, unaffected by the table filters).
        // Buses still in the fleet. Retired ones are counted separately.
        public int ActiveVehicles { get; set; }
        public int RetiredVehicles { get; set; }
        public int FlaggedVehicles { get; set; }
        public int ScheduledMaintenance { get; set; }

        // Dropdown option lists.
        public List<SelectListItem> RouteOptions { get; set; } = new();

        // The identifier the next added bus will receive.
        public string NextVehicleId { get; set; } = "";
        public List<string> StatusOptions { get; set; } = new();
        public List<string> ConditionOptions { get; set; } = new();

        // Selected filter state (echoed back so dropdowns/search keep their value).
        public string? SelectedRoute { get; set; }
        public string? SelectedStatus { get; set; }
        public string? SelectedCondition { get; set; }
        public string? SearchTerm { get; set; }
    }
}
