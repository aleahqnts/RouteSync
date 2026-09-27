using FleetWise.Services;

namespace FleetWise.Models.ViewModels;

/// <summary>The GPS check page: every trip of one day, replayed against its route line.</summary>
public class GpsCheckViewModel
{
    /// <summary>The trip date shown, as the day a trip starts on.</summary>
    public DateTime Date { get; set; }

    public List<GpsCheckRow> Trips { get; set; } = new();

    /// <summary>All the day's readings, taken together.</summary>
    public GpsCheckTotals Totals { get; set; } = new();
}

/// <summary>One trip's line on the GPS check page.</summary>
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

/// <summary>The day's readings added up across every trip.</summary>
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
