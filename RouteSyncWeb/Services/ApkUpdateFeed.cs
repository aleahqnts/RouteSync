using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace FleetWise.Services
{
    // The newest phone app a GitHub Release carries as an APK.
    public record ApkUpdate(string Version, string ApkUrl);

    // Tells the phone apps which version is newest, without every phone asking GitHub.
    //
    // GitHub allows sixty unauthenticated requests an hour per address, which phones sharing
    // one network could use up. The dashboard fetches the release list once in a while and
    // answers every phone from memory. A release counts for an app only when it carries an
    // asset named with that app's prefix, such as RouteSync-Driver-X.Y.Z.apk, so a release
    // made for the dashboard alone offers nothing.
    public class ApkUpdateFeed
    {
        public const string Driver = "RouteSync-Driver-";
        public const string Sentinel = "RouteSync-Sentinel-";

        private const string ReleasesUrl = "https://api.github.com/repos/aleahqnts/RouteSync/releases?per_page=10";
        private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);

        private readonly HttpClient _http;
        private readonly IMemoryCache _cache;

        public ApkUpdateFeed(HttpClient http, IMemoryCache cache)
        {
            _http = http;
            _cache = cache;
        }

        public async Task<ApkUpdate?> LatestAsync(string prefix) =>
            await ReleasesAsync() is { } json ? Pick(json, prefix) : null;

        // The release list, shared by both apps' checks.
        private async Task<string?> ReleasesAsync()
        {
            if (_cache.TryGetValue(nameof(ApkUpdateFeed), out string? kept)) return kept;

            try
            {
                var json = await _http.GetStringAsync(ReleasesUrl);
                _cache.Set(nameof(ApkUpdateFeed), json, Keep);
                return json;
            }
            catch (Exception)
            {
                // GitHub unreachable or refusing: say nothing for a minute rather than ask again at once.
                _cache.Set(nameof(ApkUpdateFeed), (string?)null, TimeSpan.FromMinutes(1));
                return null;
            }
        }

        // The highest-numbered APK with this prefix among published (not draft, not
        // pre-release) releases.
        public static ApkUpdate? Pick(string releasesJson, string prefix)
        {
            Version? best = null;
            string? url = null;
            using var doc = JsonDocument.Parse(releasesJson);
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
                if (!release.TryGetProperty("assets", out var assets)) continue;

                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (!name.StartsWith(prefix) || !name.EndsWith(".apk")) continue;
                    if (!Version.TryParse(name[prefix.Length..^4], out var version)) continue;
                    if (best is not null && version <= best) continue;

                    best = version;
                    url = asset.GetProperty("browser_download_url").GetString();
                }
            }
            return best is null || url is null ? null : new ApkUpdate(best.ToString(3), url);
        }
    }
}
