using System.Security.Claims;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

/// <summary>
/// Guards an endpoint with one permission instead of "is an admin". The permissions are read from the
/// user store on every request rather than from the sign-in cookie: taking a permission away has to
/// take effect now, not after the user happens to log in again.
/// </summary>
public static class PermissionPolicy
{
    public static TBuilder RequirePerm<TBuilder>(this TBuilder builder, string perm)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy.RequireAssertion(context =>
        {
            if (context.User.IsInRole("Admin")) return true;
            var id = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(id)) return false;
            // Endpoint-based authorization hands the HttpContext along as the resource, which is the
            // only way to reach the store from inside a policy assertion.
            var users = (context.Resource as HttpContext)?.RequestServices.GetService<UserStore>();
            return Perm.Has(users?.Get(id), perm);
        }));
}
