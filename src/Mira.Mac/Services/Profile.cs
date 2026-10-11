using System.Text.Json;
using Mira.Core;

namespace Mira.Mac.Services;

/// <summary>
/// Mira's files on this Mac: ~/Library/Application Support/Mira (or the folder given with --data). The Jellyfin
/// session is readable by this macOS account only; the password itself is never stored.
/// </summary>
public sealed class Profile
{
    public string DirectoryPath { get; }
    public string DeviceId { get; }
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public Profile(string? path = null)
    {
        DirectoryPath = path ?? DefaultDirectory();
        Directory.CreateDirectory(DirectoryPath);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(DirectoryPath, Private | UnixFileMode.UserExecute);
        var deviceFile = Path.Combine(DirectoryPath, "device-id");
        var stored = File.Exists(deviceFile) ? File.ReadAllText(deviceFile).Trim() : "";
        // An empty file (cut off while being written) would send Jellyfin an empty device id: start a new one.
        DeviceId = stored.Length > 0 ? stored : Guid.NewGuid().ToString("N");
        if (stored.Length == 0) File.WriteAllText(deviceFile, DeviceId);
    }

    public static string DefaultDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "Application Support", "Mira")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mira");
    }

    public Connection? LoadConnection()
    {
        var path = Path.Combine(DirectoryPath, "session.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Connection>(File.ReadAllBytes(path), Json.Options) is { Token.Length: > 0 } connection ? connection : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    public void SaveConnection(Connection connection) => Write("session.json", JsonSerializer.SerializeToUtf8Bytes(connection, Json.Options));
    public void ClearConnection() { var path = Path.Combine(DirectoryPath, "session.json"); if (File.Exists(path)) File.Delete(path); }

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
        // Unreadable and not copied at loading (no rights on it, a full disk): copied now if it can be, before being replaced.
        if (_keepBeforeSave) KeepUnreadableSettings();
        Write("settings.json", JsonSerializer.SerializeToUtf8Bytes(settings, Json.Options)); _keepBeforeSave = false;
    }
    private void KeepUnreadableSettings()
    {
        var path = Path.Combine(DirectoryPath, "settings.json");
        try { var copy = $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}"; File.Copy(path, copy, overwrite: true); SettingsCopy = copy; _keepBeforeSave = false; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Replaces a file in one step, private to this account from its creation.</summary>
    private void Write(string file, byte[] bytes)
    {
        var target = Path.Combine(DirectoryPath, file);
        var temporary = target + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = Private;
        using (var stream = new FileStream(temporary, options)) stream.Write(bytes);
        File.Move(temporary, target, true);
    }
}
