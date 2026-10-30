using FleetWise.Services;

namespace FleetWise.Models.ViewModels;

// The GPS check page: every trip of one day, replayed against its route line.
public class GpsCheckViewModel
{
    // The trip date shown, as the day a trip starts on.
    public DateTime Date { get; set; }

    public List<GpsCheckRow> Trips { get; set; } = new();

    // All the day's readings, taken together.
    public GpsCheckTotals Totals { get; set; } = new();
}

// One trip's line on the GPS check page.
public class GpsCheckRow
{
    public string TripId { get; set; } = "";
    public string RouteName { get; set; } = "";
    public string VehicleId { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string Shift { get; set; } = "";
    public string Status { get; set; } = "";
    public GpsTripCheck Check { get; set; } = null!;
}

// The day's readings added up across every trip.
public class GpsCheckTotals
{
    public int Trips { get; set; }
    public int Readings { get; set; }
    public int OnRoad { get; set; }
    public int Held { get; set; }
    public int OffRoute { get; set; }
    public int Ignored { get; set; }
    public int WithAccuracy { get; set; }

    public double? OnRoadShare => Readings == 0 ? null : (double)OnRoad / Readings;
}
