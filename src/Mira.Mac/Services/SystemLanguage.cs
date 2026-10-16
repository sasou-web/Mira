using System.Globalization;
using System.Runtime.InteropServices;
using Mira.Core;

namespace Mira.Mac.Services;

/// <summary>
/// Mira's language on this Mac (<see cref="Lang"/>): the one chosen in its settings, else French when French is the first
/// of the Mac's preferred languages (System Settings → General → Language and Region), else English. Read from
/// CFLocaleCopyPreferredLanguages: .NET's culture comes from NSLocale, whose language follows the localizations the
/// app declares rather than the person's own list.
/// </summary>
public static partial class SystemLanguage
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Encoding = 0x08000100; // kCFStringEncodingUTF8

    [LibraryImport(CoreFoundation)] private static partial IntPtr CFLocaleCopyPreferredLanguages();
    [LibraryImport(CoreFoundation)] private static partial nint CFArrayGetCount(IntPtr array);
    [LibraryImport(CoreFoundation)] private static partial IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [LibraryImport(CoreFoundation)][return: MarshalAs(UnmanagedType.U1)] private static partial bool CFStringGetCString(IntPtr text, byte[] buffer, nint size, uint encoding);
    [LibraryImport(CoreFoundation)] private static partial void CFRelease(IntPtr value);

    /// <summary>Sets Mira's language from the one chosen in its settings and the Mac's, before any window shows.</summary>
    public static void Apply(string? chosen) => Lang.Use(Lang.Resolve(chosen, null, Preferred()), CultureInfo.CurrentCulture);

    /// <summary>The first of the Mac's preferred languages (« fr-FR », « en-GB »…); elsewhere, or unreadable, .NET's own.</summary>
    public static CultureInfo Preferred()
    {
        if (OperatingSystem.IsMacOS() && First() is { } tag)
        {
            try { return CultureInfo.GetCultureInfo(tag); }
            catch (CultureNotFoundException) { }
        }
        return CultureInfo.CurrentUICulture;
    }

    /// <summary>Mira.app when Mira runs from it (not from a build folder): the bundle macOS opens again.</summary>
    public static string? Bundle()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        // Mira.app/Contents/MacOS/Mira.
        var folder = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/'));
        return folder is { Name: "MacOS", Parent: { Name: "Contents", Parent: { } app } } && app.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? app.FullName : null;
    }
    /// <summary>
    /// Opens Mira.app again once this Mira has quit: a shell waits for this process to end, then asks macOS to open the
    /// bundle, both passed as arguments rather than written into the command. False when it could not start.
    /// </summary>
    public static bool RelaunchAfterExit(string bundle)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            foreach (var argument in new[] { "-c", "while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; exec /usr/bin/open \"$2\"", "mira-relaunch", Environment.ProcessId.ToString(CultureInfo.InvariantCulture), bundle })
                start.ArgumentList.Add(argument);
            using var shell = System.Diagnostics.Process.Start(start);
            return shell is not null;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static string? First()
    {
        try
        {
            var list = CFLocaleCopyPreferredLanguages();
            if (list == IntPtr.Zero) return null;
            try
            {
                if (CFArrayGetCount(list) < 1) return null;
                var buffer = new byte[128];
                return CFStringGetCString(CFArrayGetValueAtIndex(list, 0), buffer, buffer.Length, Encoding)
                    ? System.Text.Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0) is var end and >= 0 ? end : buffer.Length) : null;
            }
            finally { CFRelease(list); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
}
