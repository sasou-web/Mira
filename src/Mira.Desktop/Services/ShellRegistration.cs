using System.IO;

namespace Mira.Desktop.Services;

/// <summary>Which copy of Mira the Start menu opens. No Windows call here: tested on any system.</summary>
internal static class ShellRegistration
{
    /// <summary>
    /// The installed copy always takes the Start menu shortcut and Mira's Windows identity (Setup made it the Mira of this
    /// PC). Another copy (portable, zip, <c>dist\Mira</c>) only when there is no shortcut or when its program is gone,
    /// so that the Start menu never opens another copy, with another profile, than the one it opened before. Never a
    /// copy in the temporary folder: a zip opened without extracting it, gone once closed.
    /// </summary>
    public static bool ShouldRegister(string executable, bool installed, string temporaryFolder, bool shortcutExists, string? shortcutTarget, Func<string, bool> fileExists)
    {
        if (IsInside(executable, temporaryFolder)) return false;
        if (installed) return true;
        return !shortcutExists || string.IsNullOrWhiteSpace(shortcutTarget) || !fileExists(shortcutTarget);
    }
    private static bool IsInside(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
