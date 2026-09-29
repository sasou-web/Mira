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
    public LocalProfile(string? path = null)
    {
        DirectoryPath = path ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(DirectoryPath);
        var deviceFile = Path.Combine(DirectoryPath, "device-id");
        DeviceId = File.Exists(deviceFile) ? File.ReadAllText(deviceFile).Trim() : Guid.NewGuid().ToString("N");
        if (!File.Exists(deviceFile)) File.WriteAllText(deviceFile, DeviceId);
    }
    public Connection? LoadConnection()
    {
        var path = Path.Combine(DirectoryPath, "session.protected");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Connection>(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser), Json.Options); }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { return null; }
    }
    public void SaveConnection(Connection connection) => AtomicWrite("session.protected",
        ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(connection, Json.Options), null, DataProtectionScope.CurrentUser));
    public void ClearConnection() { var path = Path.Combine(DirectoryPath, "session.protected"); if (File.Exists(path)) File.Delete(path); }
    public PlayerSettings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(Path.Combine(DirectoryPath, "settings.json")), Json.Options) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }
    public void SaveSettings(PlayerSettings settings) => AtomicWrite("settings.json", JsonSerializer.SerializeToUtf8Bytes(settings, Json.Options));
    private void AtomicWrite(string file, byte[] bytes)
    {
        var target = Path.Combine(DirectoryPath, file);
        File.WriteAllBytes(target + ".tmp", bytes); File.Move(target + ".tmp", target, true);
    }
}
