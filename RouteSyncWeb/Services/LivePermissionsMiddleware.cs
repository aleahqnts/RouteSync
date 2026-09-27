using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace FleetWise.Services
{
    /// <summary>
    /// Replaces the name, email, role and permission claims on the signed-in user with what
    /// their account and role hold now.
    /// </summary>
    /// <remarks>
    /// Done here rather than at each place that asks, so nothing downstream changes. The
    /// sidebar still calls User.HasClaim to decide which links to draw, and the permission
    /// filter still calls it to decide who gets in. Both now read a claim set rebuilt for
    /// this request instead of one written at sign-in and left to go stale.
    ///
    /// Runs between authentication and authorization: after the cookie has been read, and
    /// before anything decides what it allows.
    ///
    /// The name, email and role are refreshed for the same reason. The sidebar and the
    /// audit trail read the name from here, and the permissions are looked up by role, so
    /// a stale role would carry stale permissions with it.
    /// </remarks>
    public class LivePermissionsMiddleware
    {
        private readonly RequestDelegate _next;

        public LivePermissionsMiddleware(RequestDelegate next) => _next = next;

        public async Task InvokeAsync(HttpContext context, RolePermissions roles, LiveAccount accounts)
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
                // Everything the cookie carries except the permissions, which are replaced.
                // The forced-password-change claim is among the ones kept, or a first
                // sign-in would escape the change it is there to compel.
                var kept = identity.Claims.Where(c => c.Type != "perm").ToList();

                if (int.TryParse(context.User!.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId)
                    && await accounts.ForUserAsync(userId) is AccountNow now)
                {
                    Replace(kept, ClaimTypes.Name, now.Name);
                    Replace(kept, ClaimTypes.Email, now.Email);
                    if (now.RoleName is not null) Replace(kept, ClaimTypes.Role, now.RoleName);
                }

                var roleName = kept.FirstOrDefault(c => c.Type == ClaimTypes.Role)?.Value;
                var live = await roles.ForRoleAsync(roleName);

                var rebuilt = new ClaimsIdentity(kept, identity.AuthenticationType);
                foreach (var permission in live)
                    rebuilt.AddClaim(new Claim("perm", permission));

                context.User = new ClaimsPrincipal(rebuilt);
            }

            await _next(context);
        }

        private static void Replace(List<Claim> claims, string type, string value)
        {
            claims.RemoveAll(c => c.Type == type);
            claims.Add(new Claim(type, value));
        }
    }
}
