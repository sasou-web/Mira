using System.Runtime.InteropServices;
using Mira.Core;

namespace Mira.Mac.Services;

/// <summary>
/// The Mac's media controls (MediaPlayer framework), as the system media controls of Windows: the keyboard's play and
/// track keys (F7 to F9), the button of AirPods or a headset, and « Now Playing » of Control Center and the menu bar,
/// with the title, its poster and its bar. A remote for the player that is open (<see cref="Pressed"/>), never a second
/// player. libmpv leaves the media keys alone (input-media-keys is off for libmpv). Nothing native where the framework is
/// missing (Linux, where the self-check runs): <see cref="Clock"/> still says what would have been told.
/// </summary>
public static unsafe partial class MacNowPlaying
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string MediaPlayer = "/System/Library/Frameworks/MediaPlayer.framework/MediaPlayer";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr objc_getClass(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr sel_registerName(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);
    [LibraryImport(ObjC)] private static partial void objc_registerClassPair(IntPtr type);
    [LibraryImport(ObjC)][return: MarshalAs(UnmanagedType.U1)] private static partial bool class_addMethod(IntPtr type, IntPtr selector, delegate* unmanaged<IntPtr, IntPtr, IntPtr, nint> implementation, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);
    [LibraryImport(ObjC)] private static partial IntPtr objc_autoreleasePoolPush();
    [LibraryImport(ObjC)] private static partial void objc_autoreleasePoolPop(IntPtr pool);
    [LibraryImport(ObjC)] private static partial IntPtr objc_retain(IntPtr value);
    [LibraryImport(ObjC)] private static partial IntPtr objc_autorelease(IntPtr value);
    [LibraryImport(ObjC)] private static partial void objc_release(IntPtr value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr Send(IntPtr receiver, IntPtr selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr value, IntPtr key);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr SendText(IntPtr receiver, IntPtr selector, string text);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr SendBytes(IntPtr receiver, IntPtr selector, byte* bytes, nuint length);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr SendDoubleArgument(IntPtr receiver, IntPtr selector, double value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr SendUnsignedArgument(IntPtr receiver, IntPtr selector, nuint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void SendBool(IntPtr receiver, IntPtr selector, byte value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void SendTarget(IntPtr receiver, IntPtr selector, IntPtr target, IntPtr action);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial double SendDouble(IntPtr receiver, IntPtr selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr SendArtwork(IntPtr receiver, IntPtr selector, CGSize size, IntPtr handler);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr SendCString(IntPtr receiver, IntPtr selector);

    [StructLayout(LayoutKind.Sequential)] private struct CGSize { public double Width, Height; }

    // MPNowPlayingPlaybackState (macOS only: media keys go to the app that says it plays).
    private const nuint StatePlaying = 1, StatePaused = 2, StateStopped = 3;
    // MPNowPlayingInfoMediaTypeVideo.
    private const nuint Video = 2;
    // MPRemoteCommandHandlerStatus.
    private const nint Success = 0, NoListener = 200;

    /// <summary>Each key, its command of MPRemoteCommandCenter, the selector of Mira's target that receives it.</summary>
    private static readonly (MediaKey Key, string Command, string Selector)[] Commands =
    [
        (MediaKey.Play, "playCommand", "miraPlay:"),
        (MediaKey.Pause, "pauseCommand", "miraPause:"),
        // F8, the button of AirPods and of a headset.
        (MediaKey.TogglePause, "togglePlayPauseCommand", "miraToggle:"),
        (MediaKey.Stop, "stopCommand", "miraStop:"),
        // F7 and a triple press of AirPods: 10 s back, as the previous track of Windows.
        (MediaKey.Back, "previousTrackCommand", "miraPrevious:"),
        (MediaKey.Back, "skipBackwardCommand", "miraBack:"),
        (MediaKey.Forward, "skipForwardCommand", "miraForward:"),
        // F9 and a double press of AirPods: the next episode, only when there is one.
        (MediaKey.Next, "nextTrackCommand", "miraNext:"),
        // The bar of « Now Playing ».
        (MediaKey.Seek, "changePlaybackPositionCommand", "miraSeek:"),
    ];

    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, MediaKey> Selectors = [];
    private static readonly List<(MediaKey Key, IntPtr Command)> Registered = [];
    private static bool _listening;
    private static IntPtr _target, _center, _artwork, _image, _block;
    private static string? _artworkFor;

    /// <summary>What was last told, and when to tell again.</summary>
    public static NowPlaying Clock { get; } = new();
    /// <summary>A press, on the interface's thread; the value is where to go for <see cref="MediaKey.Seek"/>.</summary>
    public static event Action<MediaKey, double>? Pressed;
    /// <summary>Where an error in a press's handling goes (errors.log), since it cannot be thrown through macOS.</summary>
    public static Action<Exception>? Failed;
    /// <summary>The commands are listened to: false outside macOS, or where MediaPlayer did not load.</summary>
    public static bool Listening => _listening;

    /// <summary>Makes Mira the target of the media commands, all off until a title plays. Once, at start.</summary>
    public static void Listen()
    {
        if (_listening || !OperatingSystem.IsMacOS() || !NativeLibrary.TryLoad(MediaPlayer, out _)) return;
        var pool = IntPtr.Zero;
        try
        {
            pool = objc_autoreleasePoolPush();
            var type = objc_getClass("MiraRemoteTarget");
            if (type == IntPtr.Zero)
            {
                type = objc_allocateClassPair(objc_getClass("NSObject"), "MiraRemoteTarget", 0);
                if (type == IntPtr.Zero) return;
                foreach (var (_, _, selector) in Commands) class_addMethod(type, sel_registerName(selector), &Handle, "q@:@");
                objc_registerClassPair(type);
            }
            _target = Send(Send(type, sel_registerName("alloc")), sel_registerName("init"));
            var center = Send(objc_getClass("MPRemoteCommandCenter"), sel_registerName("sharedCommandCenter"));
            _center = Send(objc_getClass("MPNowPlayingInfoCenter"), sel_registerName("defaultCenter"));
            if (_target == IntPtr.Zero || center == IntPtr.Zero || _center == IntPtr.Zero) return;
            var ten = Send(objc_getClass("NSArray"), sel_registerName("arrayWithObject:"), SendDoubleArgument(objc_getClass("NSNumber"), sel_registerName("numberWithDouble:"), NowPlaying.Skip));
            foreach (var (key, name, selector) in Commands)
            {
                var command = Send(center, sel_registerName(name));
                if (command == IntPtr.Zero) continue;
                var action = sel_registerName(selector);
                lock (Gate) Selectors[action] = key;
                SendTarget(command, sel_registerName("addTarget:action:"), _target, action);
                if (name is "skipBackwardCommand" or "skipForwardCommand") Send(command, sel_registerName("setPreferredIntervals:"), ten);
                SendBool(command, sel_registerName("setEnabled:"), 0);
                Registered.Add((key, command));
            }
            _listening = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        finally { if (pool != IntPtr.Zero) objc_autoreleasePoolPop(pool); }
    }

    /// <summary>
    /// The playback as it is now: told to macOS when it changed (<see cref="NowPlaying.Due"/>). The commands follow it:
    /// a key that cannot do anything is greyed in Control Center and goes to no one.
    /// </summary>
    public static void Show(NowPlayingState state)
    {
        var now = DateTimeOffset.UtcNow;
        if (!Clock.Due(state, now)) return;
        Clock.Tell(state, now);
        if (!_listening) return;
        state = Clock.Told!;
        var pool = objc_autoreleasePoolPush();
        try
        {
            foreach (var (key, command) in Registered) SendBool(command, sel_registerName("setEnabled:"), NowPlaying.Enabled(key, state) ? (byte)1 : (byte)0);
            var info = Send(objc_getClass("NSMutableDictionary"), sel_registerName("dictionary"));
            Put(info, "MPMediaItemPropertyTitle", Text(state.Title));
            if (state.Subtitle.Length > 0) Put(info, "MPMediaItemPropertyArtist", Text(state.Subtitle));
            Put(info, "MPNowPlayingInfoPropertyMediaType", SendUnsignedArgument(objc_getClass("NSNumber"), sel_registerName("numberWithUnsignedInteger:"), Video));
            if (state.Duration > 0) Put(info, "MPMediaItemPropertyPlaybackDuration", Number(state.Duration));
            Put(info, "MPNowPlayingInfoPropertyElapsedPlaybackTime", Number(state.Position));
            Put(info, "MPNowPlayingInfoPropertyPlaybackRate", Number(state.Moving));
            Put(info, "MPNowPlayingInfoPropertyDefaultPlaybackRate", Number(1));
            if (_artwork != IntPtr.Zero) Put(info, "MPMediaItemPropertyArtwork", _artwork);
            Send(_center, sel_registerName("setNowPlayingInfo:"), info);
            SendUnsignedArgument(_center, sel_registerName("setPlaybackState:"), state.Ready && !state.Paused ? StatePlaying : StatePaused);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary>
    /// The poster shown beside the title (JPEG or PNG); <paramref name="title"/> is the title it belongs to, so a poster that
    /// arrives after the next title started is left. Told with the next state.
    /// </summary>
    public static void SetArtwork(string title, byte[] picture, int width, int height)
    {
        _artworkFor = title;
        if (!_listening || picture.Length == 0) return;
        var pool = objc_autoreleasePoolPush();
        try
        {
            IntPtr data;
            fixed (byte* bytes = picture) data = SendBytes(objc_getClass("NSData"), sel_registerName("dataWithBytes:length:"), bytes, (nuint)picture.Length);
            var image = Send(Send(objc_getClass("NSImage"), sel_registerName("alloc")), sel_registerName("initWithData:"), data);
            if (image == IntPtr.Zero) return;
            IntPtr old;
            lock (Gate) { old = _image; _image = image; }
            if (old != IntPtr.Zero) objc_release(old);
            var artwork = Send(objc_getClass("MPMediaItemArtwork"), sel_registerName("alloc"));
            artwork = SendArtwork(artwork, sel_registerName("initWithBoundsSize:requestHandler:"), new CGSize { Width = Math.Max(1, width), Height = Math.Max(1, height) }, Block());
            if (_artwork != IntPtr.Zero) objc_release(_artwork);
            _artwork = artwork;
            // Told at once, the rest as it was.
            if (Clock.Told is { } told && told.Title == title) { var state = told; Clock.Clear(); Show(state); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        finally { objc_autoreleasePoolPop(pool); }
    }
    /// <summary>The title the poster was last given for: for the self-check.</summary>
    public static string? ArtworkFor => _artworkFor;

    /// <summary>The player closed: « Now Playing » empty, the keys back to the other apps.</summary>
    public static void Clear()
    {
        Clock.Clear(); _artworkFor = null;
        if (!_listening) return;
        var pool = objc_autoreleasePoolPush();
        try
        {
            foreach (var (_, command) in Registered) SendBool(command, sel_registerName("setEnabled:"), 0);
            Send(_center, sel_registerName("setNowPlayingInfo:"), IntPtr.Zero);
            SendUnsignedArgument(_center, sel_registerName("setPlaybackState:"), StateStopped);
            if (_artwork != IntPtr.Zero) { objc_release(_artwork); _artwork = IntPtr.Zero; }
            IntPtr old;
            lock (Gate) { old = _image; _image = IntPtr.Zero; }
            if (old != IntPtr.Zero) objc_release(old);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary>The title macOS holds in « Now Playing » now, read back from it: for the self-check on a Mac.</summary>
    public static string? ToldTitle()
    {
        if (!_listening) return null;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var info = Send(_center, sel_registerName("nowPlayingInfo"));
            if (info == IntPtr.Zero || Constant("MPMediaItemPropertyTitle") is not { } key) return null;
            var title = Send(info, sel_registerName("objectForKey:"), key);
            var text = title == IntPtr.Zero ? IntPtr.Zero : SendCString(title, sel_registerName("UTF8String"));
            return text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text);
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary>
    /// For the self-check: a press as macOS sends it, through Mira's own target (on a Mac, the same native method macOS
    /// calls; elsewhere straight to <see cref="Pressed"/>).
    /// </summary>
    public static void Press(MediaKey key, double value = 0)
    {
        var selector = Commands.First(x => x.Key == key).Selector;
        if (_listening && key != MediaKey.Seek) { Send(_target, sel_registerName(selector), IntPtr.Zero); return; }
        Deliver(key, value);
    }

    [UnmanagedCallersOnly]
    private static nint Handle(IntPtr self, IntPtr selector, IntPtr commandEvent)
    {
        // Nothing may leave this method: an exception through native code ends the app.
        try
        {
            MediaKey key;
            lock (Gate) if (!Selectors.TryGetValue(selector, out key)) return NoListener;
            var value = key == MediaKey.Seek && commandEvent != IntPtr.Zero ? SendDouble(commandEvent, sel_registerName("positionTime")) : 0;
            if (Pressed is null) return NoListener;
            Deliver(key, value);
            return Success;
        }
        catch (Exception ex) { Failed?.Invoke(ex); return NoListener; }
    }
    private static void Deliver(MediaKey key, double value) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        try { Pressed?.Invoke(key, value); }
        catch (Exception ex) when (Errors.Expected(ex)) { Failed?.Invoke(ex); }
    });

    /// <summary>
    /// The request handler MPMediaItemArtwork asks the poster through: a global block (it captures nothing, the poster is
    /// <see cref="_image"/>), made once, as clang lays one out. macOS may call it on any thread.
    /// </summary>
    private static IntPtr Block()
    {
        if (_block != IntPtr.Zero) return _block;
        var isa = NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_NSConcreteGlobalBlock");
        var signature = Marshal.StringToCoTaskMemUTF8("@24@?0{CGSize=dd}8");
        var descriptor = (nuint*)NativeMemory.AllocZeroed(3, (nuint)sizeof(nuint));
        descriptor[1] = 32; descriptor[2] = (nuint)signature;
        var block = (byte*)NativeMemory.AllocZeroed(32);
        *(IntPtr*)block = isa;
        *(int*)(block + 8) = (1 << 28) | (1 << 30); // BLOCK_IS_GLOBAL | BLOCK_HAS_SIGNATURE
        *(IntPtr*)(block + 16) = (IntPtr)(delegate* unmanaged<IntPtr, CGSize, IntPtr>)&Artwork;
        *(nuint**)(block + 24) = descriptor;
        return _block = (IntPtr)block;
    }
    [UnmanagedCallersOnly]
    private static IntPtr Artwork(IntPtr block, CGSize size)
    {
        try { lock (Gate) return _image == IntPtr.Zero ? IntPtr.Zero : objc_autorelease(objc_retain(_image)); }
        catch (Exception ex) { Failed?.Invoke(ex); return IntPtr.Zero; }
    }

    private static void Put(IntPtr dictionary, string key, IntPtr value)
    {
        if (value != IntPtr.Zero && Constant(key) is { } name) Send(dictionary, sel_registerName("setObject:forKey:"), value, name);
    }
    private static readonly Dictionary<string, IntPtr?> Constants = [];
    /// <summary>The key itself, exported by MediaPlayer, rather than a copy of its text; null where this macOS has none.</summary>
    private static IntPtr? Constant(string name)
    {
        if (Constants.TryGetValue(name, out var known)) return known;
        IntPtr? value = NativeLibrary.TryLoad(MediaPlayer, out var library) && NativeLibrary.TryGetExport(library, name, out var address) ? Marshal.ReadIntPtr(address) : null;
        return Constants[name] = value is { } found && found != IntPtr.Zero ? found : null;
    }
    private static IntPtr Text(string text) => SendText(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), text);
    private static IntPtr Number(double value) => SendDoubleArgument(objc_getClass("NSNumber"), sel_registerName("numberWithDouble:"), value);
}
