using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FleetWise.Models;

namespace FleetWise.Services
{
    /// <summary>One recorded reassignment, as the suggestions saw it.</summary>
    /// <param name="TookTopSuggestion">Every side that changed went to the top-ranked candidate.</param>
    /// <param name="FixedIssue">The trip could not run as assigned before the change.</param>
    /// <param name="Screen">Where the pick was made, one of <see cref="PickScreen"/>, or null for a pick recorded before screens were.</param>
    public sealed record ReassignmentPick(bool TookTopSuggestion, bool FixedIssue, string? Screen = null);

    /// <summary>
    /// Records administrator actions in the audit trail.
    /// </summary>
    /// <remarks>
    /// The dashboard reaches the database through one shared service key, so every write
    /// arrives at the database triggers as the same anonymous role. Which administrator
    /// acted is known only here, from the authentication cookie. This service supplies
    /// that half: one entry per action, with the actor taken from the signed-in principal
    /// rather than the request body, which a client could forge.
    ///
    /// The two records are complementary. The trigger's entry says what changed, as a
    /// before and after difference. This one says who asked for it and from which address.
    ///
    /// Recording must never break the action being recorded. Every failure is swallowed
    /// and each write is time-boxed, so an unreachable audit table cannot slow anything
    /// down or lock anyone out.
    /// </remarks>
    public class AuditLog
    {
        private static readonly HttpClient _http = new();
        private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(3);

        private readonly IConfiguration _config;
        private readonly IHttpContextAccessor _accessor;

        public AuditLog(IConfiguration config, IHttpContextAccessor accessor)
        {
            _config = config;
            _accessor = accessor;
        }

        /// <summary>The signed-in operator's display name, used in entry summaries.</summary>
        public string ActorName =>
            _accessor.HttpContext?.User?.FindFirst(ClaimTypes.Name)?.Value ?? "An admin";

        /// <summary>Appends one entry to the trail.</summary>
        /// <param name="phrase">
        /// The predicate only. The operator's name is prepended here, so every entry reads
        /// the same way: "<c>{name} reset the password for Juan Dela Cruz</c>". Never pass
        /// a password, a hash, or a key.
        /// </param>
        public async Task WriteAsync(
            string action,
            string phrase,
            string? targetTable = null,
            object? targetId = null,
            string outcome = "ok",
            object? changes = null)
        {
            var user = _accessor.HttpContext?.User;

            await PostAsync(new Dictionary<string, object?>
            {
                ["actor_type"] = "admin",
                ["actor_id"] = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                // The dashboard role, such as Admin or Dispatcher, not a database role.
                // The source column already says which vocabulary this one is using.
                ["actor_role"] = user?.FindFirst(ClaimTypes.Role)?.Value,
                ["action"] = action,
                ["target_table"] = targetTable,
                ["target_id"] = targetId?.ToString(),
                ["outcome"] = outcome,
                ["summary"] = $"{ActorName} {phrase}".Trim(),
                ["changes"] = changes,
            });
        }

        /// <summary>Appends an entry for a dashboard sign-in attempt.</summary>
        /// <remarks>
        /// Separate from <see cref="WriteAsync"/> because there may be no principal to read
        /// at this point. A failed attempt has no identity at all, and a successful one is
        /// signed in on the response rather than the current request. The caller therefore
        /// passes what authentication has just established, and nothing more.
        ///
        /// Never pass the password, whether it was correct or not.
        /// </remarks>
        public async Task WriteSignInAsync(
            string action, string summary, int? userId, string outcome = "ok", string? role = null)
        {
            await PostAsync(new Dictionary<string, object?>
            {
                ["actor_type"] = userId is null ? "anon" : "admin",
                ["actor_id"] = userId?.ToString(),
                // Passed in rather than read from claims, because the principal does not
                // exist on this request yet. Without it a sign-in entry cannot distinguish
                // an administrator from a dispatcher, since the actor type says only that
                // it was a dashboard operator.
                ["actor_role"] = role,
                ["action"] = action,
                ["target_table"] = "users",
                ["target_id"] = userId?.ToString(),
                ["outcome"] = outcome,
                ["summary"] = summary,
            });
        }

        /// <summary>Reads the trail back for the audit log page.</summary>
        /// <param name="query">A PostgREST query string built by the controller.</param>
        /// <returns>
        /// The matching entries and the unpaged total. The entries are null when the read
        /// itself failed, so the page can report that rather than showing an empty trail,
        /// which would suggest nothing had ever happened.
        /// </returns>
        public async Task<(List<AuditEntryViewModel>? Rows, int Total)> QueryAsync(string query)
        {
            try
            {
                var url = _config["Supabase:Url"];
                var key = _config["Supabase:Key"];
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)) return (null, 0);

