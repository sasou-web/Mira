using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    /// Downloads the installer into the profile, checks its SHA-256, runs it silently (Windows asks for the administrator's
    /// consent) and deletes it. The silent installer sets Jellyfin up as a service that starts with Windows.
    /// <paramref name="progress"/> covers the download; it reaches 1 when Windows asks for that consent.
    /// </summary>
    public static async Task InstallAsync(string profile, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var package = JellyfinServerPackage.Current;
        var folder = Path.Combine(profile, "downloads"); Directory.CreateDirectory(folder);
        var setup = Path.Combine(folder, package.FileName); var partial = setup + ".partial";
        try
        {
            await VerifiedDownload.DownloadAsync(package.Url, partial, package.Sha256, null, JellyfinServerPackage.MaximumSize, "Jellyfin", progress: progress, ct: ct);
            File.Move(partial, setup, overwrite: true); progress?.Report(1);
            Process? process;
            try { process = Process.Start(new ProcessStartInfo(setup, "/S") { UseShellExecute = true, Verb = "runas" }); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new InstallException("Installation annulée : Windows n’a pas reçu l’autorisation d’installer Jellyfin."); }
            catch (Win32Exception) { throw new InstallException("L’installateur de Jellyfin n’a pas pu démarrer."); }
            using (process)
            {
                if (process is null) throw new InstallException("L’installateur de Jellyfin n’a pas pu démarrer.");
                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0) throw new InstallException($"L’installateur de Jellyfin s’est arrêté sans terminer (code {process.ExitCode}).");
            }
        }
        finally { TryDelete(partial); TryDelete(setup); }
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
}
