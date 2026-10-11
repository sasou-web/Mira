using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Mira.Desktop.Services;

/// <summary>
/// The portable Mira is one file: .NET extracts its native libraries at its first start, into
/// <c>%TEMP%\.net\&lt;its name&gt;\&lt;one folder per build&gt;</c>, and never removes them. Each update of the portable
/// leaves one more (about 30 MB): the folders of other builds go once no other Mira runs.
/// </summary>
internal static partial class PortableExtraction
{
    /// <summary>
    /// Removes the extraction folders of other builds of the portable Mira, beside this one's. Nothing when this Mira is
    /// not a single file, when its folder is not where .NET extracts (<c>.net</c>, or DOTNET_BUNDLE_EXTRACT_BASE_DIR), or
    /// while another Mira runs: its libraries may still be loading from its own folder. Best effort.
    /// </summary>
    public static void CleanOld()
    {
        try
        {
            if (Current() is not { } current || Path.GetDirectoryName(current) is not { } app || Path.GetDirectoryName(app) is not { } extractions) return;
            if (OtherMiraRuns()) return;
            foreach (var name in Directory.EnumerateDirectories(extractions).Where(x => PortableName().IsMatch(Path.GetFileName(x))))
            {
                foreach (var build in Directory.EnumerateDirectories(name))
                    if (!string.Equals(build, current, StringComparison.OrdinalIgnoreCase)) TryDelete(build);
                if (!string.Equals(name, app, StringComparison.OrdinalIgnoreCase) && !Directory.EnumerateFileSystemEntries(name).Any()) TryDelete(name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    /// <summary>
    /// This build's extraction folder: among the folders .NET loads native libraries from, and those of the libraries
    /// loaded, the one inside the extraction folder of a portable Mira. Null for an installed or unpacked Mira.
    /// </summary>
    private static string? Current()
    {
        // As Updater tells it: one file has no Mira.dll beside it.
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "Mira.dll"))) return null;
        var root = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") is { Length: > 0 } custom ? custom : Path.Combine(Path.GetTempPath(), ".net");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var searched = (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        // WPF's own native libraries are loaded from there by now.
        using (var self = Process.GetCurrentProcess())
            searched.AddRange(self.Modules.Cast<ProcessModule>().Select(x => Path.GetDirectoryName(x.FileName)).OfType<string>());
        return searched.Select(x => Path.TrimEndingDirectorySeparator(Path.GetFullPath(x)))
            .FirstOrDefault(x => Path.GetDirectoryName(x) is { } app && PortableName().IsMatch(Path.GetFileName(app))
                && string.Equals(Path.GetDirectoryName(app), root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Another process whose name is Mira's (« Mira », « Mira-0.7.4-win-x64-portable »).</summary>
    private static bool OtherMiraRuns()
    {
        var self = Environment.ProcessId;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { if (process.Id != self && PortableName().IsMatch(process.ProcessName)) return true; }
                catch (InvalidOperationException) { }
            }
        }
        return false;
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The names .NET gives the portable's folder: its file's, as published (or renamed back to Mira).</summary>
    [GeneratedRegex(@"^Mira(-\d+\.\d+\.\d+-win-x64-portable)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PortableName();
}