                var req = new HttpRequestMessage(HttpMethod.Get, $"{url}/rest/v1/audit_log?{query}");
                req.Headers.TryAddWithoutValidation("apikey", key);
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
                // Requests the unpaged total, which comes back in the Content-Range header
                // as a range and count. Without it the pager cannot be sized.
                req.Headers.TryAddWithoutValidation("Prefer", "count=exact");

                var res = await _http.SendAsync(req);
                if (!res.IsSuccessStatusCode) return (null, 0);

                var total = ParseTotal(res.Content.Headers.TryGetValues("Content-Range", out var cr)
                    ? cr.FirstOrDefault()
                    : null);

                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var rows = doc.RootElement.EnumerateArray().Select(Map).ToList();
                return (rows, total == -1 ? rows.Count : total);
            }
            catch
            {
                return (null, 0);
            }
        }

        /// <summary>
        /// What each pick from a ranking in a span chose, measured against the suggestions
        /// offered at the time.
        /// </summary>
        /// <remarks>
        /// Reassignments, and trips made by hand into a roster gap, which carry the same
        /// record. A trip made by hand anywhere else carries none and is not counted.
        /// </remarks>
        /// <returns>
        /// One entry per pick, or null when the read failed, so a figure that could not be
        /// read is not shown as a month with none.
        /// </returns>
        public async Task<List<ReassignmentPick>?> ReassignmentPicksAsync(DateTimeOffset from, DateTimeOffset to)
        {
            try
            {
                var url = _config["Supabase:Url"];
                var key = _config["Supabase:Key"];
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)) return null;

                var query = "select=changes&action=in.(trip_reassigned,trip_created)"
                    + $"&occurred_at=gte.{Uri.EscapeDataString(from.ToString("o"))}"
                    + $"&occurred_at=lt.{Uri.EscapeDataString(to.ToString("o"))}"
                    + "&limit=10000";

                var req = new HttpRequestMessage(HttpMethod.Get, $"{url}/rest/v1/audit_log?{query}");
                req.Headers.TryAddWithoutValidation("apikey", key);
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");

                var res = await _http.SendAsync(req);
                if (!res.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var picks = new List<ReassignmentPick>();

                foreach (var row in doc.RootElement.EnumerateArray())
                {
                    // Entries written before suggestions existed, and route-only moves,
                    // carry no recommendation and say nothing about one.
                    if (!row.TryGetProperty("changes", out var changes)
                        || changes.ValueKind != JsonValueKind.Object
                        || !changes.TryGetProperty("recommendation", out var rec)
                        || rec.ValueKind != JsonValueKind.Object)
                        continue;

                    var tookTop = rec.TryGetProperty("via", out var via)
                                  && via.ValueKind == JsonValueKind.String
                                  && via.GetString() == "suggestion";
                    var fixedIssue = rec.TryGetProperty("issues", out var issues)
                                     && issues.ValueKind == JsonValueKind.Array
                                     && issues.GetArrayLength() > 0;

                    var screen = rec.TryGetProperty("screen", out var s)
                                 && s.ValueKind == JsonValueKind.String
                                 && PickScreen.IsKnown(s.GetString())
                        ? s.GetString()
                        : null;

                    picks.Add(new ReassignmentPick(tookTop, fixedIssue, screen));
                }

                return picks;
            }
            catch
            {
                return null;
            }
        }

        private static AuditEntryViewModel Map(JsonElement e)
        {
            static string? Str(JsonElement el, string name) =>
                el.TryGetProperty(name, out var v) && v.ValueKind is not JsonValueKind.Null
                    ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
                    : null;

            return new AuditEntryViewModel
            {
                Id = e.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
                OccurredAt = e.TryGetProperty("occurred_at", out var at)
                             && DateTimeOffset.TryParse(at.GetString(), out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue,
                ActorType = Str(e, "actor_type") ?? "",
                ActorId = Str(e, "actor_id"),
                ActorRole = Str(e, "actor_role"),
                Action = Str(e, "action") ?? "",
                TargetTable = Str(e, "target_table"),
                TargetId = Str(e, "target_id"),
                Source = Str(e, "source") ?? "",
                Outcome = Str(e, "outcome") ?? "",
                Summary = Str(e, "summary"),
                Ip = Str(e, "ip"),
                // The stored record is read into fields and never carried further. It
                // reads as machine output, and the column names in it are of no use to
                // anyone reading the trail.
                // Only a row edit counts. A reassignment's note on how it compared with the
                // suggestions sits in the same column and is not a change to any column.
                HasChanges = e.TryGetProperty("changes", out var ch)
                             && (ch.ValueKind == JsonValueKind.Array
                                 || (ch.ValueKind == JsonValueKind.Object
                                     && (ch.TryGetProperty("old", out _) || ch.TryGetProperty("new", out _)))),
                FieldChanges = ch.ValueKind == JsonValueKind.Object ? FieldChangesOf(ch) : new(),
            };
        }


        /// <summary>
        /// Reads a row edit as the columns that actually differ.
        /// </summary>
        /// <remarks>
        /// The trigger stores the whole row twice, as `old` and `new`. Most columns are
        /// identical between them and say nothing, so only the differences are kept, and
        /// each value is written the way it reads on the rest of the dashboard.
        /// </remarks>
        private static List<AuditFieldChange> FieldChangesOf(JsonElement changes)
        {
            var result = new List<AuditFieldChange>();

            var hasOld = changes.TryGetProperty("old", out var oldRow)
                         && oldRow.ValueKind == JsonValueKind.Object;
            var hasNew = changes.TryGetProperty("new", out var newRow)
                         && newRow.ValueKind == JsonValueKind.Object;
            if (!hasOld && !hasNew) return result;

            var fields = new List<string>();
            if (hasNew) foreach (var p in newRow.EnumerateObject()) fields.Add(p.Name);
            if (hasOld)
                foreach (var p in oldRow.EnumerateObject())
                    if (!fields.Contains(p.Name)) fields.Add(p.Name);

            foreach (var field in fields)
            {
                var before = hasOld && oldRow.TryGetProperty(field, out var o) ? Cell(o) : null;
                var after = hasNew && newRow.TryGetProperty(field, out var n) ? Cell(n) : null;
                if (before == after) continue;
                result.Add(new AuditFieldChange(FieldLabel(field), before, after));
            }

            return result;
        }

        /// <summary>A column name as a heading: `email_address` reads "Email address".</summary>
        private static string FieldLabel(string column)
        {
            var words = column.Replace('_', ' ').Trim();
            return words.Length == 0 ? column : char.ToUpperInvariant(words[0]) + words[1..];
        }

        /// <summary>One stored value, written for a reader rather than a parser.</summary>
        private static string? Cell(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => Text(value.GetString()),
            _ => value.GetRawText(),
        };

        // Timestamps are stored as text, and an ISO string in the middle of a sentence
        // is harder to read than the date it stands for.
        private static string? Text(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return DateTimeOffset.TryParse(raw, out var when)
                   && raw.Contains('-') && raw.Contains(':')
                ? when.ToString("MMM d, yyyy h:mm tt")
                : raw;
        }

        // "0-49/1234" -> 1234. "*" (count unknown) and anything unexpected -> -1.
        private static int ParseTotal(string? contentRange)
        {
            var slash = contentRange?.LastIndexOf('/') ?? -1;
            if (slash < 0) return -1;
            return int.TryParse(contentRange![(slash + 1)..], out var n) ? n : -1;
        }

        private async Task PostAsync(Dictionary<string, object?> row)
        {
            try
            {
                var url = _config["Supabase:Url"];
                var key = _config["Supabase:Key"];
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)) return;

                row["source"] = "web";
                row["ip"] = ClientIp();

                using var cts = new CancellationTokenSource(WriteTimeout);
                var req = new HttpRequestMessage(HttpMethod.Post, $"{url}/rest/v1/audit_log");
                req.Headers.TryAddWithoutValidation("apikey", key);
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
                req.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
                req.Content = new StringContent(
                    JsonSerializer.Serialize(row), Encoding.UTF8, "application/json");

                await _http.SendAsync(req, cts.Token);
            }
            catch
            {
                // Swallowed on purpose (see header).
            }
        }

        // Caller IP, best-effort. Behind a proxy the socket address is the proxy's, so
        // prefer the first hop in x-forwarded-for.
        private string? ClientIp()
        {
            var ctx = _accessor.HttpContext;
            if (ctx is null) return null;

            var fwd = ctx.Request.Headers["X-Forwarded-For"].ToString();
            if (!string.IsNullOrWhiteSpace(fwd)) return fwd.Split(',')[0].Trim();

            return ctx.Connection.RemoteIpAddress?.ToString();
        }
    }
}
