using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Mira.Desktop.Playback;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    private FullscreenWindow? _fullscreenWindow;
    private WindowFrame? _windowFrame;
    private bool _miniPlayer, _optionsBusy, _controlsVisible;
    private bool _positionQueued, _changingPlayerLayout;
    private Point? _lastSurfacePoint;
    private int _surfaceMotionCount;
    private readonly DispatcherTimer _surfaceClick = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private double _lastAudibleVolume = 75;
    private DateTimeOffset _lastPlayerInteraction = DateTimeOffset.UtcNow, _lastScrub;
    // Playing: controls leave quickly. Paused: they also leave, so the still frame can be captured cleanly.
    private static readonly TimeSpan PlayingIdle = TimeSpan.FromSeconds(2.6), PausedIdle = TimeSpan.FromSeconds(4), MiniIdle = TimeSpan.FromSeconds(1.4);
    private void InitializePlayerControls()
    {
        _fullscreenWindow = new(this);
        _windowFrame = new(this);
        _surfaceClick.Tick += (_, _) => { _surfaceClick.Stop(); if (_playing) Pause_Click(this, new()); };
        if (_settings.Volume > 0) _lastAudibleVolume = _settings.Volume;
        SeekBar.SeekStarted += () => { _seeking = true; ShowPlayerControls(); };
        SeekBar.Seeking += Scrub;
        SeekBar.SeekCompleted += SeekTo;
        // WPF supplies custom placement bounds in device pixels, including at 200% DPI.
        PlayerOptionsPopup.CustomPopupPlacementCallback = (size, target, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(PlayerShell);
            return [new CustomPopupPlacement(new Point(Math.Max(12 * dpi.DpiScaleX, target.Width - size.Width - 34 * dpi.DpiScaleX), Math.Max(12 * dpi.DpiScaleY, target.Height - (PlayerControls.Height - 22) * dpi.DpiScaleY - size.Height)), PopupPrimaryAxis.None)];
        };
        LocationChanged += (_, _) => PositionPlayerControls();
        StateChanged += (_, _) => { PositionPlayerControls(); UpdatePlayerControls(); };
        Activated += (_, _) => { _fullscreenWindow.ActiveChanged(); ShowPlayerControls(); };
        Deactivated += (_, _) => { _fullscreenWindow.ActiveChanged(); ClosePlayerPopups(); };
    }
    private void Player_SizeChanged(object sender, SizeChangedEventArgs e) => PositionPlayerControls();
    private void PositionPlayerControls()
    {
        if (PlayerSurface is null || _changingPlayerLayout || _positionQueued) return;
        _positionQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _positionQueued = false;
            var size = new Size(Math.Max(1, PlayerShell.ActualWidth), Math.Max(1, PlayerShell.ActualHeight));
            var radius = _miniPlayer ? MiniRadius : _windowFrame?.Rounded == true ? WindowFrame.CornerRadius : 0;
            // Rebuilding the clip repaints the whole transparent overlay window: only do it when its shape changes.
            if (size != _surfaceSize || radius != _surfaceRadius)
            {
                _surfaceSize = size; _surfaceRadius = radius;
                PlayerSurface.Width = size.Width; PlayerSurface.Height = size.Height;
                var clip = new RectangleGeometry(new Rect(size), radius, radius); clip.Freeze(); PlayerSurface.Clip = clip;
            }
            PlayerOverlayLayer.Reposition();
            if (PlayerOptionsPopup.IsOpen) { PlayerOptionsPopup.HorizontalOffset = .01; PlayerOptionsPopup.HorizontalOffset = 0; }
        }, DispatcherPriority.Render);
    }
    private Size _surfaceSize;
    private double _surfaceRadius = -1;
    private void ShowPlayerControls()
    {
        _lastPlayerInteraction = DateTimeOffset.UtcNow;
        if (!_playing || (!IsActive && !_args.Contains("--player-check")) || WindowState == WindowState.Minimized) return;
        // The overlay window stays closed while the mini-player is dragged, gliding or tucked away.
        if (_miniPlayer && !MiniSettled) return;
        // Called on every mouse move: layout changes position the overlay themselves, so only a new window needs it.
        if (!PlayerOverlayLayer.IsOpen) PositionPlayerControls();
        // Keep a hit-testable surface above the native video, even after the controls fade.
        // Fully transparent pixels of a layered HWND would let mouse input pass through.
        PlayerOverlayLayer.IsOpen = true;
        PlayerSurface.Cursor = Cursors.Arrow;
        if (_controlsVisible) return;
        _controlsVisible = true;
        SetPlayerPanels(true, 160);
    }
    private void HidePlayerControls()
    {
        if (!_controlsVisible) return;
        _controlsVisible = false;
        SetPlayerPanels(false, 240);
        if (!_miniPlayer) PlayerSurface.Cursor = Cursors.None;
    }
    private void SetPlayerPanels(bool visible, int duration)
    {
        foreach (var panel in new UIElement[] { PlayerHeader, PlayerControls, PlayerWindowChrome })
        { panel.IsHitTestVisible = visible; Motion.Fade(panel, visible ? 1 : 0, duration); }
        SeekBar.Live = visible; UpdateSkip();
    }
    private void ClosePlayerPopups()
    {
        _surfaceClick.Stop(); _controlsVisible = false; _lastSurfacePoint = null;
        PlayerOverlayLayer.IsOpen = PlayerOptionsPopup.IsOpen = false;
        PlayerSurface.Cursor = Cursors.Arrow;
    }
    private void UpdatePlayerControls()
    {
        if (!_playing || WindowState == WindowState.Minimized || (!IsActive && !_args.Contains("--player-check"))) { ClosePlayerPopups(); return; }
        if (_miniPlayer && !MiniSettled) { if (PlayerOverlayLayer.IsOpen) ClosePlayerPopups(); return; }
        if (!_loaded || _seeking || PlayerOptionsPopup.IsOpen || (_keyboardNavigation && (PlayerControls.IsKeyboardFocusWithin || PlayerHeader.IsKeyboardFocusWithin))) return;
        var idle = DateTimeOffset.UtcNow - _lastPlayerInteraction;
        if (_miniPlayer) { if (!PlayerSurface.IsMouseOver && idle > MiniIdle) HidePlayerControls(); return; }
        // While playing, a pointer resting on the controls keeps them. Once paused they leave anyway.
        if (!_paused && _controlsVisible && (PlayerControls.IsMouseOver || PlayerHeader.IsMouseOver || PlayerWindowChrome.IsMouseOver || SkipButton.IsMouseOver)) return;
        if (idle > (_paused ? PausedIdle : PlayingIdle)) HidePlayerControls();
    }
    private void PlayerSurface_MouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(PlayerSurface);
        if (_lastSurfacePoint is { } previous && (point - previous).LengthSquared < 1) return;
        _lastSurfacePoint = point; ++_surfaceMotionCount;
        if (_miniPlayer && _miniPress is { } press && e.LeftButton == MouseButtonState.Pressed && !_miniDragging && (Mouse.GetPosition(Root) - press).Length > 5) { BeginMiniDrag(); return; }
        ShowPlayerControls();
    }
    private void PlayerSurface_MouseLeave(object sender, MouseEventArgs e) => _lastSurfacePoint = null;
    private void PlayerSurface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_miniPlayer) { MiniPointerDown(e); return; }
        if (e.OriginalSource != PlayerSurface) return;
        PlayerSurface.Focus(); ShowPlayerControls(); e.Handled = true;
        if (e.ClickCount == 2) { _surfaceClick.Stop(); ToggleFullscreen(); }
        else { _surfaceClick.Stop(); _surfaceClick.Start(); }
    }
    private void PlayerSurface_MouseUp(object sender, MouseButtonEventArgs e) { if (_miniPlayer) MiniPointerUp(e); }
    private void PlayerChrome_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != PlayerWindowChrome || _fullscreen || _miniPlayer) return;
        if (e.ClickCount == 2) Maximize_Click(sender, e); else DragMove();
        e.Handled = true;
    }
    /// <summary>Player shortcuts are read before any focused control: a clicked button can no longer
    /// replay its action on Space, and a clicked slider no longer swallows the arrows.</summary>
    private void Player_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || !_playing || PlayerOptionsPopup.IsOpen || Keyboard.Modifiers != ModifierKeys.None || FromTorLinkTerminal(e)) return;
        if (e.OriginalSource is TextBoxBase or System.Windows.Controls.PasswordBox or ComboBox or ComboBoxItem) return;
        // While browsing with the mini-player, the library keeps its keys unless the player has focus.
        if (_miniPlayer && !ReferenceEquals(sender, PlayerSurface) && !IsInPlayer(e.OriginalSource as DependencyObject)) return;
        switch (e.Key)
        {
            case Key.Space or Key.K: if (!e.IsRepeat) Pause_Click(this, new()); break;
            case Key.Left: SeekRelative(-10); break;
            case Key.Right: SeekRelative(10); break;
            case Key.Up: ChangeVolume(5); break;
            case Key.Down: ChangeVolume(-5); break;
            case Key.M: if (!e.IsRepeat) Mute_Click(this, new()); break;
            case Key.F: if (!e.IsRepeat) ToggleFullscreen(); break;
            case Key.S: if (!SkipAvailable) return; if (!e.IsRepeat) Skip_Click(this, new()); e.Handled = true; return;
            default: return;
        }
        ShowPlayerControls(); e.Handled = true;
    }
    private bool IsInPlayer(DependencyObject? node)
    {
        for (; node is not null; node = (node is Visual ? VisualTreeHelper.GetParent(node) : null) ?? LogicalTreeHelper.GetParent(node))
            if (ReferenceEquals(node, PlayerShell) || ReferenceEquals(node, PlayerSurface)) return true;
        return false;
    }
    private void ChangeVolume(double delta) { VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100); UpdateMuteIcon(); }
    private void HandlePlayerMessage(string message)
    {
        if (!_playing) return;
        switch (message)
        {
            case "mira-motion": ShowPlayerControls(); break;
            case "mira-fullscreen": ToggleFullscreen(); break;
            case "mira-back": if (_fullscreen) ToggleFullscreen(); else if (!_miniPlayer) SetMiniPlayer(true); break;
            case "mira-mini": SetMiniPlayer(!_miniPlayer); break;
            case "mira-pause": Pause_Click(this, new()); break;
            case "mira-seek-back": SeekRelative(-10); ShowPlayerControls(); break;
            case "mira-seek-forward": SeekRelative(10); ShowPlayerControls(); break;
            case "mira-volume-up": ChangeVolume(5); ShowPlayerControls(); break;
            case "mira-volume-down": ChangeVolume(-5); ShowPlayerControls(); break;
            case "mira-mute": Mute_Click(this, new()); break;
            case "mira-skip": if (SkipAvailable) Skip_Click(this, new()); break;
        }
    }
    private void Scrub(double seconds)
    {
        PositionText.Text = TimeLabel(seconds); ShowPlayerControls();
        if (_mpv is null || !_loaded || DateTimeOffset.UtcNow - _lastScrub < TimeSpan.FromMilliseconds(90)) return;
        _lastScrub = DateTimeOffset.UtcNow;
        // Keyframe seeks follow the pointer quickly; the release performs the exact seek.
        TryCommand("seek", seconds.ToString(CultureInfo.InvariantCulture), "absolute+keyframes");
    }
    private void SeekTo(double seconds)
    {
        _seeking = false; _lastScrub = DateTimeOffset.MinValue;
        if (_mpv is null || !_loaded) return;
        if (TryCommand("seek", seconds.ToString(CultureInfo.InvariantCulture), "absolute+exact")) HoldPosition(seconds);
        ShowPlayerControls();
    }
    private bool TryCommand(params string[] arguments)
    {
        try { _mpv?.Command(arguments); return _mpv is not null; }
        catch (IOException) { return false; }
    }
    private void MiniPlayer_Click(object sender, RoutedEventArgs e) => SetMiniPlayer(!_miniPlayer);
    private void ExpandPlayer_Click(object sender, RoutedEventArgs e) => SetMiniPlayer(false);
    private void SetMiniPlayer(bool mini, bool preserveFullscreen = false)
    {
        if (!_playing) return;
        if (_fullscreen && !preserveFullscreen) ToggleFullscreen();
        PlayerOptionsPopup.IsOpen = false; ClosePreview();
        var wasMini = _miniPlayer; _miniPlayer = mini;
        _changingPlayerLayout = true;
        if (mini) EnterMiniLayout(keepPlacement: wasMini); else ResetMiniPlacement();
        LibraryShell.Visibility = NavigationRail.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;
        if (!mini) DetailOverlay.Visibility = SettingsOverlay.Visibility = LoginOverlay.Visibility = Visibility.Collapsed;
        TitleBar.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;
        PlayerWindowChrome.Visibility = mini || _fullscreen ? Visibility.Collapsed : Visibility.Visible;
        PlayerHeader.Height = mini ? 58 : 120; PlayerControls.Height = mini ? 84 : 138;
        PlayerHeaderBody.Margin = mini ? new Thickness(14, 10, 8, 0) : new Thickness(24, _fullscreen ? 22 : 42, 24, 22);
        PlayerControlsBody.Margin = mini ? new Thickness(14, 0, 14, 10) : new Thickness(28, 20, 28, 22);
        PlayerTransport.Height = mini ? 38 : 52; PlayerTransport.Margin = new Thickness(0, mini ? 2 : 6, 0, 0);
        PlayingTitle.FontSize = mini ? 12.5 : 20; PlayingSubtitle.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        MiniExpandButton.Visibility = MiniCloseButton.Visibility = MiniOutline.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;
        foreach (var control in new FrameworkElement[] { PlayerBackButton, PlayerVolumeGroup, RewindButton, ForwardButton, NextButton, PlayerOptionsButton, MiniPlayerButton, FullscreenButton }) control.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Visibility = !mini && !_demo && _playingItem?.Type == "Episode" ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Width = PauseButton.Height = mini ? 36 : 50; Motion.SetRadius(PauseButton, new CornerRadius(mini ? 18 : 25));
        PauseIcon.Width = PauseIcon.Height = mini ? 16 : 22;
        _changingPlayerLayout = false; PositionPlayerControls(); ShowPlayerControls(); UpdateSkip();
        if (mini) { Motion.Reveal(LibraryScroll, 220, 5); UpdateNavigation(); if (!wasMini) RestoreDetailPage(refresh: false); }
        if (mini && !wasMini && _loaded) _ = RefreshMiniResumeAsync();
    }
    private async void StopPlayer_Click(object sender, RoutedEventArgs e)
    { await StopPlaybackAsync(); ReturnToLibrary(); if (!_demo) await RefreshAsync(quiet: true); else RenderDemo(); }
    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_mpv is null) return;
        if (_mpv.Flag("mute") || VolumeSlider.Value <= 0)
        {
            if (VolumeSlider.Value <= 0) VolumeSlider.Value = _lastAudibleVolume;
            _mpv.Set("mute", "no");
        }
        else _mpv.Set("mute", "yes");
        UpdateMuteIcon(); ShowPlayerControls();
    }
    private void UpdateMuteIcon()
    {
        var muted = _mpv?.Flag("mute") == true || VolumeSlider.Value <= 0;
        Motion.Swap(VolumeIcon, muted ? "mute" : "volume"); MuteButton.ToolTip = muted ? "Réactiver le son · M" : "Couper le son · M";
    }
    private void Tracks_Click(object sender, RoutedEventArgs e)
    {
        if (_mpv is null) return;
        if (PlayerOptionsPopup.IsOpen) { PlayerOptionsPopup.IsOpen = false; return; }
        _optionsBusy = true; AudioTrackChoice.Items.Clear(); SubtitleTrackChoice.Items.Clear(); SpeedChoice.Items.Clear();
        SubtitleTrackChoice.Items.Add(new ComboBoxItem { Content = "Désactivés", Tag = "no" });
        foreach (var track in _mpv.Tracks())
        { var target = track.Type == "audio" ? AudioTrackChoice : SubtitleTrackChoice; target.Items.Add(new ComboBoxItem { Content = track.Label, Tag = track.Id }); }
        SelectChoice(AudioTrackChoice, _mpv.Get("aid") ?? ""); SelectChoice(SubtitleTrackChoice, _mpv.Get("sid") ?? "no");
        AudioTrackChoice.IsEnabled = AudioTrackChoice.Items.Count > 1;
        foreach (var speed in new[] { .5, .75, 1, 1.25, 1.5, 2 }) SpeedChoice.Items.Add(new ComboBoxItem { Content = speed.ToString("0.##", CultureInfo.CurrentCulture) + "×", Tag = speed.ToString(CultureInfo.InvariantCulture) });
        SelectChoice(SpeedChoice, _mpv.Number("speed", 1).ToString(CultureInfo.InvariantCulture));
        SubtitleDelayReset.Content = $"{_mpv.Number("sub-delay"):0.0} s";
        PlaybackInfo.Text = $"Vidéo  {_mpv.Get("video-codec")}\nDécodage  {_mpv.Get("hwdec-current")}\nAudio  {_mpv.Get("audio-codec")}\nImages perdues  {_mpv.Get("frame-drop-count")}";
        _optionsBusy = false; ShowPlayerControls(); PlayerOptionsPopup.IsOpen = true;
    }
    private void PlayerOptions_Opened(object? sender, EventArgs e) => ShowPlayerControls();
    private void PlayerOptions_Closed(object? sender, EventArgs e) { _lastPlayerInteraction = DateTimeOffset.UtcNow; }
    private void PlayerOptions_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        var open = new[] { AudioTrackChoice, SubtitleTrackChoice, SpeedChoice }.FirstOrDefault(x => x.IsDropDownOpen);
        if (open is not null) open.IsDropDownOpen = false; else PlayerOptionsPopup.IsOpen = false;
        e.Handled = true;
    }
    private void AudioTrack_Changed(object sender, SelectionChangedEventArgs e) { if (!_optionsBusy && Choice(AudioTrackChoice) is { Length: > 0 } id) _mpv?.Set("aid", id); }
    private void SubtitleTrack_Changed(object sender, SelectionChangedEventArgs e) { if (!_optionsBusy && Choice(SubtitleTrackChoice) is { Length: > 0 } id) _mpv?.Set("sid", id); }
    private void Speed_Changed(object sender, SelectionChangedEventArgs e) { if (!_optionsBusy && Choice(SpeedChoice) is { Length: > 0 } speed) _mpv?.Set("speed", speed); }
    private void LoadSubtitle_Click(object sender, RoutedEventArgs e)
    {
        PlayerOptionsPopup.IsOpen = false;
        var dialog = new OpenFileDialog { Filter = "Sous-titres|*.srt;*.ass;*.ssa;*.vtt;*.sub|Tous les fichiers|*.*" };
        if (dialog.ShowDialog(this) == true && !TryCommand("sub-add", dialog.FileName, "select")) SetNotice("Ce fichier de sous-titres n’a pas pu être chargé.");
        ShowPlayerControls();
    }
    private void SubtitleDelay_Click(object sender, RoutedEventArgs e)
    {
        var delta = double.Parse(((Button)sender).Tag.ToString()!, CultureInfo.InvariantCulture);
        var value = delta == 0 ? 0 : (_mpv?.Number("sub-delay") ?? 0) + delta;
        _mpv?.Set("sub-delay", value.ToString(CultureInfo.InvariantCulture)); SubtitleDelayReset.Content = $"{value:0.0} s";
    }
}
