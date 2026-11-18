using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;

namespace FleetWise.Services
{
    // Gates a controller or action on a permission held by the signed-in user's role.
    //
    // The permission claims are rebuilt on every request by LivePermissionsMiddleware from
    // the role's stored permissions, and a user whose role lacks the permission is
    // redirected to the dashboard. This pairs with the sidebar, which hides the link for the
    // same permission.
    //
    // A refusal is also an audit event. The sidebar has already hidden the link, so arriving
    // here usually means the address was entered directly, which is worth recording.
    //
    // The filter is asynchronous so that write can be awaited rather than blocking a
    // request thread.
    public class RequirePermissionAttribute : Attribute, IAsyncAuthorizationFilter
    {
        // One audit entry per person, address and permission in this window. A page left
        // open polls its own endpoints every few seconds, so one refusal repeated by a timer
        // would otherwise write an entry per poll and read as someone trying many pages.
        // Different addresses are still each recorded, which is what the refused pages
        // incident looks for.
        public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(1);

        private readonly string _permission;
        public RequirePermissionAttribute(string permission) => _permission = permission;

        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var user = context.HttpContext.User;
            if (user?.Identity?.IsAuthenticated != true) return; // [Authorize] handles unauthenticated
            if (user.HasClaim("perm", _permission)) return;

            var services = context.HttpContext.RequestServices;
            var audit = services.GetService<AuditLog>();
            var path = context.HttpContext.Request.Path.Value ?? "";
            var cache = services.GetService<IMemoryCache>();
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (audit is not null && (cache is null || IsFirstInWindow(cache, userId, path, _permission)))
            {
                await audit.WriteAsync("access_denied",
                    $"was blocked from {path} (needs the {_permission} permission)",
                    outcome: "denied");
            }

            context.Result = new RedirectToActionResult("Index", "Dashboard", null);
        }

        // True for the first refusal of this person, address and permission in the window,
        // false for repeats until it passes.
        public static bool IsFirstInWindow(IMemoryCache cache, string? userId, string path, string permission)
        {
            var key = $"denied:{userId}:{path.ToLowerInvariant()}:{permission}";
            if (cache.TryGetValue(key, out _)) return false;
            cache.Set(key, true, RepeatWindow);
            return true;
        }
    }
}
