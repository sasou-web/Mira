using System.Runtime.InteropServices;
using System.Text;
using static Mira.Core.Lang;

namespace Mira.Mac.Services;

/// <summary>
/// mira-tailscale, the node of « Tailscale intégré » (src/Mira.Tailscale): inside Mira.app beside Mira
/// (Contents/MacOS), built for its version and signed with the rest of the bundle by tools/mac/package.sh. A helper of
/// another version says so in its first line, and Mira does not use it (TailnetProcess).
/// </summary>
internal static partial class TailnetExecutable
{
    public const string Name = "mira-tailscale";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    /// <summary>The flag macOS adds to a download's quarantine once the person let it open (« Ouvrir quand même »).</summary>
    private const int Approved = 0x40;

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)] private static partial nint getxattr(string path, string name, [Out] byte[]? value, nuint size, uint position, int options);

    /// <summary>The executable, or why there is none, in words.</summary>
    public static (string? Path, string? Problem) Locate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, Name);
        return File.Exists(path) ? (path, null)
            : (null, L("mira-tailscale, le Tailscale intégré de Mira, manque dans Mira.app : réinstalle Mira depuis son image disque.",
                "mira-tailscale, Mira’s Built-in Tailscale, is missing from Mira.app: reinstall Mira from its disk image."));
    }

    /// <summary>
    /// Why a helper that never got to its first line could not, when macOS says it: Gatekeeper still holds it as a download
    /// nobody let open (its quarantine mark without the « approved » flag), as it may after the app itself was allowed. Null
    /// otherwise: the helper's own failure is then said as it is. <paramref name="path"/>: another copy (the self-check's).
    /// </summary>
    public static string? Blocked(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, Name);
        if (!OperatingSystem.IsMacOS() || Quarantine(path) is not { } mark) return null;
        var flags = mark.Split(';')[0];
        if (int.TryParse(flags, System.Globalization.NumberStyles.HexNumber, null, out var value) && (value & Approved) != 0) return null;
        return L($"macOS bloque mira-tailscale, le Tailscale intégré de Mira, comme tout programme téléchargé qu’il ne peut pas vérifier. Pour l’autoriser, tape dans le Terminal la commande ci-dessous, puis relance Mira : {Command}",
            $"macOS blocks mira-tailscale, Mira’s Built-in Tailscale, as it does any downloaded program it can’t check. To allow it, type the command below in Terminal, then open Mira again: {Command}");
    }

    /// <summary>What lifts the « downloaded from the Internet » mark off Mira, as docs/INSTALLATION-MAC.md says it for the app itself.</summary>
    public static string Command => $"xattr -dr com.apple.quarantine \"{Bundle}\"";
    /// <summary>
    /// Mira.app where the person keeps it: the bundle around this executable, unless macOS runs it from a copy of its own
    /// (« App Translocation », for an app opened where it was downloaded), where the command would change nothing.
    /// </summary>
    private static string Bundle
    {
        get
        {
            var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
            return folder.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !folder.Contains("/AppTranslocation/", StringComparison.Ordinal) ? folder : "/Applications/Mira.app";
        }
    }

    /// <summary>The file's com.apple.quarantine attribute (« 0083;65f0b3a1;Safari;… »), null when it has none.</summary>
    private static string? Quarantine(string path)
    {
        try
        {
            var size = getxattr(path, "com.apple.quarantine", null, 0, 0, 0);
            if (size <= 0) return null;
            var value = new byte[size];
            var read = getxattr(path, "com.apple.quarantine", value, (nuint)value.Length, 0, 0);
            return read > 0 ? Encoding.UTF8.GetString(value, 0, (int)read) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
}
