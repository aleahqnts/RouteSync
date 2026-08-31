using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace FleetWise.Services
{
    /// <summary>
    /// Replaces the permission claims on the signed-in user with what their role holds now.
    /// </summary>
    /// <remarks>
    /// Done here rather than at each place that asks, so nothing downstream changes. The
    /// sidebar still calls User.HasClaim to decide which links to draw, and the permission
    /// filter still calls it to decide who gets in. Both now read a claim set rebuilt for
    /// this request instead of one written at sign-in and left to go stale.
    ///
    /// Runs between authentication and authorization: after the cookie has been read, and
    /// before anything decides what it allows.
    /// </remarks>
    public class LivePermissionsMiddleware
    {
        private readonly RequestDelegate _next;

        public LivePermissionsMiddleware(RequestDelegate next) => _next = next;

        public async Task InvokeAsync(HttpContext context, RolePermissions roles)
        {
            var identity = context.User?.Identity as ClaimsIdentity;

            // Stylesheets and scripts are endpoints as well, and routing has already run by
            // the time this does. Without this every asset on a page would ask what the
            // signed-in user may see, which nothing then reads. Only a page rendered by a
            // controller has a sidebar to draw or an action to guard.
            var isPage = context.GetEndpoint()?.Metadata
                .GetMetadata<ControllerActionDescriptor>() is not null;

            if (isPage && identity?.IsAuthenticated == true)
            {
                var roleName = context.User!.FindFirst(ClaimTypes.Role)?.Value;
                var live = await roles.ForRoleAsync(roleName);

                // Everything the cookie carries except the permissions, which are replaced.
                // The forced-password-change claim is among the ones kept, or a first
                // sign-in would escape the change it is there to compel.
                var kept = identity.Claims.Where(c => c.Type != "perm");
                var rebuilt = new ClaimsIdentity(kept, identity.AuthenticationType);
                foreach (var permission in live)
                    rebuilt.AddClaim(new Claim("perm", permission));

                context.User = new ClaimsPrincipal(rebuilt);
            }

            await _next(context);
        }
    }
}
