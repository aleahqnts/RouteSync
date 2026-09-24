using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FleetWise.Services;

/// <summary>A burst of unusual activity, as stored.</summary>
public sealed record SecurityIncident(
    long IncidentId,
    string Rule,
    string Severity,
    string KeyColumn,
    string KeyValue,
    IReadOnlyList<string> Actions,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int EventCount,
    DateTimeOffset DetectedAt,
    DateTimeOffset? ReviewedAt,
    string? ReviewedBy,
    string? ReviewNote,
    int? ReviewedEventCount,
    bool NeedsReview)
{
    /// <summary>Whether the incident concerns this account rather than an address.</summary>
    public bool IsAbout(string? userId) =>
        userId is not null
        && KeyColumn is "target_id" or "actor_id"
        && KeyValue == userId;
}

/// <summary>
/// Reads and writes security incidents and how far the detector has got.
/// </summary>
/// <remarks>
/// Talks to the database directly, as the audit trail does, and reads every time as an
/// instant with its offset. The spans stored here are compared against audit entries to
/// the microsecond, and a time that silently took on the host's zone would move an
/// incident's filter by eight hours on one machine and not on another.
///
/// Every read answers null when it could not reach the table, rather than an empty list.
/// An empty list claims there is nothing unusual, and that claim is the one thing this
/// feature must never make falsely.
/// </remarks>
public sealed class SecurityIncidents
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly IConfiguration _config;

    public SecurityIncidents(IConfiguration config) => _config = config;

    /// <summary>A time as the database's filters and columns take it.</summary>
    public static string Stamp(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    public async Task<DateTimeOffset?> ScannedThroughAsync()
    {
        var rows = await GetAsync("security_detector_state?select=scanned_through&id=eq.1");
        if (rows is null || rows.Count == 0) return null;
        return Time(rows[0], "scanned_through");
    }

    public async Task<bool> SetScannedThroughAsync(DateTimeOffset through) =>
        await SendAsync(HttpMethod.Patch, "security_detector_state?id=eq.1",
            new Dictionary<string, object?> { ["scanned_through"] = Stamp(through) });

    /// <summary>Incidents whose activity reaches back to or past a point.</summary>
    public async Task<List<SecurityIncident>?> TouchingAsync(DateTimeOffset since)
    {
        var rows = await GetAsync(
            $"security_incidents?select=*&last_seen_at=gte.{Stamp(since)}&order=last_seen_at.asc");
        return rows?.Select(Map).ToList();
    }

    /// <summary>
    /// Raises an incident. A burst already raised is left alone rather than duplicated.
    /// </summary>
    /// <returns>
    /// True when a row was created. A burst that was already on record answers false
    /// rather than true, since both succeed at the database and only one of them raised
    /// anything.
    /// </returns>
    public async Task<bool> InsertAsync(SecurityRule rule, string key, Episode episode, int count)
    {
        try
        {
            var req = Request(HttpMethod.Post,
                "security_incidents?on_conflict=rule,key_value,first_seen_at&select=incident_id");
            // A duplicate is dropped by the database and comes back as an empty list, which
            // is how it is told apart from a row that was created.
            req.Headers.TryAddWithoutValidation("Prefer", "resolution=ignore-duplicates,return=representation");
            req.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["rule"] = rule.Code,
                ["severity"] = rule.Severity,
                ["key_column"] = rule.KeyColumn,
                ["key_value"] = key,
                ["actions"] = rule.Actions,
                ["first_seen_at"] = Stamp(episode.Start),
                ["last_seen_at"] = Stamp(episode.End),
                ["event_count"] = count,
            }), Encoding.UTF8, "application/json");

            var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The audit trail query that returns exactly this incident's entries.
    /// </summary>
    /// <remarks>
    /// The one definition of which entries belong to an incident. The detector counts with
    /// it and the incident panel lists with it, so the number on the row and the entries
    /// behind it cannot disagree.
    ///
    /// An exact match on the key, never a search. The audit page's search matches part of
    /// a value, so an address search for 1.2.3.4 also finds 11.2.3.45, and an incident
    /// built on that would show other people's activity as part of an attack.
    ///
    /// Everything interpolated here came from the incident's own row, which the table's
    /// constraints limit, and is checked again rather than trusted.
    /// </remarks>
    /// <param name="through">A later end than the stored one, while an extension is being counted.</param>
    public static string? FilterFor(SecurityIncident incident, DateTimeOffset? through = null)
    {
        if (incident.KeyColumn is not ("ip" or "target_id" or "actor_id")) return null;
        if (incident.Actions.Count == 0 || incident.Actions.Any(a => !IsActionName(a))) return null;

        return $"action=in.({string.Join(",", incident.Actions)})"
             + $"&{incident.KeyColumn}=eq.{Uri.EscapeDataString(incident.KeyValue)}"
             + $"&occurred_at=gte.{Stamp(incident.FirstSeenAt)}"
             + $"&occurred_at=lte.{Stamp(through ?? incident.LastSeenAt)}";
    }

    /// <summary>
    /// Whether a name is shaped like an audit action, so it cannot reshape the list it
    /// is placed in.
    /// </summary>
    private static bool IsActionName(string action) =>
        action.Length is > 0 and <= 64 && action.All(c => c is (>= 'a' and <= 'z') or '_');

    public async Task<bool> UpdateSpanAsync(long incidentId, DateTimeOffset lastSeen, int count) =>
        await SendAsync(HttpMethod.Patch, $"security_incidents?incident_id=eq.{incidentId}",
            new Dictionary<string, object?>
            {
                ["last_seen_at"] = Stamp(lastSeen),
                ["event_count"] = count,
            });

    /// <summary>What needs review first, then everything else, newest activity first.</summary>
    public async Task<List<SecurityIncident>?> ListAsync(int limit)
    {
        var rows = await GetAsync(
            $"security_incidents?select=*&order=needs_review.desc,last_seen_at.desc&limit={limit}");
        return rows?.Select(Map).ToList();
    }

    public async Task<SecurityIncident?> FindAsync(long incidentId)
    {
        var rows = await GetAsync($"security_incidents?select=*&incident_id=eq.{incidentId}");
        return rows is { Count: > 0 } ? Map(rows[0]) : null;
    }

    /// <summary>
    /// Marks an incident reviewed as of what the reviewer was shown.
    /// </summary>
    /// <param name="seenCount">
    /// How many entries were on the reviewer's screen. Anything gathered after they looked
    /// raises the count past this and returns the incident to needing review.
    /// </param>
    public async Task<bool> ReviewAsync(long incidentId, string reviewer, string? note, int seenCount) =>
        await SendAsync(HttpMethod.Patch, $"security_incidents?incident_id=eq.{incidentId}",
            new Dictionary<string, object?>
            {
                ["reviewed_at"] = Stamp(DateTimeOffset.UtcNow),
                ["reviewed_by"] = reviewer,
                ["review_note"] = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                ["reviewed_event_count"] = seenCount,
            });

    /// <summary>
    /// How many incidents need review, and whether any of them is a possible break-in.
    /// </summary>
    public async Task<(int Count, bool Urgent)?> NeedsReviewAsync()
    {
        var rows = await GetAsync("security_incidents?select=severity&needs_review=is.true&limit=1000");
        if (rows is null) return null;
        return (rows.Count, rows.Any(r => Str(r, "severity") == "high"));
    }

    private HttpRequestMessage Request(HttpMethod method, string pathAndQuery)
    {
        var url = _config["Supabase:Url"];
        var key = _config["Supabase:Key"];
        var req = new HttpRequestMessage(method, $"{url}/rest/v1/{pathAndQuery}");
        req.Headers.TryAddWithoutValidation("apikey", key);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        return req;
    }

    private async Task<List<JsonElement>?> GetAsync(string pathAndQuery)
    {
        try
        {
            var res = await _http.SendAsync(Request(HttpMethod.Get, pathAndQuery));
            if (!res.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> SendAsync(
        HttpMethod method, string pathAndQuery, Dictionary<string, object?> body,
        string prefer = "return=minimal")
    {
        try
        {
            var req = Request(method, pathAndQuery);
            req.Headers.TryAddWithoutValidation("Prefer", prefer);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var res = await _http.SendAsync(req);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static SecurityIncident Map(JsonElement e) => new(
        e.GetProperty("incident_id").GetInt64(),
        Str(e, "rule") ?? "",
        Str(e, "severity") ?? "medium",
        Str(e, "key_column") ?? "",
        Str(e, "key_value") ?? "",
        e.TryGetProperty("actions", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : new List<string>(),
        Time(e, "first_seen_at") ?? DateTimeOffset.MinValue,
        Time(e, "last_seen_at") ?? DateTimeOffset.MinValue,
        e.TryGetProperty("event_count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0,
        Time(e, "detected_at") ?? DateTimeOffset.MinValue,
        Time(e, "reviewed_at"),
        Str(e, "reviewed_by"),
        Str(e, "review_note"),
        e.TryGetProperty("reviewed_event_count", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : null,
        e.TryGetProperty("needs_review", out var n) && n.ValueKind == JsonValueKind.True);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        Str(e, name) is { } s
        && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;
}
