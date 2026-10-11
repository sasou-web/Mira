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
    /// asks before the new Mira reads the keychain. Not to be called on the UI thread.
    /// </summary>
    public Connection? LoadConnection()
    {
        var path = Path.Combine(DirectoryPath, "session.json");
        if (!File.Exists(path))
        {
            // A sign-out whose token the keychain kept: removed now that it may let go.
            if (File.Exists(Marker(Forget)) && _tokens?.Delete(DirectoryPath) == true) DeleteMarker(Forget);
            return null;
        }
        Connection? saved;
        try { saved = JsonSerializer.Deserialize<Connection>(File.ReadAllBytes(path), Json.Options); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
        if (saved is null) return null;
        if (saved.Token is { Length: > 0 })
        {
            // Refused at a launch of this version (locked keychain, question declined): not asked again at each launch,
            // only at the next sign-in or after an update. Not rewritten this time (disk full): the next launch moves it.
            if (_tokens is null || ReadMarker(Refused) == Version) return saved;
            if (_tokens.Store(DirectoryPath, saved.Token))
            {
                try { Write("session.json", JsonSerializer.SerializeToUtf8Bytes(saved with { Token = "" }, Json.Options)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                Stored(true);
            }
            else Stored(false);
            return saved;
        }
        if (_tokens?.Read(DirectoryPath) is not { Length: > 0 } token) return null;
        // Once per version, stored again by this copy, which the item then trusts: after « Autoriser » (once) rather than
        // « Toujours autoriser », macOS would otherwise ask at every launch until the next sign-in.
        if (ReadMarker(Trusted) != Version) Stored(_tokens.Store(DirectoryPath, token));
        return saved with { Token = token };
    }
    /// <summary>The token in the keychain when it takes it; otherwise in the file with the rest, as before. Not on the UI thread.</summary>
    public void SaveConnection(Connection connection)
    {
        var stored = _tokens?.Store(DirectoryPath, connection.Token) == true;
        Write("session.json", JsonSerializer.SerializeToUtf8Bytes(stored ? connection with { Token = "" } : connection, Json.Options));
        DeleteMarker(Forget);
        if (_tokens is not null) Stored(stored);
    }
    /// <summary>
    /// Off this Mac: the file goes, and the token leaves the keychain; when the keychain keeps it, it is removed at a
    /// later launch. Not on the UI thread.
    /// </summary>
    public void ClearConnection()
    {
        if (_tokens is not null && !_tokens.Delete(DirectoryPath)) WriteMarker(Forget, "");
        DeleteMarker(Trusted);
        var path = Path.Combine(DirectoryPath, "session.json"); if (File.Exists(path)) File.Delete(path);
    }
    // Small files beside session.json: a token to remove from the keychain, the version that stored it last, the version
    // the keychain refused.
    private const string Forget = "keychain-forget", Trusted = "keychain-version", Refused = "keychain-refused";
    private static readonly string Version = typeof(Profile).Assembly.GetName().Version?.ToString() ?? "";
    private string Marker(string name) => Path.Combine(DirectoryPath, name);
    private string? ReadMarker(string name)
    {
        try { return File.Exists(Marker(name)) ? File.ReadAllText(Marker(name)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    private void WriteMarker(string name, string text)
    {
        try { Write(name, System.Text.Encoding.UTF8.GetBytes(text)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private void DeleteMarker(string name)
    {
        try { File.Delete(Marker(name)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private void Stored(bool stored)
    {
        if (stored) { WriteMarker(Trusted, Version); DeleteMarker(Refused); }
        else WriteMarker(Refused, Version);
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
