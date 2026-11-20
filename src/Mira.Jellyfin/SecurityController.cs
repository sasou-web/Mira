using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Mira.Jellyfin;

/// <summary>
/// « Security » of Mira's Réglages (an administrator's), at /Mira/home/security: the accounts that open without a
/// password (<see cref="AccountPasswords"/>), which Jellyfin's API does not tell. Under /Mira/home: never through Tailscale
/// Funnel (<see cref="TunnelGuard"/>).
/// </summary>
[ApiController]
[Route("Mira/home/security")]
public sealed class SecurityController : ControllerBase
{
    /// <summary>The ids of the accounts without a password, and of a party's guests (theirs is the link's: never listed).</summary>
    [HttpGet("")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult Accounts()
    {
        var accounts = AccountPasswords.Read(HttpContext.RequestServices.GetService<IUserManager>());
        Response.Headers.CacheControl = "no-store";
        if (accounts is null)
        {
            return Ok(new { known = false, open = Array.Empty<string>(), guests = Array.Empty<string>() });
        }

        var guests = accounts.Where(x => Plugin.Invites.IsGuestAccount(x.Id)).Select(x => x.Id).ToList();
        var open = accounts.Where(x => x.Open && !guests.Contains(x.Id)).Select(x => x.Id).ToList();
        return Ok(new { known = true, open, guests });
    }
}
