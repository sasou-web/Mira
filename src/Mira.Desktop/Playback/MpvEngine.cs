using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Mira.Core;

namespace Mira.Desktop.Playback;

public sealed class MpvEngine : MpvClient
{
    /// <summary>
    /// The library chosen in Réglages (<paramref name="custom"/>), as it is: loading it says what is wrong; otherwise the
    /// first one found in the known places that is built for x64 (<see cref="PortableExecutable.IsX64"/>): another
    /// program's may be 32-bit, and loading it would fail.
    /// </summary>
    public static string? FindLibrary(string? custom = null)
    {
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;
        var candidates = new[] {
            // Installed by Mira on request (Setup's task or Réglages → Lecture), in the profile that updates never touch.
            MpvInstaller.LibraryPath(Services.AppFiles.ProfileDirectory),
            Path.Combine(AppContext.BaseDirectory, "mpv-2.dll"),
            Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"),
            Path.Combine(AppContext.BaseDirectory, "native", "mpv-2.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Jellyfin MPV Shim", "_internal", "mpv-2.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Jellyfin MPV Shim", "_internal", "mpv-2.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "mpv.net", "libmpv-2.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "mpv.net", "libmpv-2.dll") };
        return candidates.FirstOrDefault(p => File.Exists(p) && PortableExecutable.IsX64(p));
    }
    public MpvEngine(IntPtr window, PlayerSettings settings, bool headless = false) : this(window, settings, headless, FindLibrary(settings.MpvPath)) { }
    private MpvEngine(IntPtr window, PlayerSettings settings, bool headless, string? path) : base(path ?? "", Load(path), unload: true)
    {
        try
        {
            if (!headless) Option("wid", window.ToInt64().ToString(CultureInfo.InvariantCulture));
            // Keeps the screen on and the PC awake while a video plays (mpv's default, made explicit); paused, it may sleep.
            if (!headless) Option("stop-screensaver", "yes");
            Option("input-vo-keyboard", "yes");
            Option("input-conf", Services.AppFiles.InputConf);
            Option("vo", headless ? "null" : "gpu-next,gpu");
            if (headless) Option("ao", "null");
            Option("hwdec", !headless && settings.HardwareDecoding ? "auto" : "no");
            PlayerOptions(settings);
            Initialize(L("Échec de l’initialisation du rendu mpv.", "mpv’s video rendering couldn’t start."));
        }
        catch { Dispose(); throw; }
    }
    /// <summary>
    /// LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR + DEFAULT_DIRS: dependencies beside the chosen DLL, never the current folder or
    /// the PATH.
    /// </summary>
    private static IntPtr Load(string? path)
    {
        if (path is null) throw new FileNotFoundException(L("Moteur mpv introuvable. Indique le fichier mpv-2.dll dans les réglages.", "mpv engine not found. Choose the mpv-2.dll file in Settings."));
        var library = LoadLibraryEx(path, IntPtr.Zero, 0x100 | 0x1000);
        return library != IntPtr.Zero ? library : throw new IOException(L("Impossible de charger mpv. Vérifie que la bibliothèque est en 64 bits et que ses dépendances sont présentes.", "Couldn’t load mpv. Check that the library is 64-bit and that its dependencies are there."));
    }
    protected override string PlaybackFailure => L("mpv n’a pas pu lire ce média. Vérifie l’accès au fichier et la connexion Jellyfin.", "mpv couldn’t play this media. Check access to the file and the connection to Jellyfin.");
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string file, IntPtr fileHandle, uint flags);
}
