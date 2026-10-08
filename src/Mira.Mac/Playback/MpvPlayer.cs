using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mira.Core;

namespace Mira.Mac.Playback;

/// <summary>An audio or subtitle track: mpv id, "audio" or "sub", its menu name and details (codec, channels…).</summary>
public sealed record MpvTrack(string Id, string Type, string Label, string Details);

/// <summary>
/// libmpv for the Mac app: the same playback core as the Windows app (options, tracks, chapters, volume above 100 %),
/// with the video drawn by Mira itself through mpv's render API (<see cref="VideoView"/>) instead of an embedded window.
/// </summary>
public sealed unsafe class MpvPlayer : IDisposable
{
    private IntPtr _library, _handle;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> _setOption, _setProperty;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr> _get;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> _command;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> _free, _destroy;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, double, IntPtr> _wait;
    [StructLayout(LayoutKind.Sequential)] private struct MpvEvent { public int Id; public int Error; public ulong Reply; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct EndFile { public int Reason; public int Error; }
    public event Action? FileLoaded;
    public event Action? PlaybackRestarted;
    public event Action<bool>? Ended;
    public event Action<string>? Error;
    public string LibraryPath { get; }
    /// <summary>The native handle, for the render context of <see cref="VideoView"/>.</summary>
    internal IntPtr Handle => _handle;
    internal IntPtr Library => _library;
    /// <summary>The volume from 0 to 200, and where the part above 100 stands (see <see cref="SetLevel"/>).</summary>
    private double _level;
    private Boost _boost;
    private enum Boost { None, Installed, Unavailable }
    /// <summary>A file is open, so mpv checks a new audio filter before using it.</summary>
    private bool _open;

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

    public MpvPlayer(PlayerSettings settings, bool silent = false)
    {
        LibraryPath = FindLibrary(settings.MpvPath) ?? throw new FileNotFoundException("Le moteur de lecture mpv est introuvable dans Mira.app.");
        _library = NativeLibrary.Load(LibraryPath);
        try
        {
            var create = (delegate* unmanaged[Cdecl]<IntPtr>)Export("mpv_create");
            var initialize = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("mpv_initialize");
            _setOption = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)Export("mpv_set_option_string");
            _setProperty = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)Export("mpv_set_property_string");
            _get = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)Export("mpv_get_property_string");
            _command = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)Export("mpv_command");
            _free = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("mpv_free");
            _destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("mpv_terminate_destroy");
            _wait = (delegate* unmanaged[Cdecl]<IntPtr, double, IntPtr>)Export("mpv_wait_event");
            _handle = create(); if (_handle == IntPtr.Zero) throw new IOException("mpv n’a pas pu démarrer.");
            Option("config", "no"); Option("terminal", "no"); Option("osc", "no"); Option("idle", "yes");
            // Frames go to Mira's own view through the render API; mpv opens no window.
            Option("vo", "libmpv");
            Option("keep-open", "no");
            Option("input-default-bindings", "no"); Option("input-vo-keyboard", "no");
            if (silent) Option("ao", "null");
            // A log of mpv's own messages, when asked for: MIRA_MPV_LOG=/path/to/mpv.log.
            if (Environment.GetEnvironmentVariable("MIRA_MPV_LOG") is { Length: > 0 } log) { Option("log-file", log); Option("msg-level", "all=v"); }
            Option("hwdec", settings.HardwareDecoding ? "auto-safe" : "no");
            _level = VolumeBoost.Clamp(settings.Volume);
            // Room for mpv's own volume above 100, used only by an mpv without the boost filter.
            Option("volume-max", Format(VolumeBoost.Maximum));
            Option("volume", Format(VolumeBoost.Split(_level).Volume));
            Option("alang", settings.AudioLanguage); Option("slang", settings.SubtitleLanguage);
            Option("sub-font-size", settings.SubtitleSize.ToString(CultureInfo.InvariantCulture));
            // Extra mpv options for a diagnosis, MIRA_MPV_OPTIONS="name=value;name=value".
            foreach (var pair in (Environment.GetEnvironmentVariable("MIRA_MPV_OPTIONS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (pair.Split('=', 2) is [var name, var value]) Option(name.Trim(), value.Trim());
            if (initialize(_handle) < 0) throw new IOException("Échec de l’initialisation de mpv.");
        }
        catch { Dispose(); throw; }
    }
    internal IntPtr Export(string name) => NativeLibrary.GetExport(_library, name);
    private void Option(string name, string value)
    {
        using var n = new Utf8(name); using var v = new Utf8(value);
        if (_setOption(_handle, n, v) < 0) throw new IOException($"Option mpv indisponible : {name}.");
    }
    public bool Set(string name, string value)
    {
        if (_handle == IntPtr.Zero) return false;
        using var n = new Utf8(name); using var v = new Utf8(value);
        return _setProperty(_handle, n, v) >= 0;
    }
    public string? Get(string name)
    {
        if (_handle == IntPtr.Zero) return null;
        using var n = new Utf8(name);
        var result = _get(_handle, n); if (result == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(result); } finally { _free(result); }
    }
    public double Number(string name, double fallback = 0) => double.TryParse(Get(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    public bool Flag(string name) => Get(name) == "yes";
    public void Command(params string[] arguments)
    {
        // A native call on a destroyed handle would end the whole process, not just this playback.
        if (_handle == IntPtr.Zero) throw new IOException("La lecture est arrêtée.");
        if (!Run(arguments)) throw new IOException("La commande de lecture a échoué.");
    }
    public bool TryCommand(params string[] arguments) => _handle != IntPtr.Zero && Run(arguments);
    private bool Run(string[] arguments)
    {
        var pointers = arguments.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var array = Marshal.AllocHGlobal((pointers.Length + 1) * IntPtr.Size);
        try
        {
            for (var i = 0; i < pointers.Length; i++) Marshal.WriteIntPtr(array, i * IntPtr.Size, pointers[i]);
            Marshal.WriteIntPtr(array, pointers.Length * IntPtr.Size, IntPtr.Zero);
            return _command(_handle, array) >= 0;
        }
        finally { foreach (var p in pointers) Marshal.FreeCoTaskMem(p); Marshal.FreeHGlobal(array); }
    }
    public void Load(string location, double startSeconds, string? authorization = null)
    {
        // Tokens are sent as request headers, never in media URLs or logs.
        Set("http-header-fields", authorization is null ? "" : "Authorization: " + authorization.Replace(",", "\\,"));
        Set("start", Format(startSeconds)); Set("pause", "no");
        _open = false;
        Command("loadfile", location, "replace");
    }
    /// <summary>The volume as the slider shows it, from 0 to <see cref="VolumeBoost.Maximum"/>.</summary>
    public double Level => _level;
    /// <summary>
    /// Sets the volume from 0 to 200. Above 100, mpv stays at 100 and the boost filter adds the rest, limited so that
    /// nothing clips. The filter is added while a file is open, when mpv checks it: an mpv without it refuses it and
    /// keeps playing, and Mira then falls back on mpv's own volume above 100.
    /// </summary>
    public void SetLevel(double level) { _level = VolumeBoost.Clamp(level); ApplyLevel(); }
    private void ApplyLevel(bool loading = false)
    {
        if (_handle == IntPtr.Zero) return;
        var (volume, gain) = VolumeBoost.Split(_level);
        if (_boost == Boost.Unavailable) { Set("volume", Format(_level)); return; }
        Set("volume", Format(volume));
        // Back to 100 or less: a new file starts without the filter; the open one keeps it at gain 1, without a gap.
        if (_boost == Boost.Installed && gain <= 1 && (loading || !_open))
        { if (TryCommand("af", "remove", "@" + VolumeBoost.Label)) _boost = Boost.None; }
        // Live while the audio plays; otherwise the filter's own settings are what the next file starts with.
        else if (_boost == Boost.Installed)
        { if (!TryCommand("af-command", VolumeBoost.Label, "volume", VolumeBoost.Gain(gain), VolumeBoost.Target)) TryCommand("af", "set", VolumeBoost.Filter(gain)); }
        else if (gain > 1 && _open)
        {
            if (TryCommand("af", "set", VolumeBoost.Filter(gain))) _boost = Boost.Installed;
            else { _boost = Boost.Unavailable; Set("volume", Format(_level)); }
        }
    }
    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
    /// <summary>Reads mpv's pending events; called by the player's timer on the UI thread.</summary>
    public void Poll()
    {
        if (_handle == IntPtr.Zero) return;
        for (var i = 0; i < 40 && _handle != IntPtr.Zero; i++)
        {
            var p = _wait(_handle, 0); if (p == IntPtr.Zero) break;
            var ev = Marshal.PtrToStructure<MpvEvent>(p); if (ev.Id == 0) break;
            if (ev.Id == 8) { _open = true; ApplyLevel(loading: true); FileLoaded?.Invoke(); }
            else if (ev.Id == 21) PlaybackRestarted?.Invoke();
            else if (ev.Id == 7 && ev.Data != IntPtr.Zero)
            {
                var end = Marshal.PtrToStructure<EndFile>(ev.Data); _open = false;
                // Without a sound output (none plugged in, or AirPlay gone), mpv plays the picture to its end and then
                // reports MPV_ERROR_AO_INIT_FAILED: that is the end of the file, not a failure to play it.
                var silentEnd = end.Reason == 4 && end.Error == -14;
                if (end.Reason == 4 && !silentEnd) Error?.Invoke("mpv n’a pas pu lire ce média. Vérifie la connexion au serveur Jellyfin.");
                Ended?.Invoke(end.Reason == 0 || silentEnd);
            }
        }
    }
    /// <summary>Audio and subtitle tracks, named for the menu: title or language first, then codec and channels.</summary>
    public List<MpvTrack> Tracks()
    {
        try
        {
            using var doc = JsonDocument.Parse(Get("track-list") ?? "[]");
            return doc.RootElement.EnumerateArray().Where(t => t.GetProperty("type").GetString() is "audio" or "sub").Select(t =>
            {
                string Read(string name) => t.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v.ToString() : "";
                bool Flag(string name) => t.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
                var channels = t.TryGetProperty("demux-channel-count", out var count) && count.TryGetInt32(out var value) ? value : 0;
                var id = Read("id"); var type = Read("type");
                var (label, details) = PlayerText.Track(id, Read("title"), Read("lang"), Read("codec"), type == "audio" ? channels : 0, Flag("forced"), Flag("external"));
                return new MpvTrack(id, type, label, details);
            }).ToList();
        }
        catch (JsonException) { return []; }
    }
    /// <summary>Chapters stored in the media file itself (Matroska/MP4 chapter titles and times).</summary>
    public List<ChapterMark> Chapters()
    {
        try
        {
            using var doc = JsonDocument.Parse(Get("chapter-list") ?? "[]");
            return doc.RootElement.EnumerateArray()
                .Where(c => c.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Number)
                .Select(c => new ChapterMark(c.GetProperty("time").GetDouble(), c.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null))
                .OrderBy(c => c.Start).ToList();
        }
        catch (JsonException) { return []; }
    }
    /// <summary>Destroys mpv. The view's render context must be released first (see <see cref="VideoView.Release"/>).</summary>
    public void Dispose()
    {
        if (_handle != IntPtr.Zero) { var handle = _handle; _handle = IntPtr.Zero; _destroy(handle); }
        // The library stays loaded for the next playback: unloading a dylib that registered Objective-C classes is unsafe.
    }

    /// <summary>A UTF-8 copy of a string for one native call.</summary>
    private readonly struct Utf8 : IDisposable
    {
        private readonly IntPtr _pointer;
        public Utf8(string value) => _pointer = Marshal.StringToCoTaskMemUTF8(value);
        public static implicit operator IntPtr(Utf8 value) => value._pointer;
        public void Dispose() => Marshal.FreeCoTaskMem(_pointer);
    }
}
