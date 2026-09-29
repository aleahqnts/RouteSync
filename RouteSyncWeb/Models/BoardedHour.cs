using System.Text.Json.Serialization;

namespace FleetWise.Models;

/// <summary>One trip's boardings within one hour, as boardings_by_hour counts them.</summary>
/// <remarks>
/// Counted from the counter phone's crossings, so passengers a driver added by hand are not
/// here: trips.total_boarded stays the reported figure.
/// </remarks>
/// <param name="TripId">The trip.</param>
/// <param name="HourStart">When the hour starts, as an instant.</param>
/// <param name="Boarded">Passengers the counter saw board in that hour.</param>
public sealed record BoardedHour(
    [property: JsonPropertyName("trip_id")] string TripId,
    [property: JsonPropertyName("hour_start")] DateTimeOffset HourStart,
    [property: JsonPropertyName("boarded")] int Boarded);
