using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FleetWise.Services
{
    // Gates a controller or action on a permission held by the signed-in user's role.
    //
    // Permission claims are stamped at sign-in from the role's stored permissions, and a
    // user whose role lacks the permission is redirected to the dashboard. This pairs with
    // the sidebar, which hides the link for the same permission. Because claims are issued
    // at sign-in, a change to a role takes effect on that user's next sign-in.
    //
    // A redirect is also an audit event. The sidebar has already hidden the link, so
    // arriving here means the address was entered directly, which is worth recording.
    //
    // The filter is asynchronous so that write can be awaited rather than blocking a
    // request thread.
    public class RequirePermissionAttribute : Attribute, IAsyncAuthorizationFilter
    {
        private readonly string _permission;
        public RequirePermissionAttribute(string permission) => _permission = permission;

        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var user = context.HttpContext.User;
            if (user?.Identity?.IsAuthenticated != true) return; // [Authorize] handles unauthenticated
            if (user.HasClaim("perm", _permission)) return;

            var audit = context.HttpContext.RequestServices.GetService<AuditLog>();
            if (audit is not null)
            {
                var path = context.HttpContext.Request.Path.Value ?? "";
                await audit.WriteAsync("access_denied",
                    $"was blocked from {path} (needs the {_permission} permission)",
                    outcome: "denied");
            }

            context.Result = new RedirectToActionResult("Index", "Dashboard", null);
        }
    }
}
