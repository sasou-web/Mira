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
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, e) => { if (e.NewValue is true) _open.Add(popup); else _open.Remove(popup); });
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
