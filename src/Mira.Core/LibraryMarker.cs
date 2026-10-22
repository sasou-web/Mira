namespace Mira.Core;

/// <summary>
/// Jellyfin leaves a library alone while its folder is empty (« Library folder … is inaccessible or empty, skipping »,
/// so that an unplugged drive does not empty it): once the last title is deleted from Films, Séries or Animes, that
/// title stays on show for good, a full scan included. A small hidden file kept in each library folder makes it never
/// empty, so Jellyfin removes deleted titles as it should. It lives on the same drive as the media: an unplugged drive
/// takes it along, and Jellyfin's protection still holds. Jellyfin counts hidden files and makes no title of this one.
/// </summary>
public static class LibraryMarker
{
    public const string FileName = ".mira";

    private const string Text = """
        Mira garde ce fichier pour que Jellyfin voie ce dossier même vide : sans lui, Jellyfin n'efface pas de la
        bibliothèque le dernier titre supprimé d'ici. Laisse-le.

        Mira keeps this file so that Jellyfin sees this folder even when empty: without it, Jellyfin doesn't remove
        the last title deleted from here from the library. Leave it.
        """;

    /// <summary>
    /// Puts the file in <paramref name="folder"/> when it is missing. True when it was put in a folder that held nothing
    /// else: Jellyfin may still show titles deleted from it, and a scan now removes them. False otherwise, and for a folder
    /// absent from this computer (a drive unplugged, another computer's path) or that may not be written.
    /// </summary>
    public static bool Ensure(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return false;
            var file = Path.Combine(folder, FileName);
            if (File.Exists(file)) return false;
            var empty = !Directory.EnumerateFileSystemEntries(folder).Any();
            File.WriteAllText(file, Text.ReplaceLineEndings("\r\n") + "\r\n");
            if (OperatingSystem.IsWindows()) File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.Hidden);
            return empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
