namespace FleetWiseMobile.Services;

// A newer driver app that a GitHub Release carries as an APK.
public record UpdateOffer(Version Version, string ApkUrl);

// Finds out whether a newer driver app has been published, and downloads it.
//
// The dashboard answers the check from the GitHub Releases it keeps in memory, so phones
// sharing a network never run into GitHub's per-address request limit. The APK itself is
// downloaded from GitHub.
public static class AppUpdate
{
    private const string FeedUrl = "https://dashboard.routesync.website/app/driver-update";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // The offer when the published driver app is newer than installed, else null.
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

    // The offer in the dashboard's answer when it is above installed.
    public static UpdateOffer? Parse(string feedJson, Version installed)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(feedJson);
        var root = doc.RootElement;
        if (!Version.TryParse(root.GetProperty("version").GetString(), out var version) || version <= installed) return null;
        return new UpdateOffer(version, root.GetProperty("apkUrl").GetString()!);
    }

    // Saves the APK to path, reporting 0 to 1 as it goes.
    //
    // Written beside it under a .part name and renamed only once complete, so a file at
    // path is always a whole download that can be installed again
    // without fetching it a second time.
    public static async Task DownloadAsync(string url, string path, IProgress<double> progress)
    {
        var part = path + ".part";
        await DownloadToAsync(url, part, progress);
        File.Move(part, path, overwrite: true);
    }

    private static async Task DownloadToAsync(string url, string path, IProgress<double> progress)
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

// Hands a downloaded APK to the phone's installer.
public interface IApkInstaller
{
    // False when the driver has not yet allowed this app to install apps. The phone's
    // own settings page for that has been opened, and the driver taps Update again after.
    bool EnsureAllowed();

    void Install(string apkPath);
}

// For the Windows build, which has nothing to install.
public class NoopApkInstaller : IApkInstaller
{
    public bool EnsureAllowed() => false;
    public void Install(string apkPath) { }
}
