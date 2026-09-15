using Postgrest.Interfaces;
using Postgrest.Models;

namespace FleetWise.Services
{
    /// <summary>Reads every row a query matches, a page at a time.</summary>
    /// <remarks>
    /// Supabase answers any one request with at most 1,000 rows and says nothing about the
    /// rest. A month of trips across the whole fleet runs past that, and a plan built on
    /// the first thousand would write trips into slots that already have them.
    ///
    /// The query is built again for every page, ordered on a unique column so no row is
    /// read twice or skipped between pages. A page shorter than the page size is the last.
    /// </remarks>
    public static class PagedRead
    {
        /// <summary>The most rows Supabase returns for one request.</summary>
        public const int PageSize = 1000;

        public static async Task<List<T>> AllAsync<T>(Func<IPostgrestTable<T>> query) where T : BaseModel, new()
        {
            var rows = new List<T>();

            for (var from = 0; ; from += PageSize)
            {
                var page = await query().Range(from, from + PageSize - 1).Get();
                rows.AddRange(page.Models);
                if (page.Models.Count < PageSize) return rows;
            }
        }
    }
}
