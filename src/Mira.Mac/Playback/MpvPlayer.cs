using System.Runtime.InteropServices;
using Mira.Core;
using static Mira.Core.Lang;

namespace Mira.Mac.Playback;

/// <summary>
/// libmpv for the Mac app: the same playback core as the Windows app (<see cref="MpvClient"/>), with the video drawn by
/// Mira itself through mpv's render API (<see cref="VideoView"/>) instead of an embedded window.
/// </summary>
public sealed class MpvPlayer : MpvClient
{
    /// <summary>
    /// libmpv inside Mira.app (Contents/Frameworks), then the usual install places. <paramref name="custom"/> comes first:
    /// the MIRA_LIBMPV variable, for a build of mpv of one's own.
    /// </summary>
    public static string? FindLibrary(string? custom = null)
    {
        var app = AppContext.BaseDirectory;
        var candidates = new[]
        {
            custom, Environment.GetEnvironmentVariable("MIRA_LIBMPV"),
            Path.GetFullPath(Path.Combine(app, "..", "Frameworks", "libmpv.2.dylib")),
            Path.Combine(app, "libmpv.2.dylib"),
            "/opt/homebrew/lib/libmpv.2.dylib", "/usr/local/lib/libmpv.2.dylib",
            "/usr/lib/x86_64-linux-gnu/libmpv.so.2", "/usr/lib/aarch64-linux-gnu/libmpv.so.2", "/usr/lib/libmpv.so.2"
        };
        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
    }

    public MpvPlayer(PlayerSettings settings, bool silent = false) : this(settings, silent, FindLibrary(settings.MpvPath)) { }
    // The library stays loaded after the playback: unloading a dylib that registered Objective-C classes is unsafe.
    private MpvPlayer(PlayerSettings settings, bool silent, string? path)
        : base(path ?? "", NativeLibrary.Load(path ?? throw new FileNotFoundException("Le moteur de lecture mpv est introuvable dans Mira.app.")), unload: false)
    {
        try
        {
            // Frames go to Mira's own view through the render API; mpv opens no window.
            Option("vo", "libmpv");
            Option("input-vo-keyboard", "no");
            if (silent) Option("ao", "null");
            // A log of mpv's own messages, when asked for: MIRA_MPV_LOG=/path/to/mpv.log.
            if (Environment.GetEnvironmentVariable("MIRA_MPV_LOG") is { Length: > 0 } log) { Option("log-file", log); Option("msg-level", "all=v"); }
            Option("hwdec", settings.HardwareDecoding ? "auto-safe" : "no");
            PlayerOptions(settings);
            // Extra mpv options for a diagnosis, MIRA_MPV_OPTIONS="name=value;name=value".
            foreach (var pair in (Environment.GetEnvironmentVariable("MIRA_MPV_OPTIONS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (pair.Split('=', 2) is [var name, var value]) Option(name.Trim(), value.Trim());
            Initialize(L("Échec de l’initialisation de mpv.", "mpv failed to start."));
        }
        catch { Dispose(); throw; }
    }
    protected override string PlaybackFailure => L("mpv n’a pas pu lire ce média. Vérifie la connexion au serveur Jellyfin.", "mpv couldn’t play this media. Check the connection to the Jellyfin server.");
    protected override bool SoundlessEndIsEnd => true;
}
