using FleetWise.Models;
using Postgrest.Attributes;
using Postgrest.Models;
using static Postgrest.Constants;

namespace FleetWise.Services
{
    // Profile photographs in the private profile-photos bucket, for the dashboard.
    //
    // A photograph is asked for by its owner's user id, never by its stored path, for the
    // same reason as inspection photographs: the key used here is the service key, and it
    // fetches whatever path it is given.
    //
    // Each change is stored under a new name and the row is pointed at it, so the URL a
    // page builds from the name can be cached for good, and the old object is removed
    // once nothing points at it.
    public class ProfilePhotoStore
    {
        private const string Bucket = "profile-photos";

        // The bucket refuses anything larger, so a bigger upload is answered here instead.
        public const int MaxBytes = 262144;

        public const string CacheControl = "private, max-age=31536000, immutable";

        private static readonly HttpClient _http = new();

        private readonly Supabase.Client _supabase;
        private readonly IConfiguration _config;

        public ProfilePhotoStore(Supabase.Client supabase, IConfiguration config)
        {
            _supabase = supabase;
            _config = config;
        }

        // The version part of a URL for this photograph, or null when there is none.
        // Just the object's file name, which changes whenever the photograph does.
        public static string? Version(string? photoKey) =>
            string.IsNullOrEmpty(photoKey) ? null : Path.GetFileNameWithoutExtension(photoKey);

        // The URL a page shows, or null when the person has no photograph.
        public static string? Url(int userId, string? photoKey) =>
            Version(photoKey) is string v ? $"/Profile/Photo/{userId}?v={v}" : null;

        public static bool IsJpeg(byte[] bytes) =>
            bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

        public async Task<string?> KeyAsync(int userId) =>
            (await _supabase.From<UserPhotoRow>()
                .Filter("user_id", Operator.Equals, userId.ToString())
                .Get()).Models.FirstOrDefault()?.PhotoKey;

        // The current photograph's name and bytes, or null when there is nothing to show.
        public async Task<(string Key, byte[] Bytes)?> ReadAsync(int userId)
        {
            if (await KeyAsync(userId) is not string key) return null;

            var res = await _http.SendAsync(Request(HttpMethod.Get, $"object/authenticated/{Bucket}/{key}"));
            return res.IsSuccessStatusCode ? (key, await res.Content.ReadAsByteArrayAsync()) : null;
        }

        // Stores a new photograph and points the row at it, then removes the old one.
        // Returns false when the bucket refused the upload, in which case the row is
        // unchanged.
        public async Task<bool> ReplaceAsync(int userId, byte[] jpeg)
        {
            var old = await KeyAsync(userId);
            var key = $"{userId}/{Guid.NewGuid():N}.jpg";

            var put = Request(HttpMethod.Post, $"object/{Bucket}/{key}");
            put.Content = new ByteArrayContent(jpeg);
            put.Content.Headers.ContentType = new("image/jpeg");
            if (!(await _http.SendAsync(put)).IsSuccessStatusCode) return false;

            await SetKeyAsync(userId, key);
            LiveAccount.Invalidate();
            if (old is not null) await DeleteObjectAsync(old);
            return true;
        }

        // Clears the row and removes the object. Returns false when there was none.
        public async Task<bool> RemoveAsync(int userId)
        {
            if (await KeyAsync(userId) is not string old) return false;
            await SetKeyAsync(userId, null);
            LiveAccount.Invalidate();
            await DeleteObjectAsync(old);
            return true;
        }

        // Only the one column is sent, so a profile saved at the same moment elsewhere
        // neither loses this change nor has it undone.
        private Task SetKeyAsync(int userId, string? key) =>
            _supabase.From<UserPhotoRow>().Update(new UserPhotoRow { UserId = userId, PhotoKey = key });

        // A failure leaves an object nothing points at, which costs a few kilobytes and
        // is not worth failing the change over.
        private async Task DeleteObjectAsync(string key)
        {
            try { await _http.SendAsync(Request(HttpMethod.Delete, $"object/{Bucket}/{key}")); }
            catch (HttpRequestException) { }
        }

        private HttpRequestMessage Request(HttpMethod method, string path)
        {
            var key = _config["Supabase:Key"];
            var req = new HttpRequestMessage(method, $"{_config["Supabase:Url"]}/storage/v1/{path}");
            req.Headers.TryAddWithoutValidation("apikey", key);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
            return req;
        }
    }

    // The users row narrowed to its photograph, so a write touches nothing else.
    [Table("users")]
    public class UserPhotoRow : BaseModel
    {
        [PrimaryKey("user_id")]
        public int UserId { get; set; }

        [Column("photo_key")]
        public string? PhotoKey { get; set; }
    }
}
