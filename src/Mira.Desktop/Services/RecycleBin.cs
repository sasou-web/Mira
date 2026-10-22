using System.IO;
using System.Runtime.InteropServices;

namespace Mira.Desktop.Services;

/// <summary>
/// « Mettre à la Corbeille » (<see cref="Mira.Core.TitleFiles"/>): through the Shell, as the Explorer does, so that the
/// title can be put back. Where Windows cannot keep it (too large for the Recycle Bin, a drive without one), it asks
/// before deleting for good; a file in use gets its own dialog.
/// </summary>
internal static class RecycleBin
{
    private const uint Delete = 3;
    private const ushort AllowUndo = 0x40, NoConfirmation = 0x10, WantNukeWarning = 0x4000;

    /// <summary>True once <paramref name="path"/> is no longer there; false when refused, cancelled or only partly done.</summary>
    public static bool Send(string path, IntPtr owner)
    {
        // A list of paths, each ended by a null, the whole by another.
        var operation = new FileOperation { Owner = owner, Function = Delete, From = path + "\0\0", Flags = AllowUndo | NoConfirmation | WantNukeWarning };
        var result = SHFileOperation(ref operation);
        return result == 0 && !operation.AnyAborted && !File.Exists(path) && !Directory.Exists(path);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public IntPtr Owner;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref FileOperation operation);
}
