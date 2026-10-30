using System.Text.Json.Serialization;

namespace FleetWise.Models;

// One trip's boardings within one hour, as boardings_by_hour counts them.
//
// Counted from the counter phone's crossings, so passengers a driver added by hand are not
// here: trips.total_boarded stays the reported figure.
// TripId: The trip.
// HourStart: When the hour starts, as an instant.
// Boarded: Passengers the counter saw board in that hour.
public sealed record BoardedHour(
    [property: JsonPropertyName("trip_id")] string TripId,
    [property: JsonPropertyName("hour_start")] DateTimeOffset HourStart,
    [property: JsonPropertyName("boarded")] int Boarded);
