using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace Mira.Desktop.Services;

/// <summary>One stable Shell identity, shared by the window, shortcuts and media session.</summary>
internal static class WindowsIdentity
{
    public const string AppId = "Mira.Desktop";
    // A distinct asset path bypasses the old executable icon cached by Explorer.
    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "mira-mark-v1.ico");
    public static string? RegistrationError { get; private set; }

    public static void SetProcessIdentity(string id) => SetCurrentProcessExplicitAppUserModelID(id);

    public static void Register()
    {
        try
        {
            var executable = Environment.ProcessPath!;
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId))
            {
                key.SetValue("DisplayName", "Mira");
                key.SetValue("IconUri", IconPath);
                key.SetValue("IconBackgroundColor", "0");
            }
            var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Mira.lnk");
            WriteShortcut(shortcut, executable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or System.Security.SecurityException)
        { RegistrationError = ex.GetType().Name; }
    }

    public static void WriteShortcut(string path, string executable)
    {
        var instance = new ShellLink();
        try
        {
            var link = (IShellLinkW)instance;
            link.SetPath(executable); link.SetWorkingDirectory(Path.GetDirectoryName(executable)!);
            link.SetDescription("Mira — Ta bibliothèque Jellyfin avec mpv intégré");
            link.SetIconLocation(IconPath, 0);
            SetString((IPropertyStore)instance, 5, AppId);
            ((IPropertyStore)instance).Commit();
            ((IPersistFile)instance).Save(Path.GetFullPath(path), true);
            var changedPath = Marshal.StringToCoTaskMemUni(Path.GetFullPath(path));
            try { SHChangeNotify(0x2000, 0x5, changedPath, IntPtr.Zero); }
            finally { Marshal.FreeCoTaskMem(changedPath); }
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    /// <summary>The file a Shell shortcut points to, or null when it cannot be read.</summary>
    public static string? ShortcutTarget(string path)
    {
        var instance = new ShellLink();
        try
        {
            ((IPersistFile)instance).Load(path, 0);
            var target = new StringBuilder(1024);
            ((IShellLinkW)instance).GetPath(target, target.Capacity, IntPtr.Zero, 0);
            return target.Length > 0 ? target.ToString() : null;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    public static void ApplyToWindow(IntPtr hwnd, string id, bool validation)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) < 0) return;
        try
        {
            // Relaunch properties must precede the ID; these also supply the taskbar's context menu icon.
            if (!validation)
            {
                SetString(store, 3, IconPath + ",0");
                SetString(store, 4, "Mira");
                SetString(store, 2, "\"" + Environment.ProcessPath + "\"");
            }
            SetString(store, 5, id);
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }

    private static void SetString(IPropertyStore store, uint property, string value)
    {
        var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = property };
        var variant = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(value) };
        try { store.SetValue(ref key, ref variant); }
        finally { Marshal.FreeCoTaskMem(variant.Pointer); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint change, uint flags, IntPtr first, IntPtr second);
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant
    { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count); void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value); void Commit();
    }
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int length, IntPtr data, uint flags);
        void GetIDList(out IntPtr list); void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey); void SetHotkey(short hotkey);
        void GetShowCmd(out int show); void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int length, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags); void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
