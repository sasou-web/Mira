using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Mira.Desktop;

public partial class MainWindow
{
    // Explicit opt-in diagnostics for real mouse testing on a synthetic, muted clip.
    private void InitializePlayerProbe()
    {
        if (!_args.Contains("--qa-window") || !_args.Contains("--demo") || !_args.Contains("--test-media")) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        string? previous = null;
        timer.Tick += (_, _) =>
        {
            var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
            var cursorKnown = GetCursorInfo(ref cursor);
            var state = JsonSerializer.Serialize(new
            {
                playing = _playing, loaded = _loaded, paused = _paused, fullscreen = _fullscreen, mini = _miniPlayer,
                active = IsActive, controlsVisible = _controlsVisible, overlayOpen = PlayerOverlayLayer.IsOpen,
                windowVisible = IsVisible, windowState = WindowState.ToString(), left = Left, top = Top,
                foreground = GetForegroundWindow().ToInt64(), overlayHandle = PlayerOverlayLayer.Handle.ToInt64(),
                pointerHidden = PlayerSurface.Cursor == Cursors.None, cursorKnown, osCursorShowing = (cursor.Flags & 1) != 0,
                mouseMoves = _surfaceMotionCount, surfaceWidth = PlayerSurface.ActualWidth, surfaceHeight = PlayerSurface.ActualHeight,
                videoWidth = PlayerShell.ActualWidth, videoHeight = PlayerShell.ActualHeight,
                chromeVisible = PlayerWindowChrome.IsVisible && PlayerWindowChrome.Opacity > .01,
                controlsOpacity = Math.Round(PlayerControls.Opacity, 2), hoverVisible = _hoverCard?.HoverVisible == true
            });
            if (state == previous) return; previous = state;
            File.AppendAllText(Path.Combine(_profile.DirectoryPath, "player-ui-probe.jsonl"), state + "\n");
        };
        Closed += (_, _) => timer.Stop(); timer.Start();
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size, Flags; public IntPtr Handle; public int X, Y; }
    [DllImport("user32")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
}
