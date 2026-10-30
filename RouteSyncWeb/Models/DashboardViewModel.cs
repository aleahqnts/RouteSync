using Microsoft.AspNetCore.Mvc.Rendering;

namespace FleetWise.Models
{
    public class DashboardViewModel
    {
        // Stat cards.
        public int ActiveTrips { get; set; }
        public int FlaggedVehicles { get; set; }
        public int TotalPassengers { get; set; }

        // Change against yesterday. Zero hides the indicator.
        public int PassengerDelta { get; set; }

        public decimal TotalRevenue { get; set; }

        // Change against yesterday in pesos. Zero hides the indicator.
        public decimal RevenueDelta { get; set; }

        // Passenger demand chart.
        // Hour labels along the horizontal axis.
        public List<string> ChartLabels { get; set; } = new();

        // Passengers boarded in each hour. A null entry is an hour still to come.
        public List<int?> ChartData { get; set; } = new();

        // The usual figure for each hour, averaged over the same weekday in the weeks before,
        // or null when those days had no boardings to average.
        public List<double>? ChartUsual { get; set; }

        // The hour the service day is in now, counted from its 06:00 start.
        public int ChartNowIndex { get; set; }

        // Today's date in Philippine time, for the header.
        public DateTime Today { get; set; }

        // Passenger figures per active trip, shown in the expandable panel.
        public List<ActiveTripRow> ActiveTripBreakdown { get; set; } = new();

        // Route dropdown.
        // The available routes, as identifier and name pairs.
        public List<SelectListItem> Routes { get; set; } = new();

        // Active filter state.
        // The selected route, or null for all routes.
        public int? SelectedRouteId { get; set; }

        // The active route filter's display name.
        public string SelectedRouteName => SelectedRouteId.HasValue
            ? Routes.FirstOrDefault(r => r.Value == SelectedRouteId.ToString())?.Text ?? "All Routes"
            : "All Routes";
    }

    // One active trip's passenger figures in the breakdown panel.
    public class ActiveTripRow
    {
        public string TripId { get; set; } = "";
        public string RouteName { get; set; } = "";
        public string VehicleId { get; set; } = "";
        public string ShiftType { get; set; } = "";
        public string Status { get; set; } = "";
        public int Passengers { get; set; }
    }
}
