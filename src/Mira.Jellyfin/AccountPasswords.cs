using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Mira.Jellyfin;

/// <summary>
/// Which accounts of this Jellyfin open without a password (« Security » of Mira's Réglages, an administrator's): read
/// from the accounts themselves, in Jellyfin, because its API cannot tell. Jellyfin 12.1 says « HasPassword » true for
/// an account made without one, and such an account lets anyone in who knows its name (checked on 12.1, 2026-11-20). An
/// account's stored password is an empty one (none at all), or the hash of an empty one: tried here with Jellyfin's own
/// PBKDF2, never by signing in (a wrong try counts toward Jellyfin's lock of the account). Nothing else is read, kept or
/// sent: the answer is a list of ids.
/// </summary>
internal static class AccountPasswords
{
    /// <summary>
    /// Each account's id (32 hexadecimal digits) and whether it opens without a password; null when Jellyfin's accounts
    /// cannot be read in this version (its user's type moved between 10.9 and 12: read by name, never by type).
    /// </summary>
    public static IReadOnlyList<(string Id, bool Open)>? Read(object? userManager)
    {
        if (userManager is null)
        {
            return null;
        }

        try
        {
            // « Users » up to 10.11, « GetUsers() » in 12.
            var type = userManager.GetType();
            var all = type.GetProperty("Users", BindingFlags.Public | BindingFlags.Instance)?.GetValue(userManager)
                ?? type.GetMethod("GetUsers", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)?.Invoke(userManager, null);
            if (all is not IEnumerable users)
            {
                return null;
            }

            var found = new List<(string, bool)>();
            foreach (var user in users)
            {
                var entity = user?.GetType();
                if (entity?.GetProperty("Id")?.GetValue(user) is not Guid id)
                {
                    continue;
                }

                found.Add((id.ToString("N", CultureInfo.InvariantCulture), Open(entity.GetProperty("Password")?.GetValue(user) as string)));
            }

            return found;
        }
        catch (Exception error) when (error is TargetInvocationException or InvalidOperationException or ArgumentException or AmbiguousMatchException or MemberAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a stored password lets in an empty one: none stored, or Jellyfin's « $PBKDF2-SHA512$iterations=N$salt$hash »
    /// (« $PBKDF2$ » = SHA-1, its older one) of an empty password. A form it does not know: false (a password, said safe).
    /// </summary>
    public static bool Open(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return true;
        }

        // 210,000 rounds a try (a tenth of a second): an answer kept for the same stored hash.
        lock (Tried)
        {
            if (Tried.TryGetValue(stored, out var known))
            {
                return known;
            }
        }

        var open = Try(stored);
        lock (Tried)
        {
            if (Tried.Count > 256)
            {
                Tried.Clear();
            }

            Tried[stored] = open;
        }

        return open;
    }

    private static readonly Dictionary<string, bool> Tried = new(StringComparer.Ordinal);

    private static bool Try(string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 5 || parts[0].Length != 0)
        {
            return false;
        }

        var algorithm = parts[1] switch
        {
            "PBKDF2-SHA512" => HashAlgorithmName.SHA512,
            "PBKDF2-SHA256" => HashAlgorithmName.SHA256,
            "PBKDF2" or "PBKDF2-SHA1" => HashAlgorithmName.SHA1,
            _ => default,
        };
        if (algorithm == default || !parts[2].StartsWith("iterations=", StringComparison.Ordinal)
            || !int.TryParse(parts[2].AsSpan("iterations=".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations is < 1 or > 10_000_000)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromHexString(parts[3]);
            var hash = Convert.FromHexString(parts[4]);
            if (hash.Length == 0)
            {
                return false;
            }

            var tried = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(string.Empty), salt, iterations, algorithm, hash.Length);
            return CryptographicOperations.FixedTimeEquals(tried, hash);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
