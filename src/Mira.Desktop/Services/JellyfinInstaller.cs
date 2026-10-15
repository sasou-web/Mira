using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using Mira.Core;

namespace Mira.Desktop.Services;

/// <summary>Jellyfin Server installed on this PC by its official installer, run silently with the administrator rights it asks for.</summary>
internal static class JellyfinInstaller
{
    /// <summary>
    /// True when Jellyfin is installed on this PC, running or not: its installer's registry key, or its service. Mira
    /// never runs the installer over an existing Jellyfin, whose library and settings are someone's.
    /// </summary>
    public static bool IsInstalled() => InstalledFolder() is not null || ServiceExists();
    /// <summary>Where Jellyfin's installer put it, from the registry key it writes; null when it is not installed.</summary>
    public static string? InstalledFolder() => Setting("InstallFolder") is { } folder && Directory.Exists(folder) ? folder : null;
    /// <summary>Jellyfin's data folder, where its <c>log</c> folder is: the installer's choice, or its default.</summary>
    public static string DataFolder() => Setting("DataFolder") is { Length: > 0 } folder ? folder
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Jellyfin", "Server");
    /// <summary>
    /// A value of <c>HKLM\Software\Jellyfin\Server</c>. Its installer is a 32-bit program, so the key is under
    /// WOW6432Node; the 64-bit view is read too, in case a later installer writes there.
    /// </summary>
    private static string? Setting(string name)
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(@"Software\Jellyfin\Server");
                if (key?.GetValue(name) is string value && value.Length > 0) return value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return null;
    }
    private static bool ServiceExists()
    {
        try { using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\JellyfinServer"); return key is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
    /// <summary>
    /// Downloads the installer into the profile, checks its SHA-256, again once held open, and starts it silently
    /// (Windows asks for the administrator's consent). The silent installer sets Jellyfin up as a service that starts
    /// with Windows. <paramref name="progress"/> covers the download; it reaches 1 when Windows asks for that consent.
    /// The installer is deleted once it ends: its exit code can report a failure while Jellyfin did install, and its
    /// last step can wait on a message box while Jellyfin runs (see <see cref="JellyfinSetup.AwaitInstallerAsync"/>).
    /// </summary>
    public static async Task<RunningInstaller> StartAsync(string profile, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var package = JellyfinServerPackage.Current;
        var folder = Path.Combine(profile, "downloads"); Directory.CreateDirectory(folder);
        var setup = Path.Combine(folder, package.FileName); var partial = setup + ".partial";
        try
        {
            await VerifiedDownload.DownloadAsync(package.Url, partial, package.Sha256, null, JellyfinServerPackage.MaximumSize, "Jellyfin", progress: progress, ct: ct);
            File.Move(partial, setup, overwrite: true);
            Process? process;
            // Held open, sharing reads only, from its check to its start: no program of this account can change or
            // replace the file Windows then runs with administrator rights.
            await using (var held = new FileStream(setup, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            {
                if (!VerifiedDownload.Matches(await SHA256.HashDataAsync(held, ct), package.Sha256))
                    throw new InstallException("Le fichier téléchargé ne correspond pas à la version vérifiée par Mira : il n’est pas installé.");
                progress?.Report(1);
                try { process = Process.Start(new ProcessStartInfo(setup, "/S") { UseShellExecute = true, Verb = "runas" }); }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new InstallException("Installation annulée : Windows n’a pas reçu l’autorisation d’installer Jellyfin."); }
                catch (Win32Exception) { throw new InstallException("L’installateur de Jellyfin n’a pas pu démarrer."); }
            }
            if (process is null) throw new InstallException("L’installateur de Jellyfin n’a pas pu démarrer.");
            return new(process, EndAsync(process, partial, setup));
        }
        catch { TryDelete(partial); TryDelete(setup); throw; }
    }
    private static async Task<int> EndAsync(Process process, string partial, string setup)
    {
        using (process)
        {
            try { await process.WaitForExitAsync(); return process.ExitCode; }
            finally { TryDelete(partial); TryDelete(setup); }
        }
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
}

/// <summary>Jellyfin's installer while it runs with administrator rights: its exit code once it ends, and its window.</summary>
internal sealed class RunningInstaller(Process process, Task<int> exited)
{
    private readonly int _id = process.Id;
    public Task<int> Exited => exited;
    public bool Running => !exited.IsCompleted;
    /// <summary>Silent, it shows no window but a message box: an error, or that it could not start the service.</summary>
    public bool HasWindow => Running && Window() != IntPtr.Zero;
    /// <summary>
    /// Brings that message box in front of Mira, so that it can be answered: allowed while Mira is the window in front,
    /// and only moved, never answered. False when there is none.
    /// </summary>
    public bool BringForward() => Window() is var window && window != IntPtr.Zero && SetForegroundWindow(window);
    private IntPtr Window()
    {
        var found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowThreadProcessId(window, out var owner) == 0 || owner != _id) return true;
            found = window; return false;
        }, IntPtr.Zero);
        return found;
    }
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
