using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mira.Core;

namespace Mira.Desktop.Services;

public sealed class LocalProfile
{
    public string DirectoryPath { get; }
    public string DeviceId { get; }
    /// <summary>True when Mira had never opened this profile: no settings and no session yet (read before anything is written).</summary>
    public bool IsNew { get; }
    public LocalProfile(string? path = null)
    {
        DirectoryPath = path ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(DirectoryPath);
        IsNew = !File.Exists(Path.Combine(DirectoryPath, "settings.json")) && !File.Exists(Path.Combine(DirectoryPath, "session.protected"));
        var deviceFile = Path.Combine(DirectoryPath, "device-id");
        var stored = File.Exists(deviceFile) ? File.ReadAllText(deviceFile).Trim() : "";
        // An empty file (cut off while being written) would send Jellyfin an empty device id: start a new one.
        DeviceId = stored.Length > 0 ? stored : Guid.NewGuid().ToString("N");
        if (stored.Length == 0) File.WriteAllText(deviceFile, DeviceId);
    }
    public Connection? LoadConnection()
    {
        var path = Path.Combine(DirectoryPath, "session.protected");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Connection>(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser), Json.Options); }
        // Unreadable (damaged, another Windows account's, locked): signing in again replaces it.
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException) { return null; }
    }
    public void SaveConnection(Connection connection) => AtomicWrite("session.protected",
        ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(connection, Json.Options), null, DataProtectionScope.CurrentUser));
    public void ClearConnection() { var path = Path.Combine(DirectoryPath, "session.protected"); if (File.Exists(path)) File.Delete(path); }
    /// <summary>True when settings.json exists but could not be read: the defaults are used instead, and replace it on the next save.</summary>
    public bool SettingsUnreadable { get; private set; }
    /// <summary>The copy of that unreadable file kept beside it (settings.json.bad-…); null when it could not be copied either.</summary>
    public string? SettingsCopy { get; private set; }
    /// <summary>The unreadable file is still there and not copied yet: the next save copies it first.</summary>
    private bool _keepBeforeSave;
    public PlayerSettings LoadSettings()
    {
        var path = Path.Combine(DirectoryPath, "settings.json");
        try { return JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(path), Json.Options) ?? new(); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Said once by the window, and the file kept: the next save would otherwise replace every setting without a word.
            SettingsUnreadable = _keepBeforeSave = true; KeepUnreadableSettings();
            return new();
        }
    }
    public void SaveSettings(PlayerSettings settings)
    {
        // Held by another program at loading (a sync or antivirus tool), the file could not be copied then: it is now,
        // before being replaced. Still held, it cannot be replaced either.
        if (_keepBeforeSave) KeepUnreadableSettings();
        AtomicWrite("settings.json", JsonSerializer.SerializeToUtf8Bytes(settings, Json.Options)); _keepBeforeSave = false;
    }
    private void KeepUnreadableSettings()
    {
        var path = Path.Combine(DirectoryPath, "settings.json");
        try { var copy = $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}"; File.Copy(path, copy, overwrite: true); SettingsCopy = copy; _keepBeforeSave = false; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
    private void AtomicWrite(string file, byte[] bytes)
    {
        var target = Path.Combine(DirectoryPath, file);
        File.WriteAllBytes(target + ".tmp", bytes); File.Move(target + ".tmp", target, true);
    }
}
