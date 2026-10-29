using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>Windows' accessibility settings, the keyboard, screen readers, and what Mira says when something goes wrong.</summary>
public partial class MainWindow
{
    /// <summary>
    /// High contrast: secondary text as bright as the rest, and the focus ring in Windows' highlight colour. The rest
    /// keeps Mira's dark palette. Read when Mira starts, before the pages load their colours.
    /// </summary>
    private static void FollowHighContrast()
    {
        if (!SystemParameters.HighContrast || Application.Current is not { } app) return;
        app.Resources["Muted"] = Frozen("#E4E4E7"); app.Resources["Faint"] = Frozen("#D4D4D9");
        app.Resources["FocusRing"] = SystemColors.HighlightBrush;
    }

    /// <summary>Windows' « Taille du texte » (Accessibility, Text size), from 1 to 2.25; 1 when it cannot be read.</summary>
    private static double TextScale()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            return key?.GetValue("TextScaleFactor") is int percent and >= 100 and <= 225 ? percent / 100d : 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return 1; }
    }
    private double _textScale = 1, _zoom = 1;
    /// <summary>The width Mira's pages lay out in: the window's, divided by their zoom (see <see cref="UpdateZoom"/>).</summary>
    private double LayoutWidth => ActualWidth / _zoom;
    private double LayoutHeight => ActualHeight / _zoom;
    /// <summary>
    /// Mira's pages grow with Windows' text size, as far as the window holds their smallest layout (960 × 600): each
    /// page is zoomed whole, its text with it. The player and the title bar keep their size. True when the zoom changed.
    /// </summary>
    private bool UpdateZoom()
    {
        if (LibraryShell is null || Content is not FrameworkElement { ActualWidth: > 0 } root) return false;
        var zoom = ValidationRun ? 1 : Legibility.Zoom(_textScale, root.ActualWidth, root.ActualHeight, 960, 600);
        if (Math.Abs(zoom - _zoom) < .005) return false;
        _zoom = zoom;
        var scale = zoom == 1 ? Transform.Identity : new ScaleTransform(zoom, zoom);
        if (scale.CanFreeze) scale.Freeze();
        foreach (var page in new FrameworkElement[] { LibraryShell, NavigationRail, TorLinkOverlay, DetailOverlay, SettingsOverlay, GuideOverlay, LoginOverlay, WelcomeOverlay, WhatsNewOverlay, InviteOverlay, IdentifyOverlay, CollectionOverlay, Notice })
            page.LayoutTransform = scale;
        // Artwork shown larger is decoded larger, so that it stays sharp.
        if (_images is { } cache) FitArtwork(cache);
        return true;
    }

    /// <summary>
    /// Ctrl+1 to Ctrl+5: the rail's pages (Accueil, Films, Séries, Toute la bibliothèque, Téléchargements), as a click
    /// on them; the focus goes to that button of the rail.
    /// </summary>
    private bool RailShortcut(Key key)
    {
        var index = key is >= Key.D1 and <= Key.D5 ? key - Key.D1 : key is >= Key.NumPad1 and <= Key.NumPad5 ? key - Key.NumPad1 : -1;
        if (index < 0) return false;
        // Not from a screen drawn over the rail (sign-in, welcome, what's new), where a click could not reach it either.
        if (!NavigationRail.IsVisible || LoginOverlay.Visibility == Visibility.Visible || WelcomeOverlay.Visibility == Visibility.Visible || WhatsNewOverlay.Visibility == Visibility.Visible || InviteOverlay.Visibility == Visibility.Visible || IdentifyOverlay.Visibility == Visibility.Visible || CollectionOverlay.Visibility == Visibility.Visible) return false;
        var button = new[] { HomeNav, MoviesNav, SeriesNav, LibraryNav, TorLinkNav }[index];
        if (!button.IsVisible || !button.IsEnabled) return false;
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button)); button.Focus();
        return true;
    }

    /// <summary>« Hors connexion » shown in the title bar while Jellyfin does not answer.</summary>
    private bool _offlineShown;
    /// <param name="status">The status line (SyncStatus), whose words the title bar takes: « Hors connexion · 2 envois en attente ».</param>
    private void ShowOffline(bool offline, string status)
    {
        if (offline && status.TrimStart('○', ' ') is var text && OfflineText.Text != text) OfflineText.Text = text;
        if (offline == _offlineShown) return;
        _offlineShown = offline;
        if (offline) { Motion.Reveal(OfflineBadge, 200, 4); return; }
        _ = Motion.HideAsync(OfflineBadge);
        // The other half of the message said when it stopped answering (RefreshAsync), under an error still waiting for
        // you; the one about Jellyfin on this PC (stopped, starting) is over, and replaced.
        if (_noticeSticky && NoticeText.Text != _localJellyfinNotice) AddNotice(JellyfinBack); else SetNotice(JellyfinBack);
    }
    private static string JellyfinBack => L("Jellyfin répond de nouveau.", "Jellyfin is answering again.");
    private async void OfflineRetry_Click(object sender, RoutedEventArgs e)
    {
        OfflineRetry.IsEnabled = false;
        try { await RefreshAsync(); }
        finally { OfflineRetry.IsEnabled = true; }
        if (_client is { } client && _sync is { } sync && SyncStatus.Offline(sync.LastSynced, _offlineSince))
            await SayUnansweredAsync(client, null, L("Jellyfin ne répond toujours pas. Mira réessaie tout seul et te le dira dès qu’il répond.", "Jellyfin still isn’t answering. Mira keeps trying on its own and will tell you as soon as it answers."));
    }

    private DateTime _lastUnexpected = DateTime.MinValue;
    /// <summary>
    /// An unexpected error, already written to errors.log (App): said in Mira's own message, which stays with the log
    /// at hand, at most once a minute (the same error repeating would otherwise say it again and again).
    /// </summary>
    internal void ReportUnexpected()
    {
        var now = DateTime.UtcNow;
        if (now - _lastUnexpected < TimeSpan.FromMinutes(1)) return;
        _lastUnexpected = now;
        SetNotice(L("Une opération n’a pas abouti : Mira a rencontré une erreur imprévue, notée dans son journal. Tu peux réessayer ; ta progression déjà enregistrée est conservée.", "Something didn’t go through: Mira ran into an unexpected error, noted in its log. You can try again; the progress already saved is kept."), L("Ouvrir le journal", "Open the log"), OpenErrorLog, sticky: true);
    }
    private void OpenErrorLog()
    {
        var log = Path.Combine(AppFiles.ProfileDirectory, "errors.log");
        try { using var _ = Process.Start(new ProcessStartInfo(log) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // No program opens .log files on this PC: its folder, with the file selected.
            try { using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{log}\"")); }
            catch (Win32Exception) { SetNotice(L("Le journal de Mira est ici : ", "Mira’s log is here: ") + log, null, null, sticky: true); }
        }
    }

    /// <summary>Validation runs: every control shown on a captured screen can be named by a screen reader, and shows a ring under the keyboard.</summary>
    private void AssertNamed(string screen)
    {
        var unnamed = Accessible.Unnamed((DependencyObject)Content);
        if (unnamed.Count > 0)
            throw new InvalidOperationException($"{screen}: {unnamed.Count} control(s) without a name for screen readers: " + string.Join(", ", unnamed.Take(6).Select(Accessible.Describe)) + ".");
        // And every control Tab reaches there shows where the focus is.
        var unringed = Accessible.WithoutFocusRing((DependencyObject)Content, (Style)FindResource("KeyboardFocus"));
        if (unringed.Count > 0)
            throw new InvalidOperationException($"{screen}: {unringed.Count} control(s) Tab reaches without a visible focus ring: " + string.Join(", ", unringed.Take(6).Select(Accessible.Describe)) + ".");
    }

    private const int WM_NCHITTEST = 0x84, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2, WM_NCLBUTTONDBLCLK = 0xA3, WM_NCMOUSELEAVE = 0x2A2, HTMAXBUTTON = 9;
    private static readonly Brush MaximizeHover = Frozen("#1CF4F4F5");
    private bool _maximizeHot, _maximizePressed;
    /// <summary>
    /// Windows 11 opens its snap layouts from a window's maximize button: Mira's own button answers that it is one
    /// (HTMAXBUTTON), so hovering it shows them. Windows then sends its hover and clicks as non-client messages, handled
    /// here. Only the library's title bar: the player's is in a window of its own. Not on Windows 10, which would draw
    /// its classic button there.
    /// </summary>
    private void InitializeSnapLayouts()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || ValidationRun) return;
        // Added after WindowChrome's own hook (set up with the window's handle): the hook added last runs first.
        Loaded += (_, _) => (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(SnapHook);
    }
    private IntPtr SnapHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WM_NCHITTEST:
                var over = OverMaximize(lParam); SetMaximizeHot(over, hwnd);
                if (over) { handled = true; return HTMAXBUTTON; }
                break;
            case WM_NCMOUSELEAVE: SetMaximizeHot(false, hwnd); _maximizePressed = false; break;
            case WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK when wParam.ToInt64() == HTMAXBUTTON: _maximizePressed = true; handled = true; break;
            case WM_NCLBUTTONUP when wParam.ToInt64() == HTMAXBUTTON:
                if (_maximizePressed) Maximize_Click(this, new());
                _maximizePressed = false; handled = true; break;
        }
        return IntPtr.Zero;
    }
    private bool OverMaximize(IntPtr lParam)
    {
        if (_fullscreen || !MaximizeButton.IsVisible) return false;
        // Screen coordinates in device pixels, signed (a screen left of the main one).
        var x = (short)(lParam.ToInt64() & 0xFFFF); var y = (short)(lParam.ToInt64() >> 16 & 0xFFFF);
        var from = MaximizeButton.PointToScreen(new Point()); var to = MaximizeButton.PointToScreen(new Point(MaximizeButton.ActualWidth, MaximizeButton.ActualHeight));
        return x >= Math.Min(from.X, to.X) && x < Math.Max(from.X, to.X) && y >= Math.Min(from.Y, to.Y) && y < Math.Max(from.Y, to.Y);
    }
    /// <summary>The button's hover, which WPF no longer sees once Windows takes the pointer over it.</summary>
    private void SetMaximizeHot(bool hot, IntPtr hwnd)
    {
        if (hot == _maximizeHot) return;
        _maximizeHot = hot;
        if (!hot) { MaximizeButton.ClearValue(BackgroundProperty); return; }
        MaximizeButton.Background = MaximizeHover;
        // WM_NCMOUSELEAVE when the pointer leaves it for the page or another window.
        var track = new TrackMouse { Size = Marshal.SizeOf<TrackMouse>(), Flags = TME_LEAVE | TME_NONCLIENT, Window = hwnd };
        TrackMouseEvent(ref track);
    }
    private const uint TME_LEAVE = 0x2, TME_NONCLIENT = 0x10;
    [StructLayout(LayoutKind.Sequential)] private struct TrackMouse { public int Size; public uint Flags; public IntPtr Window; public uint HoverTime; }
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TrackMouse track);
}
