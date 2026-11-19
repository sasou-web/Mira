using System.Security.Cryptography;
using Mira.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Mira.Jellyfin;

/// <summary>
/// The proof that this Jellyfin is the one an account signed in to, asked by Mira's apps when they find it at another
/// address of the home network (<see cref="ServerProof"/>, ServerMove): the account's key with its session, once; the
/// proof for a nonce without one. Never through Tailscale Funnel (<see cref="GuestRoutes.ThroughTunnel"/>): a device
/// that passed the question on from the Internet gets a 404.
/// </summary>
[ApiController]
[Route("Mira/proof")]
public sealed class ProofController : ControllerBase
{
    // Jellyfin's own claim for the account of a request (its InternalClaimTypes.UserId).
    private const string UserClaim = "Jellyfin-UserId";

    /// <summary>This account's key, kept by its apps with the session.</summary>
    [HttpGet("key")]
    [Authorize]
    public IActionResult Key()
    {
        if (ServerProof.PlainId(User.FindFirst(UserClaim)?.Value) is not { } user)
        {
            return Unauthorized();
        }

        if (Plugin.ProofSecret is not { } secret)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        Response.Headers.CacheControl = "no-store";
        return Ok(new { key = Convert.ToBase64String(ServerProof.Key(secret, user)) });
    }

    /// <summary>The proof for <paramref name="nonce"/>, asked at this request's host, with <paramref name="user"/>'s key.</summary>
    [HttpGet("")]
    [AllowAnonymous]
    public IActionResult Prove([FromQuery] string? user, [FromQuery] string? nonce)
    {
        if (ServerProof.PlainId(user) is not { } id || !ServerProof.IsNonce(nonce) || Request.Host.Value is not { Length: > 0 } host)
        {
            return BadRequest();
        }

        if (Plugin.ProofSecret is not { } secret)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        Response.Headers.CacheControl = "no-store";
        return Ok(new { proof = ServerProof.Prove(ServerProof.Key(secret, id), nonce!, host) });
    }
}

/// <summary>The extension's secret behind every account's key: 32 random bytes in their own file beside the plugins' settings, made once.</summary>
internal static class ProofSecrets
{
    public static byte[]? Read(string? file)
    {
        if (file is null)
        {
            return null;
        }

        try
        {
            if (File.Exists(file) && Convert.FromBase64String(File.ReadAllText(file).Trim()) is { Length: 32 } kept)
            {
                return kept;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            // Unreadable or damaged: a new one below (keys given before stop proving, as after a new install).
        }

        var made = RandomNumberGenerator.GetBytes(32);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, Convert.ToBase64String(made));
            File.Move(temporary, file, true);
            return made;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Never a secret that would change at the next start: no proof rather than one that stops holding.
            return null;
        }
    }
}
