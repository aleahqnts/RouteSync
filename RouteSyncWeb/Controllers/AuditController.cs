using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FleetWise.Models;
using FleetWise.Services;

namespace FleetWise.Controllers
{
    /// <summary>
    /// Read-only viewer for the audit trail.
    /// </summary>
    /// <remarks>
    /// Gated on its own permission, which no role holds until it is granted explicitly:
    /// reading the trail is a separate decision from operating the fleet.
    ///
    /// There is no edit or delete action here, and adding one would achieve nothing. The
    /// table refuses both through triggers of its own.
    /// </remarks>
    [Authorize]
    [RequirePermission("audit")]
    public class AuditController : Controller
    {
        private const int PageSize = 50;

        private readonly AuditLog _audit;
        private readonly SecurityIncidents _incidents;
        private readonly SecurityDetector _detector;
        private readonly Supabase.Client _supabase;

        public AuditController(
            AuditLog audit, SecurityIncidents incidents, SecurityDetector detector, Supabase.Client supabase)
        {
            _audit = audit;
            _incidents = incidents;
            _detector = detector;
            _supabase = supabase;
        }

        /// <summary>How many incidents the list shows, newest activity first.</summary>
        private const int IncidentListSize = 200;

        /// <summary>How many entries an incident's panel lists.</summary>
        private const int IncidentEntryLimit = 500;

        /// <summary>
        /// Unusual activity, one row per burst.
        /// </summary>
        /// <remarks>
        /// Detection only. Nothing on this page, and nothing behind it, blocks anybody.
        /// </remarks>
        [HttpGet]
        public async Task<IActionResult> Incidents()
        {
            var incidents = await _incidents.ListAsync(IncidentListSize);

            return View(new SecurityIncidentsViewModel
            {
                Rows = incidents is null ? null : await RowsForAsync(incidents),
                ScannedThrough = await _incidents.ScannedThroughAsync(),
                ScanMessage = TempData["ScanMessage"] as string,
            });
        }

        /// <summary>One incident and the audit entries behind it, for the panel.</summary>
        [HttpGet]
        public async Task<IActionResult> Incident(long id)
        {
            var incident = await _incidents.FindAsync(id);
            if (incident is null) return NotFound();

            var row = (await RowsForAsync(new List<SecurityIncident> { incident }))[0];

            // The incident's own filter, exactly as the detector counted it.
            List<AuditEntryViewModel>? entries = null;
            var filter = SecurityIncidents.FilterFor(incident);
            if (filter is not null)
            {
                (entries, _) = await _audit.QueryAsync(
                    $"select=*&{filter}&order=occurred_at.asc&limit={IncidentEntryLimit}");
            }

            return PartialView("_IncidentDetail", new SecurityIncidentDetailViewModel
            {
                Row = row,
                Entries = entries,
                SearchUrl = Url.Action(nameof(Index), new
                {
                    q = incident.KeyValue,
                    from = PhClock.ToPh(incident.FirstSeenAt).ToString("yyyy-MM-dd"),
                    to = PhClock.ToPh(incident.LastSeenAt).ToString("yyyy-MM-dd"),
                })!,
            });
        }

        /// <summary>Marks an incident reviewed, as of what the reviewer was shown.</summary>
        /// <param name="seen">
        /// The number of entries on the reviewer's screen. Anything gathered since is
        /// activity they did not see, and returns the incident to needing review.
        /// </param>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReviewIncident(long id, int seen, string? note)
        {
            var incident = await _incidents.FindAsync(id);
            if (incident is null) return NotFound();

            var me = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            // Checked here, where it cannot be skipped, and not only by hiding the button.
            if (incident.IsAbout(me))
            {
                await _audit.WriteAsync("incident_reviewed",
                    $"was refused review of security incident #{id}, which is about their own account",
                    "security_incidents", id, outcome: "denied");
                TempData["ScanMessage"] = "An incident about your own account has to be reviewed by somebody else.";
                return RedirectToAction(nameof(Incidents));
            }

            var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            if (trimmed is { Length: > 500 }) trimmed = trimmed[..500];

            // Never more than the incident held. A larger number from the form would mark as
            // seen entries that were never on anybody's screen.
            var shown = Math.Clamp(seen, 0, incident.EventCount);

            var reviewer = User.FindFirst(ClaimTypes.Name)?.Value ?? $"User {me}";
            if (!await _incidents.ReviewAsync(id, reviewer, trimmed, shown))
            {
                TempData["ScanMessage"] = "The review could not be saved. Try again.";
                return RedirectToAction(nameof(Incidents));
            }

            var title = SecurityRules.ByCode(incident.Rule)?.Title ?? incident.Rule;
            await _audit.WriteAsync("incident_reviewed",
                $"reviewed security incident #{id} ({title})"
                    + (trimmed is null ? "" : $": {trimmed}"),
                "security_incidents", id);

            return RedirectToAction(nameof(Incidents));
        }

        /// <summary>Runs the detector now rather than waiting for its next turn.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ScanIncidents()
        {
            var result = await _detector.ScanAsync(HttpContext.RequestAborted);

            TempData["ScanMessage"] = !result.Ran
                ? "The scan could not read the audit log. Nothing was changed."
                : result.Raised switch
                {
                    0 when result.Extended == 0 => "Scan complete. Nothing new.",
                    0 => $"Scan complete. {result.Extended} incident{(result.Extended == 1 ? "" : "s")} grew.",
                    1 => "Scan complete. 1 new incident.",
                    _ => $"Scan complete. {result.Raised} new incidents.",
                };

            return RedirectToAction(nameof(Incidents));
        }

