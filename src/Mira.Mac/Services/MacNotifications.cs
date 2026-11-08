using System.Runtime.InteropServices;
using Mira.Core;

namespace Mira.Mac.Services;

/// <summary>
/// The Mac's notifications (UserNotifications framework), as Mira for Windows' toasts (<c>Toasts</c>): « Nouvel épisode »
/// of a series this account follows, someone who joined the group. Clicked, Mira comes forward where the notice leads.
/// macOS asks the person once, at the first notice; refused, nothing more is shown and nothing is asked again (System
/// Settings → Notifications → Mira). Only from Mira.app (macOS refuses a process outside an app bundle, and raises an
/// Objective-C exception that would end Mira), never for the checks (<see cref="Quiet"/>): <see cref="Shown"/> still
/// records what would have been shown, which the self-check reads under Linux too.
/// </summary>
public static unsafe partial class MacNotifications
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string UserNotifications = "/System/Library/Frameworks/UserNotifications.framework/UserNotifications";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr objc_getClass(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr objc_getProtocol(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr sel_registerName(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);
    [LibraryImport(ObjC)] private static partial void objc_registerClassPair(IntPtr type);
    [LibraryImport(ObjC)][return: MarshalAs(UnmanagedType.U1)] private static partial bool class_addProtocol(IntPtr type, IntPtr protocol);
    [LibraryImport(ObjC)][return: MarshalAs(UnmanagedType.U1)] private static partial bool class_addMethod(IntPtr type, IntPtr selector, delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> implementation, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);
    [LibraryImport(ObjC)] private static partial IntPtr objc_autoreleasePoolPush();
    [LibraryImport(ObjC)] private static partial void objc_autoreleasePoolPop(IntPtr pool);
    [LibraryImport(ObjC)] private static partial void objc_release(IntPtr value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr Send(IntPtr receiver, IntPtr selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr value, IntPtr other);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr value, IntPtr other, IntPtr third);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr SendAttachment(IntPtr receiver, IntPtr selector, IntPtr identifier, IntPtr url, IntPtr options, IntPtr* error);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void SendOptions(IntPtr receiver, IntPtr selector, nuint options, IntPtr handler);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr SendText(IntPtr receiver, IntPtr selector, string text);

    // UNAuthorizationOptions: sound and alert, as Windows' toasts.
    private const nuint AskSound = 1 << 1, AskAlert = 1 << 2;
    // UNNotificationPresentationOptions: Mira in front still shows it, in the list and as a banner, with its sound.
    private const nuint ShowSound = 1 << 1, ShowList = 1 << 3, ShowBanner = 1 << 4;

    /// <summary>A notice shown (or that would have been, for the checks).</summary>
    public sealed record Notice(string Title, string Text, string Group, bool Picture);

    private enum Consent { Unknown, Asking, Granted, Refused }
    private static readonly object Gate = new();
    private static readonly List<Notice> Told = [];
    private static readonly Dictionary<string, Action> Clicks = [];
    private static readonly Queue<string> ClickOrder = new();
    private static readonly List<(Notice Notice, string? Image, Action Clicked, TimeSpan? Expires)> Waiting = [];
    private static Consent _consent;
    private static bool _ready, _tried;
    private static IntPtr _center, _delegate, _askBlock;
    private static long _count;

    /// <summary>No notification of macOS (the checks, as Windows' validation profiles): only <see cref="Shown"/>.</summary>
    public static bool Quiet { get; set; }
    /// <summary>Where an error in a click's handling goes (errors.log), since it cannot be thrown through macOS.</summary>
    public static Action<Exception>? Failed;
    /// <summary>Every notice asked for since Mira started, the latest last (for the self-check).</summary>
    public static IReadOnlyList<Notice> Shown { get { lock (Gate) return [.. Told]; } }
    /// <summary>macOS lets Mira show them (asked, and allowed): false before the first notice, outside Mira.app, and for the checks.</summary>
    public static bool Allowed => _consent == Consent.Granted;

    /// <summary>
    /// Shows one: <paramref name="clicked"/> runs on the interface's thread when it is clicked while Mira runs; with
    /// <paramref name="expires"/>, it leaves the notification centre after that time (Mira still open), otherwise macOS
    /// keeps it. <paramref name="image"/>: a JPEG or PNG file, moved by macOS into its own store.
    /// </summary>
    public static void Show(string title, string text, string? image, Action clicked, string group = "episodes", TimeSpan? expires = null)
    {
        var notice = new Notice(title, text, group, image is not null);
        lock (Gate) { Told.Add(notice); if (Told.Count > 32) Told.RemoveAt(0); }
        if (Quiet || !Prepare()) return;
        Consent consent;
        lock (Gate)
        {
            consent = _consent;
            if (consent is Consent.Unknown or Consent.Asking) { Waiting.Add((notice, image, clicked, expires)); if (Waiting.Count > 8) Waiting.RemoveAt(0); }
            if (consent == Consent.Unknown) _consent = Consent.Asking;
        }
        if (consent == Consent.Granted) Post(notice, image, clicked, expires);
        else if (consent == Consent.Unknown) Ask();
    }

    /// <summary>
    /// The notification centre and Mira's delegate, once: only inside an app bundle with its identifier, where macOS has
    /// the framework. False otherwise, for good.
    /// </summary>
    private static bool Prepare()
    {
        if (_tried) return _ready;
        _tried = true;
        if (!OperatingSystem.IsMacOS() || !NativeLibrary.TryLoad(UserNotifications, out _)) return false;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var bundle = Send(objc_getClass("NSBundle"), sel_registerName("mainBundle"));
            if (bundle == IntPtr.Zero || Send(bundle, sel_registerName("bundleIdentifier")) == IntPtr.Zero) return false;
            if (Utf8(Send(bundle, sel_registerName("bundlePath"))) is not { } path || !path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return false;
            _center = Send(objc_getClass("UNUserNotificationCenter"), sel_registerName("currentNotificationCenter"));
            if (_center == IntPtr.Zero) return false;
            var type = objc_getClass("MiraNotificationDelegate");
            if (type == IntPtr.Zero)
            {
                type = objc_allocateClassPair(objc_getClass("NSObject"), "MiraNotificationDelegate", 0);
                if (type == IntPtr.Zero) return false;
                if (objc_getProtocol("UNUserNotificationCenterDelegate") is var protocol && protocol != IntPtr.Zero) class_addProtocol(type, protocol);
                class_addMethod(type, sel_registerName("userNotificationCenter:willPresentNotification:withCompletionHandler:"), &WillPresent, "v@:@@@?");
                class_addMethod(type, sel_registerName("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"), &Received, "v@:@@@?");
                objc_registerClassPair(type);
            }
            // The centre holds its delegate weakly: Mira keeps this one for as long as it runs.
            _delegate = Send(Send(type, sel_registerName("alloc")), sel_registerName("init"));
            if (_delegate == IntPtr.Zero) return false;
            Send(_center, sel_registerName("setDelegate:"), _delegate);
            return _ready = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary>macOS asks the person (once: afterwards it answers at once with what was chosen).</summary>
    private static void Ask()
    {
        var pool = objc_autoreleasePoolPush();
        try { SendOptions(_center, sel_registerName("requestAuthorizationWithOptions:completionHandler:"), AskAlert | AskSound, AskBlock()); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { lock (Gate) { _consent = Consent.Refused; Waiting.Clear(); } }
        finally { objc_autoreleasePoolPop(pool); }
    }
    [UnmanagedCallersOnly]
    private static void Answered(IntPtr block, byte granted, IntPtr error)
    {
        // Nothing may leave this method: an exception through native code ends the app.
        try
        {
            List<(Notice, string?, Action, TimeSpan?)> waiting;
            lock (Gate) { _consent = granted != 0 ? Consent.Granted : Consent.Refused; waiting = [.. Waiting]; Waiting.Clear(); }
            if (granted == 0) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { foreach (var (notice, image, clicked, expires) in waiting) Post(notice, image, clicked, expires); });
        }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }

    private static void Post(Notice notice, string? image, Action clicked, TimeSpan? expires)
    {
        var id = $"mira-{Environment.ProcessId}-{Interlocked.Increment(ref _count)}";
        var pool = objc_autoreleasePoolPush();
        try
        {
            var content = Send(Send(objc_getClass("UNMutableNotificationContent"), sel_registerName("alloc")), sel_registerName("init"));
            if (content == IntPtr.Zero) return;
            Send(content, sel_registerName("setTitle:"), Text(notice.Title));
            Send(content, sel_registerName("setBody:"), Text(notice.Text));
            // One stack per kind in the notification centre, as Windows' groups.
            Send(content, sel_registerName("setThreadIdentifier:"), Text(notice.Group));
            Send(content, sel_registerName("setSound:"), Send(objc_getClass("UNNotificationSound"), sel_registerName("defaultSound")));
            if (image is not null && File.Exists(image))
            {
                var url = Send(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), Text(image));
                IntPtr error = IntPtr.Zero;
                var attachment = url == IntPtr.Zero ? IntPtr.Zero : SendAttachment(objc_getClass("UNNotificationAttachment"), sel_registerName("attachmentWithIdentifier:URL:options:error:"), Text("picture"), url, IntPtr.Zero, &error);
                // A picture macOS does not take: the notice goes without it.
                if (attachment != IntPtr.Zero) Send(content, sel_registerName("setAttachments:"), Send(objc_getClass("NSArray"), sel_registerName("arrayWithObject:"), attachment));
            }
            var request = Send(objc_getClass("UNNotificationRequest"), sel_registerName("requestWithIdentifier:content:trigger:"), Text(id), content, IntPtr.Zero);
            objc_release(content);
            if (request == IntPtr.Zero) return;
            lock (Gate)
            {
                Clicks[id] = clicked; ClickOrder.Enqueue(id);
                while (ClickOrder.Count > 24) Clicks.Remove(ClickOrder.Dequeue());
            }
            Send(_center, sel_registerName("addNotificationRequest:withCompletionHandler:"), request, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return; }
        finally { objc_autoreleasePoolPop(pool); }
        // Someone who joined an hour ago is no longer news: taken out of the notification centre, as on Windows.
        if (expires is { } after) Avalonia.Threading.DispatcherTimer.RunOnce(() => Remove(id), after);
    }
    private static void Remove(string id)
    {
        var pool = objc_autoreleasePoolPush();
        try { Send(_center, sel_registerName("removeDeliveredNotificationsWithIdentifiers:"), Send(objc_getClass("NSArray"), sel_registerName("arrayWithObject:"), Text(id))); }
        finally { objc_autoreleasePoolPop(pool); }
        lock (Gate) Clicks.Remove(id);
    }

    /// <summary>Mira in front when a notice arrives: macOS still shows it (it would not, by default).</summary>
    [UnmanagedCallersOnly]
    private static void WillPresent(IntPtr self, IntPtr selector, IntPtr center, IntPtr notification, IntPtr handler)
    {
        try { if (handler != IntPtr.Zero) ((delegate* unmanaged<IntPtr, nuint, void>)Invoke(handler))(handler, ShowList | ShowBanner | ShowSound); }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }
    /// <summary>A notice clicked: its action on the interface's thread (macOS has brought Mira forward already).</summary>
    [UnmanagedCallersOnly]
    private static void Received(IntPtr self, IntPtr selector, IntPtr center, IntPtr response, IntPtr handler)
    {
        try
        {
            var request = response == IntPtr.Zero ? IntPtr.Zero : Send(Send(response, sel_registerName("notification")), sel_registerName("request"));
            if (request != IntPtr.Zero && Utf8(Send(request, sel_registerName("identifier"))) is { } id)
            {
                Action? clicked;
                lock (Gate) Clicks.TryGetValue(id, out clicked);
                if (clicked is not null) Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try { clicked(); }
                    catch (Exception ex) when (Errors.Expected(ex)) { Failed?.Invoke(ex); }
                });
            }
        }
        catch (Exception ex) { Failed?.Invoke(ex); }
        finally
        {
            try { if (handler != IntPtr.Zero) ((delegate* unmanaged<IntPtr, void>)Invoke(handler))(handler); }
            catch (Exception ex) { Failed?.Invoke(ex); }
        }
    }

    /// <summary>A block's function: its third field, as clang lays a block out.</summary>
    private static IntPtr Invoke(IntPtr block) => *(IntPtr*)((byte*)block + 16);
    /// <summary>
    /// The completion handler of the request for consent: a global block (it captures nothing), made once, as clang lays
    /// one out (see MacNowPlaying). macOS calls it on a thread of its own.
    /// </summary>
    private static IntPtr AskBlock()
    {
        if (_askBlock != IntPtr.Zero) return _askBlock;
        var isa = NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_NSConcreteGlobalBlock");
        var signature = Marshal.StringToCoTaskMemUTF8("v20@?0B8@12");
        var descriptor = (nuint*)NativeMemory.AllocZeroed(3, (nuint)sizeof(nuint));
        descriptor[1] = 32; descriptor[2] = (nuint)signature;
        var block = (byte*)NativeMemory.AllocZeroed(32);
        *(IntPtr*)block = isa;
        *(int*)(block + 8) = (1 << 28) | (1 << 30); // BLOCK_IS_GLOBAL | BLOCK_HAS_SIGNATURE
        *(IntPtr*)(block + 16) = (IntPtr)(delegate* unmanaged<IntPtr, byte, IntPtr, void>)&Answered;
        *(nuint**)(block + 24) = descriptor;
        return _askBlock = (IntPtr)block;
    }

    private static IntPtr Text(string text) => SendText(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), text);
    private static string? Utf8(IntPtr text)
    {
        if (text == IntPtr.Zero) return null;
        var bytes = Send(text, sel_registerName("UTF8String"));
        return bytes == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(bytes);
    }
}
