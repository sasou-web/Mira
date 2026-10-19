using System.Security.Cryptography;
using System.Text;

namespace Mira.Core;

/// <summary>
/// What Mira keeps on this computer for one Jellyfin account, beside its session: the library cache (pages, local
/// progress, reports not sent yet) and the artwork. The settings and the device's identity belong to no account.
/// </summary>
public static class AccountData
{
    /// <summary>The name of an account's library cache (see <see cref="LibraryStore"/>).</summary>
    public static string LibraryProfile(Connection connection) => connection.Server + "|" + connection.UserId;
    /// <summary>The folder of an account's artwork, under the profile's "images" (every account's, trimmed together).</summary>
    public static string ImageFolder(string directory, Connection connection)
    {
        // As its client knows it: JellyfinClient normalizes the server.
        var account = JellyfinClient.NormalizeServer(connection.Server) + connection.UserId;
        return Path.Combine(directory, "images", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account)))[..16]);
    }
    /// <summary>
    /// Deletes an account's library cache and artwork, once its session is closed; other accounts' stay. False when a
    /// file could not be deleted (still in use after a few tries).
    /// </summary>
    public static Task<bool> EraseAsync(string directory, Connection connection) => Task.Run(async () =>
    {
        // A download or a cache write that was finishing as the session closed holds its file for a moment, and Windows
        // deletes no open file.
        for (var attempt = 1; ; attempt++)
        {
            if (Erase(directory, connection)) return true;
            if (attempt == 5) return false;
            await Task.Delay(200).ConfigureAwait(false);
        }
    });
    private static bool Erase(string directory, Connection connection)
    {
        var library = LibraryStore.Delete(directory, LibraryProfile(connection));
        var images = ImageFolder(directory, connection);
        try { if (Directory.Exists(images)) Directory.Delete(images, recursive: true); return library; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    /// <summary>What « Déconnecter » says once done: where the session was closed, and what became of this account's data.</summary>
    /// <param name="computer">« ce PC » or « ce Mac ».</param>
    /// <param name="erased">Null when erasing was not asked.</param>
    public static string SignedOut(string computer, bool closedOnServer, bool? erased) =>
        (closedOnServer ? L($"Session fermée sur {computer} et sur Jellyfin.", $"Signed out on {computer} and on Jellyfin.")
            : L($"Session fermée sur {computer}. Jellyfin n’a pas répondu : elle y reste ouverte tant que tu ne retires pas {computer} de ses appareils (Tableau de bord, Appareils).",
                $"Signed out on {computer}. Jellyfin didn’t answer: the session stays open there until you remove {computer} from its devices (Dashboard, Devices)."))
        + erased switch
        {
            true => L($" Les données de ce compte ont été effacées de {computer}.", $" This account’s data was erased from {computer}."),
            false => L($" Certains fichiers de ce compte, encore utilisés, sont restés sur {computer}.", $" Some of this account’s files, still in use, stayed on {computer}."),
            null => ""
        };
}
