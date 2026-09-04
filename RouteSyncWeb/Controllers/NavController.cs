using FleetWise.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FleetWise.Controllers
{
    /// <summary>Keeps the navigation rail's badges current without a page load.</summary>
    /// <remarks>
    /// The rail is drawn on every page, so the first paint comes from the same service
    /// this reads. This is only the second and every later reading.
    ///
    /// A tab the signed-in role cannot open is left out of the answer entirely rather
    /// than sent as a zero, so a count never travels to somebody with no way of seeing
    /// what it is about.
    /// </remarks>
    [Authorize]
    public class NavController : Controller
    {
        private readonly NavCounts _counts;

        public NavController(NavCounts counts) => _counts = counts;

        [HttpGet]
        public async Task<IActionResult> Badges()
        {
            var b = await _counts.ReadAsync();
            var mine = new Dictionary<string, object>();

            if (User.HasClaim("perm", "routes"))
                mine["dispatch"] = new { count = b.Dispatch.Count, urgent = b.Dispatch.Urgent };

            if (User.HasClaim("perm", "requests"))
                mine["requests"] = new { count = b.Requests.Count, urgent = b.Requests.Urgent };

            if (User.HasClaim("perm", "vehicles"))
                mine["vehicles"] = new { count = b.Vehicles.Count, urgent = b.Vehicles.Urgent };

            return Json(mine);
        }
    }
}
