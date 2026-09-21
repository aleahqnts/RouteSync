using System.Text.Json.Serialization;

namespace FleetWiseMobile.Models;

/// <summary>
/// What the shift screen needs to know about the bus before a trip can be started.
/// </summary>
/// <remarks>
/// Both facts are about somebody else's work, which a driver key cannot read: trips are
/// scoped to the driver who owns them and inspections to the trips they belong to. The
/// shift_start_context function answers these two questions and nothing else, so neither
/// policy has to be widened to serve a screen that only needs a yes or a no.
///
/// No driver is named. The screen has no use for one, and a lookup that returned staff
/// names would be a way to enumerate them.
/// </remarks>
public class ShiftStartContext
{
    /// <summary>The trip still open on this bus, or null when it is free.</summary>
    [JsonPropertyName("open_trip_id")]
    public string? OpenTripId { get; set; }

    /// <summary>When that open trip was rostered to end, as Philippine wall-clock.</summary>
    [JsonPropertyName("open_trip_ends_at")]
    public DateTime? OpenTripEndsAt { get; set; }

    /// <summary>Whether this bus already carries a cleared inspection today.</summary>
    [JsonPropertyName("bus_inspected_today")]
    public bool BusInspectedToday { get; set; }
}
