using System.IO;
using System.Security.Cryptography;
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

    // —— « Tailscale intégré » ——
    /// <summary>The folder of Mira's own Tailscale node: its state, encrypted with <see cref="TailnetKey"/>, and its lock.</summary>
    public string TailnetDirectory => Path.Combine(DirectoryPath, "tailscale");
    /// <summary>
    /// The 32 bytes that encrypt the node's state, its identity on the person's Tailscale network: a keychain item of its
    /// own (« Mira (Tailscale) », the node's folder as its account), else a file of this macOS account only
    /// (tailscale.key) while the keychain does not take it, or without a keychain (the self-check). Made the first time.
    /// Null when the key is there but cannot be read now (keychain locked, question declined) or cannot be kept: another
    /// one would sign the node out for nothing. May wait for the person after an update: never on the UI thread.
    /// </summary>
    public byte[]? TailnetKey()
    {
        // Signed out while the keychain kept the old key: removed now, or never read again (a new key goes to the file).
        var keychain = _tokens;
        if (keychain is not null && File.Exists(Marker(TailnetForget)))
        {
            if (keychain.Delete(TailnetDirectory)) DeleteMarker(TailnetForget);
            else keychain = null;
        }
        // The file, when there, is the key the node uses: the keychain refused it. It moves there once the keychain takes it.
        var file = Marker(TailnetFile);
        if (File.Exists(file))
        {
            byte[]? kept;
            try { kept = Key(File.ReadAllText(file)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            if (kept is not null)
            {
                if (keychain?.Store(TailnetDirectory, Convert.ToBase64String(kept), TailnetLabel) == true)
                {
                    WriteMarker(TailnetTrusted, Version);
                    try { File.Delete(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                return kept;
            }
        }
        else if (keychain is not null)
        {
            var stored = keychain.Read(TailnetDirectory);
            if (stored is null && !keychain.Missing) return null;
            if (Key(stored) is { } key)
            {
                // Once per version, stored again by this copy, which the item then trusts, as the session's token.
                if (ReadMarker(TailnetTrusted) != Version && keychain.Store(TailnetDirectory, stored!, TailnetLabel)) WriteMarker(TailnetTrusted, Version);
                return key;
            }
        }
        // None yet, or one that is not a key (damaged): a new one, and the node signs in again.
        var made = RandomNumberGenerator.GetBytes(32);
        var text = Convert.ToBase64String(made);
        if (keychain?.Store(TailnetDirectory, text, TailnetLabel) == true)
        {
            WriteMarker(TailnetTrusted, Version);
            // A damaged file left there would come first at the next start.
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return made;
        }
        try { Write(TailnetFile, System.Text.Encoding.ASCII.GetBytes(text)); return made; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    /// <summary>
    /// « Se déconnecter de Tailscale »: the node's key and state leave this Mac. A key the keychain keeps is removed at a
    /// later start, and never read again meanwhile. Not on the UI thread.
    /// </summary>
    public void ForgetTailnet()
    {
        if (_tokens is not null && !_tokens.Delete(TailnetDirectory)) WriteMarker(TailnetForget, "");
        DeleteMarker(TailnetTrusted);
        var file = Marker(TailnetFile);
        if (File.Exists(file)) File.Delete(file);
        if (Directory.Exists(TailnetDirectory)) Directory.Delete(TailnetDirectory, recursive: true);
    }
    private const string TailnetFile = "tailscale.key", TailnetForget = "tailscale-keychain-forget", TailnetTrusted = "tailscale-keychain-version", TailnetLabel = "Mira (Tailscale)";
    private static byte[]? Key(string? text)
    {
        var bytes = new byte[32];
        return Convert.TryFromBase64String(text?.Trim() ?? "", bytes, out var length) && length == bytes.Length ? bytes : null;
    }

    /// <summary>True when settings.json exists but could not be read: the defaults are used instead, and replace it on the next save.</summary>
    public bool SettingsUnreadable { get; private set; }
    /// <summary>The copy of that unreadable file kept beside it (settings.json.bad-…); null when it could not be copied either.</summary>
    public string? SettingsCopy { get; private set; }
    /// <summary>The unreadable file is still there and not copied yet: the next save copies it first.</summary>
    private bool _keepBeforeSave;
    /// <summary>
    /// The language chosen in settings.json (<see cref="PlayerSettings.Language"/>), read alone before the settings
    /// themselves, whose defaults depend on it; null when none is chosen or the file cannot be read (LoadSettings says so).
    /// </summary>
    public string? ChosenLanguage()
    {
        try
        {
            using var settings = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(DirectoryPath, "settings.json")));
            return settings.RootElement.ValueKind == JsonValueKind.Object && settings.RootElement.EnumerateObject()
                .FirstOrDefault(x => x.Name.Equals(nameof(PlayerSettings.Language), StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } language ? language.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
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

/// <summary>Where a profile keeps its session token, apart from the rest of session.json, and the key of its Tailscale node.</summary>
public interface ITokenStore
{
    /// <summary>Replaces the token kept for <paramref name="account"/>, named <paramref name="label"/> where the Mac shows it; false when it could not be stored.</summary>
    bool Store(string account, string token, string label = "Mira");
    /// <summary>The token kept for <paramref name="account"/>, or null: none (<see cref="Missing"/>), or it cannot be read.</summary>
    string? Read(string account);
    /// <summary>Removes it; false when it may still be there.</summary>
    bool Delete(string account);
    /// <summary>The last <see cref="Read"/> found nothing for its account, rather than something it could not read (keychain locked, question declined).</summary>
    bool Missing { get; }
}
