using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mira.Core;

namespace Mira.Desktop.Playback;

public sealed class MpvEngine : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeFn(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetFn(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetFn(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CommandFn(IntPtr handle, IntPtr args);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeFn(IntPtr pointer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr WaitFn(IntPtr handle, double timeout);
    [StructLayout(LayoutKind.Sequential)] private struct MpvEvent { public int Id; public int Error; public ulong Reply; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct EndFile { public int Reason; public int Error; }
    [StructLayout(LayoutKind.Sequential)] private struct ClientMessage { public int Count; public IntPtr Args; }
    private IntPtr _library, _handle;
    private readonly SetFn _setOption, _setProperty;
    private readonly GetFn _get;
    private readonly CommandFn _command;
    private readonly FreeFn _free, _destroy;
    private readonly WaitFn _wait;
    public event Action? FileLoaded;
    public event Action? PlaybackRestarted;
    public event Action<bool>? Ended;
    public event Action<string>? Message;
    public event Action<string>? Error;
    public string LibraryPath { get; }

    public static string? FindLibrary(string? custom = null)
    {
        var candidates = new[] { custom,
            Path.Combine(AppContext.BaseDirectory, "mpv-2.dll"),
            Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"),
            Path.Combine(AppContext.BaseDirectory, "native", "mpv-2.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Jellyfin MPV Shim", "_internal", "mpv-2.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Jellyfin MPV Shim", "_internal", "mpv-2.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "mpv.net", "libmpv-2.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "mpv.net", "libmpv-2.dll") };
        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
    }
    public MpvEngine(IntPtr window, PlayerSettings settings, bool headless = false)
    {
        LibraryPath = FindLibrary(settings.MpvPath) ?? throw new FileNotFoundException("Moteur mpv introuvable. Indique le fichier mpv-2.dll dans les réglages.");
        // LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR + DEFAULT_DIRS also resolves dependencies beside the chosen DLL.
        _library = LoadLibraryEx(LibraryPath, IntPtr.Zero, 0x100 | 0x1000);
        if (_library == IntPtr.Zero) throw new IOException("Impossible de charger mpv. Vérifie que la bibliothèque est en 64 bits et que ses dépendances sont présentes.");
        try
        {
            var create = Export<CreateFn>("mpv_create"); var initialize = Export<InitializeFn>("mpv_initialize");
            _setOption = Export<SetFn>("mpv_set_option_string"); _setProperty = Export<SetFn>("mpv_set_property_string");
            _get = Export<GetFn>("mpv_get_property_string"); _command = Export<CommandFn>("mpv_command");
            _free = Export<FreeFn>("mpv_free"); _destroy = Export<FreeFn>("mpv_terminate_destroy"); _wait = Export<WaitFn>("mpv_wait_event");
            _handle = create(); if (_handle == IntPtr.Zero) throw new IOException("mpv n’a pas pu démarrer.");
            Option("config", "no"); Option("terminal", "no"); Option("osc", "no"); Option("idle", "yes");
            if (!headless) Option("wid", window.ToInt64().ToString(CultureInfo.InvariantCulture));
            Option("keep-open", "no");
            Option("input-default-bindings", "no"); Option("input-vo-keyboard", "yes");
            Option("input-conf", Path.Combine(AppContext.BaseDirectory, "mira-input.conf"));
            Option("vo", headless ? "null" : "gpu-next,gpu");
            if (headless) Option("ao", "null");
            Option("hwdec", !headless && settings.HardwareDecoding ? "auto" : "no");
            Option("volume", settings.Volume.ToString(CultureInfo.InvariantCulture));
            Option("alang", settings.AudioLanguage); Option("slang", settings.SubtitleLanguage);
            Option("sub-font-size", settings.SubtitleSize.ToString(CultureInfo.InvariantCulture));
            if (initialize(_handle) < 0) throw new IOException("Échec de l’initialisation du rendu mpv.");
        }
        catch { Dispose(); throw; }
    }
    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
    private void Option(string name, string value) { if (_setOption(_handle, name, value) < 0) throw new IOException($"Option mpv indisponible : {name}."); }
    public bool Set(string name, string value) => _handle != IntPtr.Zero && _setProperty(_handle, name, value) >= 0;
    public string? Get(string name)
    {
        if (_handle == IntPtr.Zero) return null;
        var result = _get(_handle, name); if (result == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(result); } finally { _free(result); }
    }
    public double Number(string name, double fallback = 0) => double.TryParse(Get(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    public bool Flag(string name) => Get(name) == "yes";
    public void Command(params string[] arguments)
    {
        var pointers = arguments.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var array = Marshal.AllocHGlobal((pointers.Length + 1) * IntPtr.Size);
        try
        {
            for (var i = 0; i < pointers.Length; i++) Marshal.WriteIntPtr(array, i * IntPtr.Size, pointers[i]);
            Marshal.WriteIntPtr(array, pointers.Length * IntPtr.Size, IntPtr.Zero);
            if (_command(_handle, array) < 0) throw new IOException("La commande de lecture a échoué.");
        }
        finally { foreach (var p in pointers) Marshal.FreeCoTaskMem(p); Marshal.FreeHGlobal(array); }
    }
    public void Load(string location, double startSeconds, string? authorization = null)
    {
        // Tokens are sent as request headers, never in media URLs, logs or shell commands.
        Set("http-header-fields", authorization is null ? "" : "Authorization: " + authorization.Replace(",", "\\,"));
        Set("start", startSeconds.ToString(CultureInfo.InvariantCulture)); Set("pause", "no");
        Command("loadfile", location, "replace");
    }
    public void Poll()
    {
        if (_handle == IntPtr.Zero) return;
        for (var i = 0; i < 40 && _handle != IntPtr.Zero; i++)
        {
            var p = _wait(_handle, 0); if (p == IntPtr.Zero) break;
            var ev = Marshal.PtrToStructure<MpvEvent>(p); if (ev.Id == 0) break;
            if (ev.Id == 8) FileLoaded?.Invoke();
            else if (ev.Id == 21) PlaybackRestarted?.Invoke();
            else if (ev.Id == 7 && ev.Data != IntPtr.Zero)
            {
                var end = Marshal.PtrToStructure<EndFile>(ev.Data);
                if (end.Reason == 4) Error?.Invoke("mpv n’a pas pu lire ce média. Vérifie l’accès au fichier et la connexion Jellyfin.");
                Ended?.Invoke(end.Reason == 0);
            }
            else if (ev.Id == 16 && ev.Data != IntPtr.Zero)
            {
                var message = Marshal.PtrToStructure<ClientMessage>(ev.Data);
                if (message.Count > 0) Message?.Invoke(Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(message.Args)) ?? "");
            }
        }
    }
    public List<(string Id, string Label, string Type)> Tracks()
    {
        try
        {
            using var doc = JsonDocument.Parse(Get("track-list") ?? "[]");
            return doc.RootElement.EnumerateArray().Where(t => t.GetProperty("type").GetString() is "audio" or "sub").Select(t =>
            {
                string Read(string name) => t.TryGetProperty(name, out var v) ? v.ToString() : "";
                var id = Read("id"); var label = string.Join(" · ", new[] { Read("title"), Read("lang"), Read("codec") }.Where(s => s.Length > 0));
                return (id, string.IsNullOrEmpty(label) ? $"Piste {id}" : label, Read("type"));
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
    public void Dispose()
    {
        if (_handle != IntPtr.Zero) { _destroy?.Invoke(_handle); _handle = IntPtr.Zero; }
        if (_library != IntPtr.Zero) { NativeLibrary.Free(_library); _library = IntPtr.Zero; }
    }
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string file, IntPtr fileHandle, uint flags);
}
