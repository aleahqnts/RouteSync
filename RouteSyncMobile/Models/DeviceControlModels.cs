using System.Text.Json.Serialization;

namespace FleetWiseMobile.Models;

// Remote camera control. Plain JSON objects rather than postgrest models, because these
// tables are read and written through the REST endpoint directly.
//
// device_config holds the desired state, written by the driver app or the dashboard and
// followed by the camera. device_status holds what the camera reports back.

public class DeviceConfigDto
{
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("line_ax")] public double? LineAx { get; set; }
    [JsonPropertyName("line_ay")] public double? LineAy { get; set; }
    [JsonPropertyName("line_bx")] public double? LineBx { get; set; }
    [JsonPropertyName("line_by")] public double? LineBy { get; set; }
    [JsonPropertyName("inward_sign")] public int InwardSign { get; set; } = 1;
    [JsonPropertyName("use_back_camera")] public bool UseBackCamera { get; set; }
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("updated_by")] public string? UpdatedBy { get; set; }
}

public class DeviceStatusDto
{
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    // The camera writes real UTC instants, unlike the local-time convention used
    // elsewhere, so the offset is preserved rather than discarded.
    [JsonPropertyName("last_seen")] public DateTimeOffset? LastSeen { get; set; }
    [JsonPropertyName("config_version_applied")] public int ConfigVersionApplied { get; set; } = -1;
    // Wake lifecycle, one of idle, capturing, preview or applied, with the time the
    // snapshot became available.
    [JsonPropertyName("wake_state")] public string? WakeState { get; set; }
    [JsonPropertyName("snapshot_ready_at")] public DateTimeOffset? SnapshotReadyAt { get; set; }
    // Counts the camera has made but cannot get the database to accept. Zero in
    // normal running, since a count is confirmed within seconds of a trip ending.
    [JsonPropertyName("unreconciled_counts")] public int UnreconciledCounts { get; set; }
}
