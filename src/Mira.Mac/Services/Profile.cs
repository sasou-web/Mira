using System.IO;
using System.Text.Json;
using Mira.Core;

namespace Mira.Mac.Services;

/// <summary>
/// Mira's files on this Mac: ~/Library/Application Support/Mira (or the folder given with --data). The Jellyfin
/// session is readable by this macOS account only, its token in the login keychain when there is one; the password
/// itself is never stored.
/// </summary>
public sealed class Profile
{
    public string DirectoryPath { get; }
    public string DeviceId { get; }
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly ITokenStore? _tokens;

    /// <param name="tokens">Where the session token goes (the login keychain); null: in session.json with the rest.</param>
    public Profile(string? path = null, ITokenStore? tokens = null)
    {
        DirectoryPath = path ?? DefaultDirectory();
        _tokens = tokens;
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

    /// <summary>
    /// The saved session, or null. A token still in session.json (written by an earlier Mira, or when the keychain refused
    /// it) moves to the keychain and leaves the file once stored there. May wait for the person: after an update, macOS
    /// asks before the new Mira reads the keychain.
    /// </summary>
    public Connection? LoadConnection()
    {
        var path = Path.Combine(DirectoryPath, "session.json");
        if (!File.Exists(path)) return null;
        Connection? saved;
        try { saved = JsonSerializer.Deserialize<Connection>(File.ReadAllBytes(path), Json.Options); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
        if (saved is null) return null;
        if (saved.Token is { Length: > 0 })
        {
            // Not rewritten this time (disk full, file held): the next launch moves it again.
            if (_tokens?.Store(DirectoryPath, saved.Token) == true)
                try { Write("session.json", JsonSerializer.SerializeToUtf8Bytes(saved with { Token = "" }, Json.Options)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return saved;
        }
        return _tokens?.Read(DirectoryPath) is { Length: > 0 } token ? saved with { Token = token } : null;
    }
    /// <summary>The token in the keychain when it takes it; otherwise in the file with the rest, as before.</summary>
    public void SaveConnection(Connection connection) =>
        Write("session.json", JsonSerializer.SerializeToUtf8Bytes(_tokens?.Store(DirectoryPath, connection.Token) == true ? connection with { Token = "" } : connection, Json.Options));
    public void ClearConnection()
    {
        _tokens?.Delete(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "session.json"); if (File.Exists(path)) File.Delete(path);
    }

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

/// <summary>Where a profile keeps its session token, apart from the rest of session.json.</summary>
public interface ITokenStore
{
    /// <summary>Replaces the token kept for <paramref name="account"/>; false when it could not be stored.</summary>
    bool Store(string account, string token);
    /// <summary>The token kept for <paramref name="account"/>, or null: none, or it cannot be read.</summary>
    string? Read(string account);
    /// <summary>Removes it; false when it may still be there.</summary>
    bool Delete(string account);
}
