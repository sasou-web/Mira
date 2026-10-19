using System.Globalization;
using System.Runtime.InteropServices;

namespace Mira.Core;

/// <summary>
/// libmpv's client API as both apps use it: options, properties, commands, events, tracks, chapters and the volume above
/// 100 %. Each app loads the library, sets its own options and draws the video its own way (Playback/MpvEngine on
/// Windows, an embedded window; Playback/MpvPlayer on Mac, mpv's render API).
/// </summary>
public abstract unsafe class MpvClient : IDisposable
{
    private IntPtr _library, _handle;
    private readonly bool _unload;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int> _initialize;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> _setOption, _setProperty;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr> _get;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> _command;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> _free, _destroy;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, double, IntPtr> _wait;
    [StructLayout(LayoutKind.Sequential)] private struct MpvEvent { public int Id; public int Error; public ulong Reply; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct EndFile { public int Reason; public int Error; }
    [StructLayout(LayoutKind.Sequential)] private struct ClientMessage { public int Count; public IntPtr Args; }
    /// <summary>The volume from 0 to 200, and where the part above 100 stands (see <see cref="SetLevel"/>).</summary>
    private double _level;
    private Boost _boost;
    private enum Boost { None, Installed, Unavailable }
    /// <summary>A file is open, so mpv checks a new audio filter before using it.</summary>
    private bool _open;
    public event Action? FileLoaded;
    public event Action? PlaybackRestarted;
    /// <summary>A file ended: true when it played to its end.</summary>
    public event Action<bool>? Ended;
    /// <summary>A script-message from mpv's input bindings.</summary>
    public event Action<string>? Message;
    public event Action<string>? Error;
    public string LibraryPath { get; }
    /// <summary>The native handle and library, for the render API (Mac).</summary>
    public IntPtr Handle => _handle;
    public IntPtr Library => _library;

    /// <summary>
    /// Creates mpv from <paramref name="library"/>, already loaded from <paramref name="libraryPath"/>, with the options
    /// both apps share. The app then sets its own and calls <see cref="Initialize"/>; <paramref name="unload"/> frees the
    /// library with mpv (never on Mac: unloading a dylib that registered Objective-C classes is unsafe).
    /// </summary>
    protected MpvClient(string libraryPath, IntPtr library, bool unload)
    {
        LibraryPath = libraryPath; _library = library; _unload = unload;
        try
        {
            var create = (delegate* unmanaged[Cdecl]<IntPtr>)Export("mpv_create");
            _initialize = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("mpv_initialize");
            _setOption = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)Export("mpv_set_option_string");
            _setProperty = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)Export("mpv_set_property_string");
            _get = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)Export("mpv_get_property_string");
            _command = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)Export("mpv_command");
            _free = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("mpv_free");
            _destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("mpv_terminate_destroy");
            _wait = (delegate* unmanaged[Cdecl]<IntPtr, double, IntPtr>)Export("mpv_wait_event");
            _handle = create(); if (_handle == IntPtr.Zero) throw new IOException(L("mpv n’a pas pu démarrer.", "mpv couldn’t start."));
            Option("config", "no"); Option("terminal", "no"); Option("osc", "no"); Option("idle", "yes");
            Option("keep-open", "no"); Option("input-default-bindings", "no");
        }
        catch { Dispose(); throw; }
    }
    public IntPtr Export(string name) => NativeLibrary.GetExport(_library, name);
    protected void Option(string name, string value)
    {
        using var n = new Utf8(name); using var v = new Utf8(value);
        if (_setOption(_handle, n, v) < 0) throw new IOException(L($"Option mpv indisponible : {name}.", $"mpv option unavailable: {name}."));
    }
    /// <summary>An option an older mpv, chosen by hand, may not know: left out rather than stopping the playback.</summary>
    private bool TryOption(string name, string value)
    {
        using var n = new Utf8(name); using var v = new Utf8(value);
        return _setOption(_handle, n, v) >= 0;
    }
    /// <summary>The options set from Réglages: volume, preferred languages, and the subtitles' size and style.</summary>
    protected void PlayerOptions(PlayerSettings settings)
    {
        _level = VolumeBoost.Clamp(settings.Volume);
        // Room for mpv's own volume above 100, used only by an mpv without the boost filter.
        Option("volume-max", Format(VolumeBoost.Maximum));
        Option("volume", Format(VolumeBoost.Split(_level).Volume));
        Option("alang", settings.AudioLanguage); Option("slang", settings.SubtitleLanguage);
        foreach (var (name, value) in SubtitleStyle.Options(settings))
            if (value is not null) TryOption(name, value);
    }
    /// <summary>
    /// The subtitles' size and style changed while a video plays: each option set again, mpv's own default where Mira
    /// sets none (so a box turned off goes away). One this mpv does not know is left out.
    /// </summary>
    public void StyleSubtitles(PlayerSettings settings)
    {
        foreach (var (name, value) in SubtitleStyle.Options(settings))
            if ((value ?? Get($"option-info/{name}/default-value")) is { } text) Set(name, text);
    }
    protected void Initialize(string failure) { if (_initialize(_handle) < 0) throw new IOException(failure); }
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
        if (_handle == IntPtr.Zero) throw new IOException(L("La lecture est arrêtée.", "Playback is stopped."));
        if (!Run(arguments)) throw new IOException(L("La commande de lecture a échoué.", "The playback command failed."));
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
    /// <param name="paused">Opened paused: « Regarder ensemble » plays it when the group does.</param>
    public void Load(string location, double startSeconds, string? authorization = null, bool paused = false)
    {
        // Tokens are sent as request headers, never in media URLs, logs or shell commands.
        Set("http-header-fields", authorization is null ? "" : "Authorization: " + authorization.Replace(",", "\\,"));
        Set("start", Format(startSeconds)); Set("pause", paused ? "yes" : "no");
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
    /// <summary>What <see cref="Error"/> says when mpv could not play the file.</summary>
    protected abstract string PlaybackFailure { get; }
    /// <summary>
    /// A file that ends because no sound output could open (none plugged in, or AirPlay gone) played its picture to the
    /// end: that is the end of the file, not a failure to play it (Mac).
    /// </summary>
    protected virtual bool SoundlessEndIsEnd => false;
    /// <summary>How mpv's MPV_EVENT_END_FILE ends a file: played to its end, or failed (a reason and an error of libmpv).</summary>
    public static (bool Ended, bool Failed) Ending(int reason, int error, bool soundlessEndIsEnd)
    {
        // MPV_END_FILE_REASON_EOF = 0, MPV_END_FILE_REASON_ERROR = 4; MPV_ERROR_AO_INIT_FAILED = -14.
        var soundless = soundlessEndIsEnd && reason == 4 && error == -14;
        return (reason == 0 || soundless, reason == 4 && !soundless);
    }
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
                var (ended, failed) = Ending(end.Reason, end.Error, SoundlessEndIsEnd);
                if (failed) Error?.Invoke(PlaybackFailure);
                Ended?.Invoke(ended);
            }
            else if (ev.Id == 16 && ev.Data != IntPtr.Zero)
            {
                var message = Marshal.PtrToStructure<ClientMessage>(ev.Data);
                if (message.Count > 0) Message?.Invoke(Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(message.Args)) ?? "");
            }
        }
    }
    /// <summary>Audio and subtitle tracks, named for the menu: title or language first, then codec and channels.</summary>
    public List<MpvTrack> Tracks() => MpvLists.Tracks(Get("track-list"));
    /// <summary>Chapters stored in the media file itself (Matroska/MP4 chapter titles and times).</summary>
    public List<ChapterMark> Chapters() => MpvLists.Chapters(Get("chapter-list"));
    /// <summary>Destroys mpv (on Mac, after the view's render context: VideoView.Release).</summary>
    public void Dispose()
    {
        if (_handle != IntPtr.Zero) { var handle = _handle; _handle = IntPtr.Zero; if (_destroy != null) _destroy(handle); }
        if (_unload && _library != IntPtr.Zero) { NativeLibrary.Free(_library); _library = IntPtr.Zero; }
        GC.SuppressFinalize(this);
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
