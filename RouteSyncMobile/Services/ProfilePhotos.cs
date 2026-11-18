using System.Net.Http.Headers;

namespace FleetWiseMobile.Services;

// Moves the driver's own profile photograph between the phone and the storage bucket.
//
// Every change is stored under a new name in the driver's own folder, which is what the
// storage policy and the row's check constraint both hold it to. A name is never reused,
// so a copy kept on the phone under that name is never stale and the photograph travels
// once per change rather than once per visit to the profile.
public class ProfilePhotos
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private const string Bucket = "profile-photos";

    // Uploads the photograph and returns its new name, or null when it could not be stored.
    public async Task<string?> UploadAsync(int driverId, byte[] jpeg)
    {
        if (SupabaseConfig.Jwt is null || jpeg.Length == 0) return null;
        var key = $"{driverId}/{Guid.NewGuid():N}.jpg";
        try
        {
            var req = Request(HttpMethod.Post, $"object/{Bucket}/{key}");
            req.Content = new ByteArrayContent(jpeg);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ProfilePhotos] upload {(int)res.StatusCode} ({jpeg.Length} bytes)");
                return null;
            }
            Remember(key, jpeg);
            return key;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProfilePhotos] upload {ex.GetType().Name}");
            return null;
        }
    }

    // The photograph's bytes, from the phone when it was fetched before, or null.
    public async Task<byte[]?> ReadAsync(string key)
    {
        var cached = CachePath(key);
        if (File.Exists(cached)) return await File.ReadAllBytesAsync(cached);
        if (SupabaseConfig.Jwt is null) return null;
        try
        {
            var res = await _http.SendAsync(Request(HttpMethod.Get, $"object/authenticated/{Bucket}/{key}"));
            if (!res.IsSuccessStatusCode) return null;
            var bytes = await res.Content.ReadAsByteArrayAsync();
            Remember(key, bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProfilePhotos] read {ex.GetType().Name}");
            return null;
        }
    }

    // Removes a photograph nothing points at any more. A failure leaves a few kilobytes
    // behind and is not reported.
    public async Task DeleteAsync(string key)
    {
        try { File.Delete(CachePath(key)); } catch (IOException) { }
        if (SupabaseConfig.Jwt is null) return;
        try { await _http.SendAsync(Request(HttpMethod.Delete, $"object/{Bucket}/{key}")); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProfilePhotos] delete {ex.GetType().Name}");
        }
    }

    private static string CachePath(string key) =>
        Path.Combine(FileSystem.CacheDirectory, "profile-" + Path.GetFileName(key));

    private static void Remember(string key, byte[] bytes)
    {
        try { File.WriteAllBytes(CachePath(key), bytes); } catch (IOException) { }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, $"{SupabaseConfig.Url}/storage/v1/{path}");
        req.Headers.TryAddWithoutValidation("apikey", SupabaseConfig.Key);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {SupabaseConfig.Jwt}");
        return req;
    }
}
