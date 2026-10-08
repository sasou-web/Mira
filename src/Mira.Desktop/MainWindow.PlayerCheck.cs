using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mira.Desktop.Services;

namespace Mira.Desktop;

public partial class MainWindow
{
    // Explicit integration fixture; only synthetic local media, a separate profile, and demo mode.
    private async Task RunPlayerCheckAsync()
    {
        var index = Array.IndexOf(_args, "--player-check"); var output = Path.GetFullPath(_args[index + 1]); Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add("PASS " + message); }
        async Task Until(Func<bool> predicate)
        { var timer = Stopwatch.StartNew(); while (!predicate()) { if (timer.Elapsed.TotalSeconds > 8) throw new TimeoutException("Native player did not become ready."); await Task.Delay(50); } }
        try
        {
            if (!_demo || _testMedia is null) throw new InvalidOperationException("Player checks require --demo and --test-media, with an isolated --data profile.");
            _settings.Volume = 0; _settings.AutoNext = false; VolumeSlider.Value = 0;
            await PlayAsync(DemoLibrary.Items()[0]); await Until(() => _loaded);
            Require(_mpv is not null && _videoHandle != IntPtr.Zero, "native video host loads the synthetic clip");
            Require(PlayerLoading.Visibility == Visibility.Collapsed && System.Windows.Input.Mouse.OverrideCursor is null, "loading indicator and busy cursor clear once the first frame is ready");
            ShowPlayerControls(); HidePlayerControls();
            Require(!_controlsVisible && PlayerSurface.IsHitTestVisible && PlayerSurface.Cursor == System.Windows.Input.Cursors.None && !PlayerControls.IsHitTestVisible, "hidden controls keep a mouse-sensitive video surface and hide the cursor");
            await Task.Delay(70); ShowPlayerControls(); await Task.Delay(260);
            Require(_controlsVisible && PlayerControls.IsHitTestVisible && PlayerControls.Opacity > .99 && PlayerSurface.Cursor == System.Windows.Input.Cursors.Arrow, "mouse activity can interrupt the fade without a stale hide closing the controls");
            await RunPlayerV044ChecksAsync(output, checks, Require);
            await RunPlayerBarChecksAsync(output, checks, Require);
            _mpv!.Set("pause", "yes"); await Task.Delay(200);
            var engine = _mpv; var handle = _videoHandle;
            _mpv.Command("seek", "4", "absolute+exact"); await Task.Delay(240); _mpv.Poll();
            Require(Math.Abs(_mpv.Number("time-pos") - 4) < .3, "seek moves to four seconds");
            SetMiniPlayer(true); UpdateLayout(); await Task.Delay(150);
            Require(_miniPlayer && LibraryShell.IsVisible && PlayerShell.ActualWidth == MiniWidth && ReferenceEquals(engine, _mpv) && _videoHandle == handle, "mini-player preserves the video engine and exposes the library");
            await RunMiniPlayerChecksAsync(output, checks, Require, engine);
            _view = "library"; await NavigateLibraryAsync();
            Require(_miniPlayer && _playing && ReferenceEquals(engine, _mpv), "navigation keeps the mini-player alive");
            SetMiniPlayer(false); await Task.Delay(120);
            Require(!_miniPlayer && !LibraryShell.IsVisible && PlayerShell.ActualWidth > 900, "expanded player restores its full layout");
            SetVolumeReveal(true); UpdateLayout(); await Task.Delay(260);
            double Middle(FrameworkElement element) => element.TranslatePoint(new Point(0, element.ActualHeight / 2), PlayerSurface).Y;
            var volumeCenter = Middle(MuteButton);
            Require(Math.Abs(volumeCenter - Middle(VolumeSlider)) < 1 && Math.Abs(volumeCenter - Middle(PauseButton)) < 1 && Math.Abs(volumeCenter - Middle(PlayerMoreButton)) < 1 && Math.Abs(volumeCenter - Middle(FullscreenButton)) < 1,
                "play, volume, slider, menus and fullscreen share one vertical center");
            SetVolumeReveal(false);
            var playGlyph = FindVisual<Views.Icon>(PauseButton)!;
            Require(playGlyph.TranslatePoint(new Point(0, playGlyph.ActualHeight), PlayerSurface).Y <= PlayerSurface.Height - 20, "bottom controls stay inside the video with breathing room");
            var original = new Rect(Left, Top, Width, Height);
            ToggleFullscreen(); await Task.Delay(200);
            var pixels = _fullscreenWindow!.WindowPixels();
            checks.Add($"INFO fullscreen rectangle {pixels}; logical size {ActualWidth} × {ActualHeight}");
            Require(_fullscreen && TitleBar.Visibility == Visibility.Collapsed && Math.Abs(pixels.Width - 1600) < 2 && Math.Abs(pixels.Height - 900) < 2, "fullscreen uses the requested screen rectangle including its full height");
            Require(PlayerWindowChrome.Visibility == Visibility.Collapsed && Grid.GetRowSpan(PlayerShell) == 2 && ReferenceEquals(engine, _mpv) && _videoHandle == handle, "fullscreen retains the same full-height video host and hides window chrome");
            Require(!HasCaptionStyle() && WindowStyle == WindowStyle.None, "fullscreen drops the native caption style: no title bar or border can appear");
            ToggleFullscreen(); await Task.Delay(200);
            Require(!_fullscreen && Math.Abs(Width - original.Width) < 2 && Math.Abs(Height - original.Height) < 2 && !TitleBar.IsVisible && PlayerWindowChrome.Visibility == Visibility.Visible, "leaving fullscreen restores the prior window dimensions");
            Require(HasCaptionStyle() && WindowStyle == WindowStyle.SingleBorderWindow, "leaving fullscreen restores the caption style used by Windows animations");
            VolumeSlider.Value = 20;
            Mute_Click(this, new()); Require(_mpv.Flag("mute"), "mute control changes the native audio state");
            Mute_Click(this, new()); Require(!_mpv.Flag("mute"), "unmute restores the native audio state");
            VolumeSlider.Value = 0; Mute_Click(this, new());
            Require(!_mpv.Flag("mute") && Math.Abs(VolumeSlider.Value - 20) < .1, "unmuting zero volume restores the last audible level");
            VolumeSlider.Value = 150;
            Require(Math.Abs(_mpv.Level - 150) < .1 && Math.Abs(_mpv.Number("volume") - 100) < .1 && (_mpv.Get("af") ?? "").Contains(Mira.Core.VolumeBoost.Label) && VolumeText.Text == "150 %",
                "above 100 %, mpv stays at 100 and the boost filter adds the rest");
            VolumeSlider.Value = 0;
            More_Click(this, new()); UpdateLayout(); await Task.Delay(100);
            ChooseSpeed(1.25); Require(Math.Abs(_mpv.Number("speed") - 1.25) < .001, "speed menu selection reaches mpv");
            SubtitleDelay_Click(new Button { Tag = "0.5" }, new());
            Require(Math.Abs(_mpv.Number("sub-delay") - .5) < .001 && Equals(SubtitleDelayReset.Content, DelayLabel(.5)), "subtitle timing buttons reach mpv and show the new offset");
            var panel = (FrameworkElement)PlayerOptionsPopup.Child;
            var dpi = VisualTreeHelper.GetDpi(PlayerShell);
            var panelPixels = new Size(panel.ActualWidth * dpi.DpiScaleX, panel.ActualHeight * dpi.DpiScaleY);
            var shellPixels = new Size(PlayerShell.ActualWidth * dpi.DpiScaleX, PlayerShell.ActualHeight * dpi.DpiScaleY);
            var placement = PlayerOptionsPopup.CustomPopupPlacementCallback(panelPixels, shellPixels, new())[0].Point;
            var moreRight = PlayerMoreButton.TranslatePoint(new Point(PlayerMoreButton.ActualWidth, 0), PlayerSurface).X * dpi.DpiScaleX;
            Require(Math.Abs(placement.X + panelPixels.Width - moreRight) < 1 && placement.Y >= 0 && placement.Y + panelPixels.Height < shellPixels.Height, "the ⋮ menu aligns on its button inside the player at current DPI");
            PlayerOptionsPopup.IsOpen = false;
            var messageSeen = false; _mpv.Message += message => { if (message == "mira-fullscreen") messageSeen = true; };
            // The command is routed by input.conf exactly as a native F key would be.
            _mpv.Command("keypress", "f"); await Until(() => messageSeen && _fullscreen);
            Require(messageSeen && _fullscreen, "native keyboard F reaches the application fullscreen action");
            _mpv.Command("keypress", "ESC"); await Until(() => !_fullscreen);
            Require(!_fullscreen, "native Escape exits fullscreen without stopping playback");
            ToggleFullscreen();
            await PlayAsync(DemoLibrary.Items()[1]); await Until(() => _loaded);
            _mpv!.Set("pause", "yes");
            Require(_fullscreen && TitleBar.Visibility == Visibility.Collapsed, "starting the next video preserves fullscreen");
            SetMiniPlayer(true);
            await PlayAsync(DemoLibrary.Items()[0], keepMini: true); await Until(() => _loaded);
            _mpv!.Set("pause", "yes");
            Require(_miniPlayer && !_fullscreen && LibraryShell.IsVisible, "next video preserves mini-player browsing mode");
            await StopPlaybackAsync(); ReturnToLibrary();
            Require(!_playing && _mpv is null && LibraryShell.IsVisible && !PlayerOverlayLayer.IsOpen, "stop releases mpv and closes player overlays");
            await RunCompletionChecksAsync(Require);
            await File.WriteAllLinesAsync(Path.Combine(output, "result.txt"), checks);
        }
        catch (Exception ex) { await File.WriteAllLinesAsync(Path.Combine(output, "result.txt"), checks.Append("FAIL " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace)); }
        finally { Close(); }
    }
}
