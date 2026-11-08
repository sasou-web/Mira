using Avalonia;
using Avalonia.Controls;
using Mira.Core;

namespace Mira.Mac.Views;

// The window as a Mac app's: it opens where it was left, on the same screen, and closing it (⌘W, the red button) keeps
// Mira in the Dock, a click on its icon bringing it back; ⌘Q quits. The checks keep the old way: closing ends them.
public sealed partial class MainWindow
{
    /// <summary>A check runs (it ends by closing the window): Mira never stays in the Dock for one.</summary>
    public bool IsCheckRun => Args.Any(x => x is "--self-check" or "--gallery" or "--smooth-check" or "--tailnet-check");
    /// <summary>Closing the window keeps Mira running: a real Mac, outside the checks.</summary>
    private bool StaysInDock => OperatingSystem.IsMacOS() && !IsCheckRun;
    /// <summary>The window closed with ⌘W or its red button, Mira still running in the Dock.</summary>
    public bool InDock { get; private set; }
    private bool _quitting;

    /// <summary>
    /// Where the window was left (its top-left corner in the units of the windows' positions: points on a Mac), when
    /// enough of its title bar would still be on one of the screens present (<see cref="ScreenFit.Place"/>); otherwise
    /// centred, as before. Its size fits that screen. Before the window shows, so that it never jumps.
    /// </summary>
    private void PlaceWindow()
    {
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (Settings.WindowX is not { } x || Settings.WindowY is not { } y || Screens is not { } screens) return;
        // macOS counts the screens and the windows in points (PipWindow.ScreensOf measures it once a window shows; this
        // one has not yet), X11, where the checks run, in pixels.
        var all = screens.All.Select(s => new ScreenFit.Screen(new ScreenFit.Pixels(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height), OperatingSystem.IsMacOS() ? 1 : s.Scaling)).ToList();
        if (ScreenFit.Place(new(Width, Height), new(MinWidth, MinHeight), all, (x, y), null) is not { Remembered: true } placement) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint((int)placement.Window.Left, (int)placement.Window.Top);
        (Width, Height) = (placement.Fit.Size.Width, placement.Fit.Size.Height);
    }
    /// <summary>The window as the person left it; full screen is a passing state, not a place to reopen at.</summary>
    private void RememberWindow()
    {
        if (WindowState is WindowState.Normal) (Settings.WindowWidth, Settings.WindowHeight) = (Math.Round(Width), Math.Round(Height));
        // A zoomed window keeps its screen: it reopens there, zoomed again.
        if (WindowState is WindowState.Normal or WindowState.Maximized) (Settings.WindowX, Settings.WindowY) = (Position.X, Position.Y);
        if (WindowState is not WindowState.FullScreen and not WindowState.Minimized) Settings.WindowMaximized = WindowState == WindowState.Maximized;
    }

    /// <summary>Quits Mira for good (« Redémarrer », « Relancer »), even where closing the window only puts it in the Dock.</summary>
    public void Quit() { _quitting = true; Close(); }

    /// <summary>
    /// ⌘W or the red button: the window goes, Mira stays in the Dock. What plays stops as on quitting (its progress
    /// reported, the group told); the account, « Tailscale intégré » and a party stay, for the window that comes back.
    /// </summary>
    public async Task CloseToDockAsync()
    {
        if (InDock || _closing) return;
        InDock = true;
        RememberWindow();
        try { Profile.SaveSettings(Settings); } catch (Exception ex) { ErrorLog.Append(Profile.DirectoryPath, ex); }
        Player?.Pip?.Hide();
        try { await StopPlaybackAsync(); } catch (Exception ex) { ErrorLog.Append(Profile.DirectoryPath, ex); }
        // A full-screen window hidden would leave its empty Space behind: back in the desktop first, as macOS slides it.
        if (WindowState == WindowState.FullScreen) { WindowState = WindowState.Normal; await Task.Delay(900); RememberWindow(); }
        if (InDock && !_closing) Hide();
    }
    /// <summary>A click on the Dock icon, a mira:// link, a menu item while the window is closed: the window back, in front.</summary>
    public void Reopen()
    {
        if (_closing) return;
        if (InDock) { InDock = false; Show(); }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
