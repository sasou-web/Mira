using System.Security.Cryptography;

namespace Mira.Jellyfin;

/// <summary>
/// The extension's secret behind every account's key: 32 random bytes in their own file beside the plugins' settings,
/// made once, sealed there (<see cref="ServerSecrets"/>).
/// </summary>
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
            ServerSecrets.Restrict(file);
            ServerSecrets.Plain();
            if (File.Exists(file) && ServerSecrets.Open(File.ReadAllText(file).Trim()) is { } text && Convert.FromBase64String(text) is { Length: 32 } kept)
            {
                if (ServerSecrets.Plain() && ServerSecrets.Sealing)
                {
                    // In clear (0.9.15 and older): sealed now, the same secret (the keys given still prove).
                    Keep(file, kept);
                }

                return kept;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            // Unreadable or damaged: a new one below (keys given before stop proving, as after a new install).
        }

        var made = RandomNumberGenerator.GetBytes(32);
        // Never a secret that would change at the next start: no proof rather than one that stops holding.
        return Keep(file, made) ? made : null;
    }

    private static bool Keep(string file, byte[] secret)
    {
        try
        {
            ServerSecrets.Write(file, ServerSecrets.Seal(Convert.ToBase64String(secret)));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
