using System.IO;
using System.Runtime.InteropServices;

namespace Mira.Desktop.Services;

/// <summary>
/// « Mettre à la Corbeille » (<see cref="Mira.Core.TitleFiles"/>): through the Shell, as the Explorer does, so that the
/// title can be put back; never deleted for good. Where Windows could not keep an item (a drive without a Recycle Bin, a
/// folder larger than the Recycle Bin, a Recycle Bin set to delete at once), it would delete it instead: Mira stops the
/// operation there (<see cref="Sink.PreDeleteItem"/>), and asks first whether the drive has one (<see cref="Available"/>).
/// A file in use gets the Shell's own dialog.
/// </summary>
internal static class RecycleBin
{
    /// <summary>What happened: all gone to the Recycle Bin, stopped where it would have been deleted for good, or refused, cancelled or only partly done.</summary>
    public enum Outcome { Recycled, NotKept, Failed }

    /// <summary>
    /// True when the drive of <paramref name="path"/> has a Recycle Bin: a fixed drive (an internal disk or a hard disk in
    /// USB) that the Shell can count the Recycle Bin of. A removable stick, a network share or an optical disc: false.
    /// </summary>
    public static bool Available(string path)
    {
        // The volume itself: a disk mounted in a folder (« C:\Users\…\MEDIAS ») is not C:'s.
        var volume = new char[1024];
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || !GetVolumePathName(path, volume, volume.Length)) return false;
        var root = new string(volume).TrimEnd('\0');
        if (GetDriveType(root) != FixedDrive) return false;
        // Its size given at the start: 24 bytes in 64 bits, 20 where Windows packs it tighter.
        foreach (var size in new[] { 24, 20 })
        {
            var info = new RecycleBinInfo { Size = size };
            if (SHQueryRecycleBin(root, ref info) == 0) return true;
        }
        return false;
    }

    /// <summary>
    /// True once <paramref name="path"/> is in the Recycle Bin; false when refused, cancelled, only partly done, or when
    /// Windows could only have deleted it for good. <paramref name="quiet"/>: no dialog for a file in use (« Remplacer
    /// par une meilleure version » tries again later).
    /// </summary>
    public static bool Send(string path, IntPtr owner, bool quiet = false) => Send([path], owner, quiet) == Outcome.Recycled;

    /// <summary>Several at once (a season, a film in several files), as one operation of the Explorer: Recycled once none is there.</summary>
    public static Outcome Send(IReadOnlyList<string> paths, IntPtr owner, bool quiet = false)
    {
        var sink = new Sink();
        IFileOperation? operation = null;
        try
        {
            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"), true)!)!;
            operation.SetOperationFlags(AllowUndo | NoConfirmation | RecycleOnDelete | (quiet ? NoErrorUi : 0));
            if (owner != IntPtr.Zero) operation.SetOwnerWindow(owner);
            foreach (var path in paths)
            {
                if (!File.Exists(path) && !Directory.Exists(path)) continue;
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
                operation.DeleteItem(item, sink);
                Marshal.ReleaseComObject(item);
            }
            // An error HRESULT here is the operation stopped (by the sink, the user or a file in use): what is left is checked below.
            operation.PerformOperations();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or FileNotFoundException or UnauthorizedAccessException) { }
        finally { if (operation is not null) Marshal.ReleaseComObject(operation); }
        if (paths.All(x => !File.Exists(x) && !Directory.Exists(x))) return Outcome.Recycled;
        return sink.WouldDelete ? Outcome.NotKept : Outcome.Failed;
    }

    private const uint AllowUndo = 0x40, NoConfirmation = 0x10, NoErrorUi = 0x400, RecycleOnDelete = 0x80000;
    private const uint RecycleIfPossible = 0x80;
    private const int Ok = 0, Abort = unchecked((int)0x80004004);

    /// <summary>Told before each item goes: one Windows could not keep in the Recycle Bin stops everything left.</summary>
    [ComVisible(true)]
    private sealed class Sink : IFileOperationProgressSink
    {
        public bool WouldDelete { get; private set; }
        public int PreDeleteItem(uint flags, IShellItem item)
        {
            if ((flags & RecycleIfPossible) != 0) return Ok;
            WouldDelete = true;
            return Abort;
        }
        public int StartOperations() => Ok;
        public int FinishOperations(int result) => Ok;
        public int PreRenameItem(uint flags, IShellItem item, string? name) => Ok;
        public int PostRenameItem(uint flags, IShellItem item, string? name, int result, IShellItem? created) => Ok;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? name) => Ok;
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? created) => Ok;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? name) => Ok;
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? created) => Ok;
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created) => Ok;
        public int PreNewItem(uint flags, IShellItem destination, string? name) => Ok;
        public int PostNewItem(uint flags, IShellItem destination, string? name, string? template, uint attributes, int result, IShellItem? created) => Ok;
        public int UpdateProgress(uint total, uint done) => Ok;
        public int ResetTimer() => Ok;
        public int PauseTimer() => Ok;
        public int ResumeTimer() => Ok;
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem { }

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        uint Advise(IFileOperationProgressSink sink);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        uint NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, IFileOperationProgressSink? sink);
        void PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
    }

    [ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IShellItem? created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IShellItem? created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IShellItem? created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, [MarshalAs(UnmanagedType.LPWStr)] string? template, uint attributes, int result, IShellItem? created);
        [PreserveSig] int UpdateProgress(uint total, uint done);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    private const uint FixedDrive = 3;
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct RecycleBinInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr context, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string root, ref RecycleBinInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumePathNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string path, [Out] char[] volume, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDriveTypeW")]
    private static extern uint GetDriveType(string root);
}
