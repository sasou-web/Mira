using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Mira.Desktop.Services;

/// <summary>
/// Locks that keep Mira from starting while its own files change: the one held by the update helper of a copy (named
/// after its profile folder, like the single-instance lock), and Inno Setup's SetupMutex for an installed copy.
/// </summary>
internal static class UpdateLock
{
    /// <summary>Declared as SetupMutex in installer/Mira.iss.</summary>
    public const string SetupMutex = "MiraSetup";
    /// <summary>Key of a profile folder, shared with the single-instance lock of App.</summary>
    public static string Key(string profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(profile).ToUpperInvariant())))[..24];
    public static string Name(string profile) => @"Local\Mira-Update-" + Key(profile);
    public static bool Busy(string profile, bool installer) => Exists(Name(profile)) || installer && Exists(SetupMutex);
    /// <summary>Waits while an update of this copy, or for an installed copy a Mira installer, is running;
    /// false if it still runs after <paramref name="timeout"/>.</summary>
    public static bool WaitIdle(string profile, bool installer, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Busy(profile, installer)) { if (DateTime.UtcNow > deadline) return false; Thread.Sleep(300); }
        return true;
    }
    private static bool Exists(string name)
    {
        try { if (!Mutex.TryOpenExisting(name, out var mutex)) return false; mutex.Dispose(); return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
