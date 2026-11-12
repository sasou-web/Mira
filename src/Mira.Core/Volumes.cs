using System.Runtime.InteropServices;

namespace Mira.Core;

/// <summary>
/// The volume a path is on, mount points followed. A drive mounted in a folder (« C:\Users\Toi\Videos\MEDIAS »,
/// a NTFS mount point without a letter) has its own root there: <see cref="Path.GetPathRoot(string)"/> would say « C:\ »
/// for it and for the system drive alike, and a move between the two would look like a rename while it copies every byte.
/// </summary>
public static class Volumes
{
    /// <summary>
    /// The root of the volume holding <paramref name="path"/>, with its trailing separator (« C:\ », « C:\Users\Toi\Videos\MEDIAS\ »),
    /// for a path that need not exist yet (its nearest existing folder decides). Outside Windows, the path root. Null when it cannot be told.
    /// </summary>
    public static string? Of(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!OperatingSystem.IsWindows()) return Path.GetPathRoot(full);
            var existing = full;
            while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { Length: > 0 } parent) existing = parent;
            var buffer = new char[32_768];
            return GetVolumePathName(existing, buffer, (uint)buffer.Length) && Array.IndexOf(buffer, '\0') is > 0 and var end
                ? new string(buffer, 0, end) : Path.GetPathRoot(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Two roots of <see cref="Of"/> name the same volume (case and trailing separator aside).</summary>
    public static bool Same(string? a, string? b) => a is { Length: > 0 } && b is { Length: > 0 }
        && string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>A volume root with exactly one trailing separator (« D:\ » stays « D:\ », a mount point gets one).</summary>
    public static string Folder(string root) => Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

    /// <summary>Both paths are on one volume (<see cref="Of"/>); false when either cannot be told.</summary>
    public static bool Shared(string a, string b, Func<string, string?>? volumeOf = null) => Same((volumeOf ?? Of)(a), (volumeOf ?? Of)(b));

    /// <summary>
    /// The bytes this account may still write on the volume holding <paramref name="path"/> (its quota counted, as the
    /// Explorer shows), for a path that need not exist yet: its nearest existing folder decides, mount points followed. A disk
    /// mounted in a folder (« C:\Users\Toi\Videos\MEDIAS », « /Volumes/Disque » on a Mac) has its own free space: the path
    /// root's would be the system disk's. Null when it cannot be told.
    /// </summary>
    public static long? FreeSpace(string path)
    {
        try
        {
            var existing = Path.GetFullPath(path);
            while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { Length: > 0 } parent) existing = parent;
            // Outside Windows, DriveInfo asks statvfs about the folder itself; on Windows it only takes a drive letter.
            if (!OperatingSystem.IsWindows()) return new DriveInfo(existing).AvailableFreeSpace;
            return GetDiskFreeSpaceEx(Folder(existing), out var free, out _, out _) ? (long)free : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException) { return null; }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, [Out] char[] volumePathName, uint bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);
}
