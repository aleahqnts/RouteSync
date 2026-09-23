using System.Net.Http.Headers;

namespace FleetWiseMobile.Services;

/// <summary>
/// Moves inspection photographs between the phone and the storage bucket.
/// </summary>
/// <remarks>
/// The object is named on the device, before the inspection is submitted and therefore
/// before the server has given the inspection an identifier. The name is generated when a
/// photograph is first attached to an item and kept if it is retaken, so a retake replaces
/// rather than accumulating, and a submit retried after a timeout claims the object the
/// first attempt uploaded instead of a second copy of it.
///
/// The folder is the driver's own identifier. It carries no meaning to any reader and
/// exists so the storage policy has something to check: a bare name says nothing about who
/// is entitled to write it.
/// </remarks>
public class InspectionPhotos
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private const string Bucket = "inspection-photos";

    /// <summary>The object's path within the bucket, which is also what is recorded.</summary>
    public static string KeyFor(int driverId, Guid objectId) => $"{driverId}/{objectId:N}.jpg";

    /// <summary>
    /// Sends a photograph, replacing any earlier one under the same name.
    /// </summary>
    /// <returns>
    /// True once the bytes are stored. A false answer means the inspection must be
    /// submitted without this photograph, because a reference to an object that is not
    /// there is worse than no reference at all.
    /// </returns>
    public async Task<bool> UploadAsync(int driverId, Guid objectId, byte[] jpeg)
    {
        if (SupabaseConfig.Jwt is null || jpeg.Length == 0) return false;
        try
        {
            var key = KeyFor(driverId, objectId);
            var req = new HttpRequestMessage(HttpMethod.Put,
                $"{SupabaseConfig.Url}/storage/v1/object/{Bucket}/{key}")
            {
                Content = new ByteArrayContent(jpeg),
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            req.Headers.TryAddWithoutValidation("apikey", SupabaseConfig.Key);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {SupabaseConfig.Jwt}");

            var res = await _http.SendAsync(req);
            if (res.IsSuccessStatusCode) return true;

            // The refusals worth telling apart: 413 is the bucket's size limit, which means
            // the reduction did not do its job, and 403 is the storage policy, which means
            // the folder does not match the signed-in driver.
            System.Diagnostics.Debug.WriteLine(
                $"[InspectionPhotos] upload {(int)res.StatusCode} for {key} ({jpeg.Length} bytes)");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InspectionPhotos] upload {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Removes a photograph the driver has taken back, either by marking the item Pass
    /// again or by removing it outright.
    /// </summary>
    /// <remarks>
    /// This is the only cleanup that happens when it is needed. An inspection abandoned
    /// outright leaves an object nothing points at, which an app that is no longer running
    /// cannot do anything about, and which the orphan sweep removes instead.
    ///
    /// A failure is not reported. The driver has already moved on, the object is no longer
    /// referenced by anything, and the sweep will reach it.
    /// </remarks>
    public async Task DeleteAsync(int driverId, Guid objectId)
    {
        if (SupabaseConfig.Jwt is null) return;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Delete,
                $"{SupabaseConfig.Url}/storage/v1/object/{Bucket}/{KeyFor(driverId, objectId)}");
            req.Headers.TryAddWithoutValidation("apikey", SupabaseConfig.Key);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {SupabaseConfig.Jwt}");
            await _http.SendAsync(req);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InspectionPhotos] delete {ex.GetType().Name}");
        }
    }
}
