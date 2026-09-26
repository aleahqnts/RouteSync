using System.Text.Json.Serialization;

namespace FleetWise.Models.ViewModels;

/// <summary>
/// The contract the Fleet Map consumes: one live bus, with its passenger count and estimated
/// revenue computed server-side so every consumer (markers, tooltip, side panel) shows
/// identical numbers.
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

    // Where to draw the bus: on its route line when the reading is near it, the reading
    // itself when it is off route or the route has no line.
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lng")]
    public double Lng { get; set; }

    // The reading as the phone reported it, and how accurate the phone said it was, for
    // comparing against where the bus is drawn. The raw position of a parked bus is its
    // terminal.
    [JsonPropertyName("rawLat")]
    public double RawLat { get; set; }

    [JsonPropertyName("rawLng")]
    public double RawLng { get; set; }

    [JsonPropertyName("accuracy")]
    public double? Accuracy { get; set; }

    [JsonPropertyName("onRoute")]
    public bool OnRoute { get; set; }

    [JsonPropertyName("offRoute")]
    public bool OffRoute { get; set; }

    // Metres along the route line, while the bus is drawn on it. The map moves the marker
    // along the road between two of these.
    [JsonPropertyName("along")]
    public double? Along { get; set; }

    // Direction to point the marker: the road's when on the route, the phone's course when
    // off it. Null when the bus is stopped.
    [JsonPropertyName("bearing")]
    public double? Bearing { get; set; }

    [JsonPropertyName("heading")]
    public double Heading { get; set; }

    [JsonPropertyName("speed")]
    public double Speed { get; set; }

    // Everyone who has boarded this trip. Nobody is counted off again, so this only ever
    // climbs. Taken from the trip's own figure where the counter phone has already raised
    // it, since telemetry carries the driver app's copy of the same number and trails it.
    [JsonPropertyName("passengers")]
    public int Passengers { get; set; }

    [JsonPropertyName("capacity")]
    public int Capacity { get; set; }

    [JsonPropertyName("estimatedRevenue")]
    public decimal EstimatedRevenue { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    // When the driver's break ends, as "10:00 AM", while the clock is inside it. Null
    // otherwise. The status stays On Trip: the bus is still on its trip and reporting.
    [JsonPropertyName("onBreakUntil")]
    public string OnBreakUntil { get; set; }
}
