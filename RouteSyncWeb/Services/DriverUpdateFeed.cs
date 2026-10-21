using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace FleetWise.Services
{
    /// <summary>The newest driver app a GitHub Release carries as an APK.</summary>
    public record DriverUpdate(string Version, string ApkUrl);

    /// <summary>Tells the driver app which version is newest, without every phone asking GitHub.</summary>
    /// <remarks>
    /// GitHub allows sixty unauthenticated requests an hour per address, which phones sharing
    /// one network could use up. The dashboard asks once in a while and answers every phone
    /// from memory. A release counts only when it carries an asset named
    /// RouteSync-Driver-X.Y.Z.apk, so a release made for the dashboard alone offers nothing.
    /// </remarks>
    public class DriverUpdateFeed
    {
        private const string ReleasesUrl = "https://api.github.com/repos/aleahqnts/RouteSync/releases?per_page=10";
        private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);

        private readonly HttpClient _http;
        private readonly IMemoryCache _cache;

        public DriverUpdateFeed(HttpClient http, IMemoryCache cache)
        {
            _http = http;
            _cache = cache;
        }

        public async Task<DriverUpdate?> LatestAsync()
        {
            if (_cache.TryGetValue(nameof(DriverUpdateFeed), out DriverUpdate? kept)) return kept;

            try
            {
                var found = Pick(await _http.GetStringAsync(ReleasesUrl));
                _cache.Set(nameof(DriverUpdateFeed), found, Keep);
                return found;
            }
            catch (Exception)
            {
                // GitHub unreachable or refusing: say nothing for a minute rather than ask again at once.
                _cache.Set(nameof(DriverUpdateFeed), (DriverUpdate?)null, TimeSpan.FromMinutes(1));
                return null;
            }
        }

        /// <summary>The highest-numbered driver APK among published (not draft, not pre-release) releases.</summary>
        public static DriverUpdate? Pick(string releasesJson)
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
                    const string prefix = "RouteSync-Driver-";
                    if (!name.StartsWith(prefix) || !name.EndsWith(".apk")) continue;
                    if (!Version.TryParse(name[prefix.Length..^4], out var version)) continue;
                    if (best is not null && version <= best) continue;

                    best = version;
                    url = asset.GetProperty("browser_download_url").GetString();
                }
            }
            return best is null || url is null ? null : new DriverUpdate(best.ToString(3), url);
        }
    }
}
