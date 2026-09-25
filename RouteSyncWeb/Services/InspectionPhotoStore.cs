using FleetWise.Models;
using static Postgrest.Constants;

namespace FleetWise.Services
{
    /// <summary>
    /// Reads inspection photographs out of the private bucket for the dashboard.
    /// </summary>
    /// <remarks>
    /// A photograph is asked for by its row, never by its stored path. The browser can
    /// already see which photographs belong to a fault, so naming one costs it nothing,
    /// while accepting a path would let anyone signed in fetch any object in the bucket:
    /// the key used here is the service key, and it fetches whatever it is given.
    ///
    /// Shared by every page that shows photographs, so that rule is written once. Each
    /// page still serves the bytes from its own controller, because the controllers are
    /// gated by different permissions and a dispatcher who cannot open the vehicle pages
    /// must still see the photograph on a trip.
    /// </remarks>
    public class InspectionPhotoStore
    {
        private static readonly HttpClient _http = new();

        private readonly Supabase.Client _supabase;
        private readonly IConfiguration _config;

        public InspectionPhotoStore(Supabase.Client supabase, IConfiguration config)
        {
            _supabase = supabase;
            _config = config;
        }

        /// <summary>The photograph's bytes, or null when there is nothing to show.</summary>
        /// <remarks>
        /// Null covers a row that does not exist, a row whose photograph has been swept,
        /// and an object the bucket would not return. The caller answers all three the
        /// same way, since none of them is something the browser can act on.
        /// </remarks>
        public async Task<byte[]?> ReadAsync(long photoId)
        {
            var row = (await _supabase.From<InspectionPhoto>()
                .Select("photo_id,object_key")
                // As text: the filter accepts a fixed set of criterion types and a long is
                // not among them, which fails at run time rather than at compile time.
                .Filter("photo_id", Operator.Equals, photoId.ToString())
                .Limit(1)
                .Get()).Models.FirstOrDefault();

            // A swept photograph keeps its row and loses its object, which is the point of
            // keeping the row. A page does not offer one, but can still be asked for it.
            if (row?.ObjectKey is null) return null;

            var key = _config["Supabase:Key"];
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"{_config["Supabase:Url"]}/storage/v1/object/authenticated/inspection-photos/{row.ObjectKey}");
            req.Headers.TryAddWithoutValidation("apikey", key);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");

            var res = await _http.SendAsync(req);
            return res.IsSuccessStatusCode ? await res.Content.ReadAsByteArrayAsync() : null;
        }

        /// <summary>
        /// Cache header for a photograph. A photograph is written once under a name nothing
        /// reuses, so a second look at the same one need not travel again.
        /// </summary>
        public const string CacheControl = "private, max-age=31536000, immutable";
    }
}
