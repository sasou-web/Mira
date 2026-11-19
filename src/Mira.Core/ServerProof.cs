using System.Security.Cryptography;
using System.Text;

namespace Mira.Core;

// Compiled by Mira's apps and by the extension (src/Mira.Jellyfin, /Mira/proof): both sides of the same proof.

/// <summary>
/// The proof that Jellyfin found at another address of the home network is this account's own (<see cref="ServerMove"/>),
/// asked before its token goes there. Jellyfin's id is public (System/Info/Public): a device of another network that
/// answered with it, at a hotel or a friend's, would otherwise have been sent the token. The extension keeps a secret of
/// its own; each account gets a key made from it (<see cref="Key"/>, GET /Mira/proof/key, with its session, once at
/// home); a server found elsewhere is asked to sign a random nonce with that key, and the address it was asked at
/// (GET /Mira/proof?user=…&amp;nonce=…, no session): only the real server can, and a device that passed the question on
/// to it by another way is asked at another address. Neither the secret nor the key ever leave as such.
/// </summary>
public static class ServerProof
{
    /// <summary>The nonce's size, in bytes.</summary>
    public const int NonceBytes = 32;

    /// <summary>The key of one account, made from the extension's <paramref name="secret"/>: another account's key proves nothing for this one.</summary>
    public static byte[] Key(byte[] secret, string userId) =>
        HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes("mira-proof\n" + PlainId(userId)));

    /// <summary>The proof for <paramref name="nonce"/> asked at <paramref name="host"/> (the address's host and port, as the request's Host).</summary>
    public static string Prove(byte[] key, string nonce, string host) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(nonce + "\n" + host.Trim().ToLowerInvariant())));

    /// <summary>Whether <paramref name="proof"/> is the one for this nonce and host, compared in constant time.</summary>
    public static bool Holds(string? key, string nonce, string host, string? proof)
    {
        if (key is not { Length: > 0 } || proof is not { Length: > 0 }) return false;
        byte[] bytes, expected, given;
        try { bytes = Convert.FromBase64String(key); given = Convert.FromBase64String(proof); }
        catch (FormatException) { return false; }
        expected = Convert.FromBase64String(Prove(bytes, nonce, host));
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }

    /// <summary>A new nonce, URL-safe.</summary>
    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(NonceBytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Whether <paramref name="nonce"/> may be one (URL-safe base64, 32 to 128 characters).</summary>
    public static bool IsNonce(string? nonce) => nonce is { Length: >= 32 and <= 128 } && nonce.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_');

    /// <summary>A user's id as Jellyfin writes it in a session (« N »: 32 lowercase digits); null when it is none.</summary>
    public static string? PlainId(string? userId) => Guid.TryParse(userId, out var id) ? id.ToString("N") : null;
}
