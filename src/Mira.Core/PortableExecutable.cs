using System.Reflection.PortableExecutable;

namespace Mira.Core;

/// <summary>What a Windows program or library (a PE file) is built for, read from its headers without loading it.</summary>
public static class PortableExecutable
{
    /// <summary>
    /// True when <paramref name="path"/> is a PE file for x64, the only kind Mira (an x64 process) can load: a 32-bit
    /// mpv (Jellyfin MPV Shim's older builds, in Program Files (x86)) or an ARM64 one is passed over, as is a file that
    /// cannot be read or is no PE file at all.
    /// </summary>
    public static bool IsX64(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new PEHeaders(stream).CoffHeader.Machine == Machine.Amd64;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
}
