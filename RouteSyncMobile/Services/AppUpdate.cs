namespace FleetWiseMobile.Services;

/// <summary>A newer driver app that a GitHub Release carries as an APK.</summary>
public record UpdateOffer(Version Version, string ApkUrl);

/// <summary>Finds out whether a newer driver app has been published, and downloads it.</summary>
/// <remarks>
/// The dashboard answers the check from the GitHub Releases it keeps in memory, so phones
/// sharing a network never run into GitHub's per-address request limit. The APK itself is
/// downloaded from GitHub.
/// </remarks>
public static class AppUpdate
{
    private const string FeedUrl = "https://dashboard.routesync.website/app/driver-update";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>The offer when the published driver app is newer than <paramref name="installed"/>, else null.</summary>
    public static async Task<UpdateOffer?> CheckAsync(Version installed)
    {
        try
        {
            using var response = await _http.GetAsync(FeedUrl);
            if (response.StatusCode != System.Net.HttpStatusCode.OK) return null;
            return Parse(await response.Content.ReadAsStringAsync(), installed);
        }
        catch (Exception ex)
        {
            // No signal or the dashboard is down: the driver just isn't offered anything.
            System.Diagnostics.Debug.WriteLine($"[AppUpdate] {ex.Message}");
            return null;
        }
    }

    /// <summary>The offer in the dashboard's answer when it is above <paramref name="installed"/>.</summary>
    public static UpdateOffer? Parse(string feedJson, Version installed)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(feedJson);
        var root = doc.RootElement;
        if (!Version.TryParse(root.GetProperty("version").GetString(), out var version) || version <= installed) return null;
        return new UpdateOffer(version, root.GetProperty("apkUrl").GetString()!);
    }

    /// <summary>Saves the APK to <paramref name="path"/>, reporting 0 to 1 as it goes.</summary>
    public static async Task DownloadAsync(string url, string path, IProgress<double> progress)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RouteSync-Driver");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(path);

        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read));
            done += read;
            if (total > 0) progress.Report((double)done / total);
        }
    }
}

/// <summary>Hands a downloaded APK to the phone's installer.</summary>
public interface IApkInstaller
{
    /// <summary>
    /// False when the driver has not yet allowed this app to install apps. The phone's
    /// own settings page for that has been opened, and the driver taps Update again after.
    /// </summary>
    bool EnsureAllowed();

    void Install(string apkPath);
}

/// <summary>For the Windows build, which has nothing to install.</summary>
public class NoopApkInstaller : IApkInstaller
{
    public bool EnsureAllowed() => false;
    public void Install(string apkPath) { }
}
