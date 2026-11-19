using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>
/// Every popup of Mira (flyouts of the player and the pages, context menus, drop-downs) closed once Mira is no longer
/// in front. On macOS a popup is a window of its own, floating above every app's windows: light dismiss only hears
/// clicks inside Mira, so a menu left open while the person went to another app (or Mira closed into the Dock) stayed
/// alone over the desktop (0.9.12, « Search for subtitles » over Chrome).
/// </summary>
public static class OpenPopups
{
    private static readonly HashSet<Popup> _open = [];
    private static bool _listening;

    /// <summary>Starts following the popups that open and close; once, at the main window's start.</summary>
    public static void Follow()
    {
        if (_listening) return;
        _listening = true;
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, e) =>
        {
            if (e.NewValue is true) { if (_open.Add(popup) && _open.Count == 1) Watch(); }
            else _open.Remove(popup);
        });
    }

    // macOS: while a popup is open, Mira itself is watched every 100 ms (Watch). A window's Deactivated was not enough
    // (0.9.14, an episode's menu alone over Chrome): a menu opened from a window that was no longer key (a right-click
    // on Mira behind another app: AppKit gives it to the window without making Mira active) or a menu that took the
    // focus from its window leaves no Deactivated to hear when the person then goes elsewhere.
    private static DispatcherTimer? _watch;
    private static OrphanWatch? _rule;

    private static void Watch()
    {
        if (MacApplication.Active is not { } active) return;
        _rule = new OrphanWatch(active, MacApplication.MousePressed);
        _watch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick());
        _watch.Start();
    }

    private static void Tick()
    {
        if (_open.Count == 0 || _rule is null) { _watch?.Stop(); _rule = null; return; }
        if (MacApplication.Active is not { } active) return;
        if (_rule.Left(active, MacApplication.MousePressed, Environment.TickCount64)) { CloseAll(); _watch?.Stop(); _rule = null; }
    }

    /// <summary>The popups open now (for the self-check).</summary>
    public static int Count => _open.Count;

    /// <summary>Closes them all; a flyout or a context menu hears its popup close and ends as by light dismiss.</summary>
    public static void CloseAll()
    {
        foreach (var popup in _open.ToArray())
            try { popup.IsOpen = false; } catch (Exception ex) when (ex is InvalidOperationException) { }
        _open.Clear();
    }

    /// <summary>
    /// A window of Mira lost the focus: once the change has settled, the popups close if no window of Mira has it any
    /// more (on macOS: Mira itself no longer the active app). A popup Mira opened, or Mira's own picture-in-picture,
    /// keeps them.
    /// </summary>
    public static void WhenLeft(Window window)
    {
        if (_open.Count == 0) return;
        DispatcherTimer.RunOnce(() => { if (!StillInFront(window)) CloseAll(); }, TimeSpan.FromMilliseconds(200));
    }

    private static bool StillInFront(Window window)
    {
        if (!window.IsVisible) return false;
        if (MacApplication.Active is { } active) return active;
        return window.IsActive || Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { Windows: var windows } && windows.Any(other => other.IsActive);
    }
}

/// <summary>
/// When Mira's open popups are left alone (macOS): Mira was the app in front since they opened and no longer is; or
/// they opened while Mira was behind another app (a right-click on its window) and a new click, 250 ms later, has still
/// not brought Mira in front (a click elsewhere: a click on the menu itself makes Mira active).
/// </summary>
public sealed class OrphanWatch(bool active, bool pressed)
{
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);
    private bool _wasActive = active, _pressed = pressed;
    private long? _pressAt;

    /// <summary>One look at Mira (active app, a mouse button down anywhere, the time in ms); true = close the popups.</summary>
    public bool Left(bool active, bool pressed, long now)
    {
        if (active) { _wasActive = true; _pressAt = null; _pressed = pressed; return false; }
        if (_wasActive) return true;
        if (pressed && !_pressed) _pressAt ??= now;
        _pressed = pressed;
        return _pressAt is { } at && now - at >= Settle.TotalMilliseconds;
    }
}
