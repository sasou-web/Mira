using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Desktop.Services;

namespace Mira.Desktop.TorLink;

/// <summary>
/// A TorLink installation: its folder, the Node.js runtime that runs it and its entry script. <see cref="Managed"/>: the
/// one Mira installed in its profile, with its own Node.js.
/// </summary>
public sealed record TorLinkInstallation(string Root, string Node, string Entry, bool Managed = false)
{
    public bool HasRuntime => Node.Length > 0;
    public string? Version => ReadVersion(Root);

    /// <summary>
    /// The configured folder when valid, then Mira's own TorLink, the targets of TorLink's shortcuts, a global npm
    /// install, and the copy "npx torlnk" keeps in npm's cache.
    /// </summary>
    public static TorLinkInstallation? Locate(string? configured, string? profile = null)
    {
        foreach (var candidate in Candidates(configured, profile))
            if (Validate(candidate, profile) is { } found) return found;
        return null;
    }

    private static IEnumerable<string> Candidates(string? configured, string? profile)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured.Trim();
        if (profile is not null) yield return TorLinkPackage.PackageRoot(profile);
        foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory }.Select(Environment.GetFolderPath))
        {
            List<string> links;
            try { links = folder.Length > 0 && Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "torlink*.lnk").ToList() : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { links = []; }
            foreach (var link in links) if (WindowsIdentity.ShortcutTarget(link) is { } target) yield return target;
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "torlnk");
        // "npx torlnk" leaves TorLink in npm's cache, one folder per version fetched: the newest is used.
        string[] npx;
        try
        {
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm-cache", "_npx");
            npx = Directory.Exists(cache) ? Directory.EnumerateDirectories(cache).Select(x => Path.Combine(x, "node_modules", "torlnk")).Where(Directory.Exists).ToArray() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { npx = []; }
        foreach (var root in npx.OrderByDescending(x => System.Version.TryParse(ReadVersion(x) ?? "", out var v) ? v : new System.Version())) yield return root;
    }

    /// <summary>
    /// Accepts TorLink's folder or a file inside it (torlink.bat): package "torlnk" with its built dist/cli.cjs. Its
    /// runtime: a node.exe bundled in that folder, Mira's own Node.js, then one on PATH, each only from Node.js 22 on.
    /// </summary>
    public static TorLinkInstallation? Validate(string path, string? profile = null)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = File.Exists(full) ? Path.GetDirectoryName(full) : full;
            if (root is null || !Directory.Exists(root)) return null;
            var package = Path.Combine(root, "package.json");
            var entry = Path.Combine(root, "dist", "cli.cjs");
            if (!File.Exists(package) || !File.Exists(entry)) return null;
            using (var document = JsonDocument.Parse(File.ReadAllText(package)))
                if (!document.RootElement.TryGetProperty("name", out var name) || name.GetString() != "torlnk") return null;
            var managed = profile is not null && string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(TorLinkPackage.PackageRoot(profile)), StringComparison.OrdinalIgnoreCase);
            var node = new[] { Path.Combine(root, "node", "node.exe"), profile is null ? null : NodeInstaller.Executable(profile), FindOnPath("node.exe") }
                .FirstOrDefault(x => x is not null && Usable(x)) ?? "";
            return new(root, node, entry, managed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// A TorLink started outside Mira (Start menu shortcut, a terminal). Two instances would share the same queue and
    /// write the same files, so Mira does not start a second one.
    /// </summary>
    public int? RunningElsewhere(int? own)
    {
        foreach (var process in Process.GetProcessesByName("node"))
        {
            using (process)
            {
                if (process.Id == own || CommandLine(process.Id) is not { } command) continue;
                if (command.Contains(Entry, StringComparison.OrdinalIgnoreCase) || command.Contains("torlnk", StringComparison.OrdinalIgnoreCase)) return process.Id;
            }
        }
        return null;
    }

    /// <summary>Fallback without WebView2: TorLink in its own console window, like its Start-menu shortcut.</summary>
    public void OpenInWindow() => Process.Start(new ProcessStartInfo(Node, "\"" + Entry + "\"") { WorkingDirectory = Root, UseShellExecute = true })?.Dispose();

    /// <summary>A node.exe recent enough for TorLink, read from its file version (no process is started).</summary>
    private static bool Usable(string node)
    {
        try { return File.Exists(node) && NodeInstaller.MajorVersion(FileVersionInfo.GetVersionInfo(node).ProductVersion) >= NodeInstaller.MinimumMajor; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
    private static string? ReadVersion(string root)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package.json")));
            return document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return null; }
    }

    private static string? FindOnPath(string file)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var candidate = Path.Combine(folder.Trim().Trim('"'), file); if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate; }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static string? CommandLine(int processId)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var size);
            if (size <= 0 || size > 1 << 20) return null;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, size, out _) != 0) return null;
                var text = Marshal.PtrToStructure<UnicodeString>(buffer);
                return text.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(handle); }
    }

    private const int QueryLimitedInformation = 0x1000, ProcessCommandLineInformation = 60;
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int processId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returnLength);
}
