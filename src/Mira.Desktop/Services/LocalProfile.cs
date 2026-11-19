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
    /// <summary>The folder of « Tailscale intégré »: its node's state, encrypted with <see cref="TailnetKey"/>.</summary>
    public string TailnetDirectory => Path.Combine(DirectoryPath, "tailscale");
    /// <summary>
    /// The key that encrypts the node's state (its identity on your Tailscale network): 32 random bytes kept with DPAPI
    /// for this Windows account, as the session is. Made the first time; one this account cannot read (another
    /// account's, damaged) is replaced, and the node then signs in again. Null when the file is there but cannot be read
    /// now (held by another program): replacing it would lose the node for nothing.
    /// </summary>
    public byte[]? TailnetKey()
    {
        var path = Path.Combine(DirectoryPath, "tailscale.protected");
        try
        {
            var key = ProtectedData.Unprotect(File.ReadAllBytes(path), TailnetEntropy, DataProtectionScope.CurrentUser);
            if (key.Length == 32) return key;
        }
        catch (Exception ex) when (ex is FileNotFoundException or CryptographicException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var made = RandomNumberGenerator.GetBytes(32);
        AtomicWrite("tailscale.protected", ProtectedData.Protect(made, TailnetEntropy, DataProtectionScope.CurrentUser));
        return made;
    }
    /// <summary>« Se déconnecter de Tailscale » or a new start: the node's key and state leave this PC.</summary>
    public void ForgetTailnet()
    {
        var path = Path.Combine(DirectoryPath, "tailscale.protected");
        if (File.Exists(path)) File.Delete(path);
        if (Directory.Exists(TailnetDirectory)) Directory.Delete(TailnetDirectory, recursive: true);
    }
    /// <summary>Ties the key to this use: DPAPI data of the same account made for something else does not decrypt it.</summary>
    private static readonly byte[] TailnetEntropy = Encoding.UTF8.GetBytes("Mira tailnet state key v1");
    /// <summary>
    /// « Le foyer »: this PC in a home (its home token, where the home's Jellyfin is), kept with DPAPI as the session is;
    /// null when it is in none, or it cannot be read (it then joins again with the home's password).
    /// </summary>
    public Mira.Core.Family.HomeMembership? LoadHome()
    {
        var path = Path.Combine(DirectoryPath, "home.protected");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Mira.Core.Family.HomeMembership>(ProtectedData.Unprotect(File.ReadAllBytes(path), HomeEntropy, DataProtectionScope.CurrentUser), Json.Options); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }
    public void SaveHome(Mira.Core.Family.HomeMembership home) => AtomicWrite("home.protected",
        ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(home, Json.Options), HomeEntropy, DataProtectionScope.CurrentUser));
    public void ClearHome() { var path = Path.Combine(DirectoryPath, "home.protected"); if (File.Exists(path)) File.Delete(path); }
    /// <summary>The folder of the home's own Tailscale node (a home link's), apart from « Tailscale intégré »'s.</summary>
    public string HomeNodeDirectory => Path.Combine(DirectoryPath, "home-tailscale");
    /// <summary>The key of the home's node's state, as <see cref="TailnetKey"/> is for « Tailscale intégré ».</summary>
    public byte[]? HomeNodeKey()
    {
        var path = Path.Combine(DirectoryPath, "home-tailscale.protected");
        try
        {
            var key = ProtectedData.Unprotect(File.ReadAllBytes(path), HomeNodeEntropy, DataProtectionScope.CurrentUser);
            if (key.Length == 32) return key;
        }
        catch (Exception ex) when (ex is FileNotFoundException or CryptographicException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var made = RandomNumberGenerator.GetBytes(32);
        AtomicWrite("home-tailscale.protected", ProtectedData.Protect(made, HomeNodeEntropy, DataProtectionScope.CurrentUser));
        return made;
    }
    /// <summary>The device left the home: its node's key and state leave this PC.</summary>
    public void ForgetHomeNode()
    {
        var path = Path.Combine(DirectoryPath, "home-tailscale.protected");
        if (File.Exists(path)) File.Delete(path);
        if (Directory.Exists(HomeNodeDirectory)) Directory.Delete(HomeNodeDirectory, recursive: true);
    }
    private static readonly byte[] HomeEntropy = Encoding.UTF8.GetBytes("Mira home membership v1");
    private static readonly byte[] HomeNodeEntropy = Encoding.UTF8.GetBytes("Mira home tailnet state key v1");
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