        /// <summary>
        /// Puts names to the incidents, and decides which of them the reader may review.
        /// </summary>
        /// <remarks>
        /// An account is shown by name rather than by number, looked up in one request for
        /// the whole list. An account that no longer exists keeps its number, which is still
        /// what the audit entries behind it say.
        /// </remarks>
        private async Task<List<SecurityIncidentRow>> RowsForAsync(List<SecurityIncident> incidents)
        {
            var ids = incidents
                .Where(i => i.KeyColumn is "target_id" or "actor_id")
                .Select(i => int.TryParse(i.KeyValue, out var n) ? n : (int?)null)
                .Where(n => n is not null)
                .Select(n => n!.Value)
                .Distinct()
                .ToList();

            var names = new Dictionary<string, string>();
            if (ids.Count > 0)
            {
                try
                {
                    var users = await _supabase.From<UserModel>()
                        .Select("user_id,first_name,last_name")
                        .Filter("user_id", Postgrest.Constants.Operator.In, ids.Cast<object>().ToList())
                        .Get();

                    foreach (var u in users.Models)
                    {
                        var name = $"{u.FirstName} {u.LastName}".Trim();
                        if (name.Length > 0) names[u.UserId.ToString()] = name;
                    }
                }
                catch
                {
                    // Without names the incidents still read by account number, which is
                    // less friendly and no less true.
                }
            }

            var me = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            return incidents.Select(i => new SecurityIncidentRow(
                i,
                SecurityRules.ByCode(i.Rule)?.Title ?? i.Rule,
                i.KeyColumn == "ip"
                    ? $"Address {i.KeyValue}"
                    : names.TryGetValue(i.KeyValue, out var n) ? n : $"Account #{i.KeyValue}",
                !i.IsAbout(me)))
                .ToList();
        }

        // The filter parameter is named `type` rather than `action`, because the default
        // route pattern is {controller}/{action}/{id?}: a parameter called `action` would
        // bind to the route value instead of the query string.
        public async Task<IActionResult> Index(
            string? type, string? source, string? outcome,
            string? q, string? from, string? to, int page = 1)
        {
            if (page < 1) page = 1;

            var filters = new List<string>
            {
                "select=*",
                "order=id.desc",                      // newest first
                $"limit={PageSize}",
                $"offset={(page - 1) * PageSize}",
            };

            if (!string.IsNullOrWhiteSpace(type))
                filters.Add($"action=eq.{Uri.EscapeDataString(type.Trim())}");
            if (!string.IsNullOrWhiteSpace(source))
                filters.Add($"source=eq.{Uri.EscapeDataString(source.Trim())}");
            if (!string.IsNullOrWhiteSpace(outcome))
                filters.Add($"outcome=eq.{Uri.EscapeDataString(outcome.Trim())}");

            // Dates are chosen as Philippine calendar days but stored as UTC instants, so
            // both boundaries carry the offset explicitly. The end date includes its whole
            // day, which is why the filter is less-than the following midnight.
            if (DateTime.TryParse(from, out var d1))
                filters.Add($"occurred_at=gte.{Uri.EscapeDataString($"{d1:yyyy-MM-dd}T00:00:00+08:00")}");
            if (DateTime.TryParse(to, out var d2))
                filters.Add($"occurred_at=lt.{Uri.EscapeDataString($"{d2.AddDays(1):yyyy-MM-dd}T00:00:00+08:00")}");

            var term = Sanitize(q);
            if (term.Length > 0)
            {
                var t = Uri.EscapeDataString(term);
                // Each value is double-quoted. Inside an or-list a bare space ends the
                // value, so an unquoted "Admin User" is a parse error and the whole request
                // fails rather than returning nothing. Sanitize already removes the quote
                // character itself, so the quoting cannot be escaped from.
                filters.Add(
                    $"or=(summary.ilike.\"*{t}*\",actor_id.eq.\"{t}\"," +
                    $"target_id.ilike.\"*{t}*\",ip.ilike.\"*{t}*\")");
            }

            var (rows, total) = await _audit.QueryAsync(string.Join("&", filters));

            return View(new AuditIndexViewModel
            {
                Entries = rows ?? new(),
                LoadFailed = rows is null,
                Page = page,
                PageSize = PageSize,
                Total = total,
                Type = type,
                Source = source,
                Outcome = outcome,
                Query = q,
                From = from,
                To = to,
            });
        }

        /// <summary>
        /// Prepares a search term for use inside a PostgREST or-list.
        /// </summary>
        /// <remarks>
        /// Commas and parentheses are structural in that syntax. They are removed, along
        /// with the wildcard character, rather than escaped, so no input can reshape the
        /// query.
        /// </remarks>
        private static string Sanitize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var cleaned = new string(raw.Trim()
                .Where(c => c is not (',' or '(' or ')' or '*' or '"' or '\\'))
                .ToArray());
            return cleaned.Length > 80 ? cleaned[..80] : cleaned;
        }
    }
}
