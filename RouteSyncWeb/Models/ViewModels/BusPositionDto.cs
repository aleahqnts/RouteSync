using System.Text.Json.Serialization;

namespace FleetWise.Models.ViewModels;

/// <summary>
/// The contract the Fleet Map consumes: one live bus, with occupancy % and estimated revenue
/// computed server-side so every consumer (markers, tooltip, side panel) shows identical numbers.
/// </summary>
public class BusPositionDto
{
    [JsonPropertyName("tripId")]
    public string TripId { get; set; }

    [JsonPropertyName("vehicleId")]
    public string VehicleId { get; set; }

    [JsonPropertyName("plateNumber")]
    public string PlateNumber { get; set; }

    [JsonPropertyName("routeId")]
    public int RouteId { get; set; }

    [JsonPropertyName("routeName")]
    public string RouteName { get; set; }

    [JsonPropertyName("shift")]
    public string Shift { get; set; }

    [JsonPropertyName("driverName")]
    public string DriverName { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; }

    // Set only for parked buses: the terminal they are shown at.
    [JsonPropertyName("terminalName")]
    public string TerminalName { get; set; }

    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lng")]
    public double Lng { get; set; }

    [JsonPropertyName("heading")]
    public double Heading { get; set; }

    [JsonPropertyName("speed")]
    public double Speed { get; set; }

    [JsonPropertyName("passengers")]
    public int Passengers { get; set; }

    [JsonPropertyName("capacity")]
    public int Capacity { get; set; }

    [JsonPropertyName("occupancyPct")]
    public int OccupancyPct { get; set; }

    [JsonPropertyName("estimatedRevenue")]
    public decimal EstimatedRevenue { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    // When the driver's break ends, as "10:00 AM", while the clock is inside it. Null
    // otherwise. The status stays On Trip: the bus is still on its trip and reporting.
    [JsonPropertyName("onBreakUntil")]
    public string OnBreakUntil { get; set; }
}
