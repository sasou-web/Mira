using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Mira.Jellyfin;

/// <summary>
/// TorLink's door (<see cref="TorLinkController"/>), after Jellyfin authenticated the request: an administrator of
/// Jellyfin (its role claim), or an account an administrator let download (<see cref="HomeFamily.CanDownload"/>).
/// Anyone else: 403, as Jellyfin's « RequiresElevation » answered before.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class TorLinkAccessAttribute : Attribute, IAuthorizationFilter
{
    // Jellyfin's own claim for the account of a request (its InternalClaimTypes.UserId), and its administrators' role.
    private const string UserClaim = "Jellyfin-UserId";
    private const string AdministratorRole = "Administrator";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status401Unauthorized);
            return;
        }

        if (!Allowed(user))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
        }
    }

    /// <summary>Whether this account reaches TorLink: an administrator, or let download.</summary>
    public static bool Allowed(ClaimsPrincipal user) =>
        user.HasClaim(ClaimTypes.Role, AdministratorRole) || Plugin.Home.CanDownload(user.FindFirst(UserClaim)?.Value);
}
