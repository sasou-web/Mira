using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Mira.Core;
using Mira.Desktop.Playback;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    private FullscreenWindow? _fullscreenWindow;
    private WindowFrame? _windowFrame;
    private bool _miniPlayer, _controlsVisible;
    private bool _positionQueued, _changingPlayerLayout;
    private Point? _lastSurfacePoint;
    private int _surfaceMotionCount;
    private readonly DispatcherTimer _surfaceClick = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private double _lastAudibleVolume = 75;
    private DateTimeOffset _lastPlayerInteraction = DateTimeOffset.UtcNow, _lastScrub;
    // Playing: controls leave quickly. Paused: they also leave, so the still frame can be captured cleanly.
    private static readonly TimeSpan PlayingIdle = TimeSpan.FromSeconds(2.6), PausedIdle = TimeSpan.FromSeconds(4), MiniIdle = TimeSpan.FromSeconds(1.4);
    /// <summary>Button whose menu is open (audio and subtitles, or ⋮); the menu opens above it.</summary>
    private Button? _menuAnchor;
    private static readonly double[] Speeds = [.5, .75, 1, 1.25, 1.5, 2];
    // Slider width plus its margins, revealed beside the speaker.
    private const double VolumeRevealWidth = 174;
    private readonly DispatcherTimer _volumeHide = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private bool _volumeShown;
    private void InitializePlayerControls()
    {
        _fullscreenWindow = new(this);
        _windowFrame = new(this);
        _surfaceClick.Tick += (_, _) => { _surfaceClick.Stop(); if (_playing) Pause_Click(this, new()); };
        _volumeHide.Tick += (_, _) => { _volumeHide.Stop(); if (!KeepVolumeOpen) SetVolumeReveal(false); };
        if (_settings.Volume > 0) _lastAudibleVolume = _settings.Volume;
        SeekBar.SeekStarted += () => { _seeking = true; ShowPlayerControls(); };
        SeekBar.Seeking += Scrub;
        SeekBar.SeekCompleted += SeekTo;
        PlayerOptionsPopup.CustomPopupPlacementCallback = (size, target, _) => [new CustomPopupPlacement(MenuPlacement(size, target), PopupPrimaryAxis.None)];
        LocationChanged += (_, _) => PositionPlayerControls();
        StateChanged += (_, _) => { PositionPlayerControls(); UpdatePlayerControls(); };
        Activated += (_, _) => { _fullscreenWindow.ActiveChanged(); ShowPlayerControls(); };
        Deactivated += (_, _) => { _fullscreenWindow.ActiveChanged(); ClosePlayerPopups(); };
    }
    /// <summary>Above the button that opened the menu, right-aligned with it and kept inside the video.
    /// WPF supplies the sizes in device pixels, including at 200% DPI.</summary>
    private Point MenuPlacement(Size size, Size target)
    {
        var dpi = VisualTreeHelper.GetDpi(PlayerShell);
        var anchor = _menuAnchor ?? PlayerTracksButton;
        var corner = anchor.IsVisible ? anchor.TranslatePoint(new Point(anchor.ActualWidth, 0), PlayerSurface) : new Point(PlayerSurface.ActualWidth - 16, PlayerSurface.ActualHeight - 60);
        var margin = 12 * dpi.DpiScaleX;
        var x = Math.Clamp(corner.X * dpi.DpiScaleX - size.Width, margin, Math.Max(margin, target.Width - size.Width - margin));
        var y = Math.Max(12 * dpi.DpiScaleY, corner.Y * dpi.DpiScaleY - size.Height);
        return new Point(x, y);
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
        if (!visible) { _volumeHide.Stop(); SetVolumeReveal(false); }
        SeekBar.Live = visible; UpdateSkip();
    }
    private void ClosePlayerPopups()
    {
        _surfaceClick.Stop(); _controlsVisible = false; _lastSurfacePoint = null;
        PlayerOverlayLayer.IsOpen = PlayerOptionsPopup.IsOpen = false;
        _volumeHide.Stop(); SetVolumeReveal(false);
        PlayerSurface.Cursor = Cursors.Arrow;
    }
    private void UpdatePlayerControls()
    {
        if (!_playing || WindowState == WindowState.Minimized || (!IsActive && !_args.Contains("--player-check"))) { ClosePlayerPopups(); return; }
        if (_miniPlayer && !MiniSettled) { if (PlayerOverlayLayer.IsOpen) ClosePlayerPopups(); return; }
        if (!_loaded || _seeking || PlayerOptionsPopup.IsOpen || VolumeSlider.IsMouseCaptureWithin || (_keyboardNavigation && (PlayerControls.IsKeyboardFocusWithin || PlayerHeader.IsKeyboardFocusWithin))) return;
        var idle = DateTimeOffset.UtcNow - _lastPlayerInteraction;
        if (_miniPlayer) { if (!PlayerSurface.IsMouseOver && idle > MiniIdle) HidePlayerControls(); return; }
        // While playing, a pointer resting on the controls keeps them. Once paused they leave anyway.
        if (!_paused && _controlsVisible && (PointerOnBars() || SkipButton.IsMouseOver)) return;
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
    /// <summary>The pointer rests on the bottom bar (from just above the title) or on the top band: the shades take no
    /// clicks, so the band is measured rather than asked for IsMouseOver.</summary>
    private bool PointerOnBars()
    {
        if (!PlayerSurface.IsMouseOver) return false;
        var point = Mouse.GetPosition(PlayerSurface);
        var bottom = PlayerControlsBody.TranslatePoint(new Point(), PlayerSurface).Y - 12;
        var top = PlayerHeaderBody.TranslatePoint(new Point(0, PlayerHeaderBody.ActualHeight), PlayerSurface).Y + 12;
        return point.Y >= bottom || point.Y <= top;
    }
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
    private void ChangeVolume(double delta)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, VolumeBoost.Maximum); UpdateMuteIcon();
        // The level shows for a moment beside the speaker.
        if (!_miniPlayer) { SetVolumeReveal(true); HideVolumeSoon(); }
    }
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
            case "mira-volume-up": ShowPlayerControls(); ChangeVolume(5); break;
            case "mira-volume-down": ShowPlayerControls(); ChangeVolume(-5); break;
            case "mira-mute": Mute_Click(this, new()); break;
            case "mira-skip": if (SkipAvailable) Skip_Click(this, new()); break;
        }
    }
    private void Scrub(double seconds)
    {
        PositionText.Text = TimeLabel(seconds); UpdateChapterText(seconds); ShowPlayerControls();
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
    /// <summary>Name of the chapter being played, above the time, as long as the file names its chapters.</summary>
    private void UpdateChapterText(double seconds)
    {
        var title = SeekBar.ChapterAt(seconds) ?? "";
        if (ChapterText.Text != title) ChapterText.Text = title;
        var visibility = title.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ChapterText.Visibility != visibility) ChapterText.Visibility = visibility;
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
        // 42 above and 22 below the 44-unit back button: the header must stay at least 108 high.
        PlayerHeader.Height = mini ? 58 : 108; PlayerControls.Height = mini ? 84 : 210;
        PlayerHeaderBody.Margin = mini ? new Thickness(14, 10, 8, 0) : new Thickness(24, _fullscreen ? 22 : 42, 24, 22);
        PlayerControlsBody.Margin = mini ? new Thickness(14, 0, 14, 8) : new Thickness(24, 0, 24, 14);
        // The mini-player shows its title at the top and keeps only play / pause, centred under the timeline.
        PlayerInfo.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        MiniTitle.Visibility = MiniExpandButton.Visibility = MiniCloseButton.Visibility = MiniOutline.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;
        PlayerTransport.Height = mini ? 36 : 44; PlayerTransport.Margin = new Thickness(-8, mini ? 0 : 2, -8, 0);
        Grid.SetColumnSpan(PlayerLeftGroup, mini ? 3 : 1); PlayerLeftGroup.HorizontalAlignment = mini ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        foreach (var control in new FrameworkElement[] { PlayerBackButton, PlayerVolumeGroup, PlayerRightGroup }) control.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Visibility = !mini && !_demo && _playingItem?.Type == "Episode" ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Width = PauseButton.Height = mini ? 34 : 40; Motion.SetRadius(PauseButton, new CornerRadius(mini ? 17 : 20));
        PauseIcon.Width = PauseIcon.Height = mini ? 19 : 24;
        _changingPlayerLayout = false; PositionPlayerControls(); ShowPlayerControls(); UpdateSkip();
        if (mini) { Motion.Reveal(LibraryScroll, 220, 5); UpdateNavigation(); if (!wasMini) RestoreDetailPage(refresh: false); ShowPendingUpdateNotice(); }
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
        Motion.Swap(VolumeIcon, muted ? "mute" : VolumeSlider.Value < 50 ? "volume-low" : "volume");
        var label = muted ? "Réactiver le son · M" : "Couper le son · M";
        if (!Equals(MuteButton.ToolTip, label)) { MuteButton.ToolTip = label; System.Windows.Automation.AutomationProperties.SetName(MuteButton, muted ? "Réactiver le son" : "Couper le son"); }
    }

    // Volume slider beside the speaker: out while the pointer is on the group, while dragging, or with keyboard focus.
    // A click also focuses the speaker: only focus reached with the keyboard keeps the slider out.
    private bool KeepVolumeOpen => PlayerVolumeGroup.IsMouseOver || VolumeSlider.IsMouseCaptureWithin || MuteButton.IsKeyboardFocused && _keyboardNavigation;
    private void VolumeGroup_MouseEnter(object sender, MouseEventArgs e) { _volumeHide.Stop(); SetVolumeReveal(true); }
    private void VolumeGroup_MouseLeave(object sender, MouseEventArgs e) => HideVolumeSoon();
    private void VolumeGroup_FocusChanged(object sender, KeyboardFocusChangedEventArgs e) { if (MuteButton.IsKeyboardFocused && _keyboardNavigation) { _volumeHide.Stop(); SetVolumeReveal(true); } else HideVolumeSoon(); }
    private void VolumeSlider_LostMouseCapture(object sender, MouseEventArgs e) => HideVolumeSoon();
    // The wheel over the speaker or its slider changes the volume, as the arrow keys do.
    private void VolumeGroup_MouseWheel(object sender, MouseWheelEventArgs e) { ChangeVolume(e.Delta > 0 ? 5 : -5); e.Handled = true; }
    /// <summary>The level beside the slider, brighter above 100 % where the sound is amplified.</summary>
    private void ShowVolumeLevel()
    {
        if (VolumeText is null) return;
        var boosted = VolumeSlider.Value > 100.5;
        VolumeText.Text = VolumeBoost.Text(VolumeSlider.Value);
        VolumeText.Foreground = boosted ? Brushes.White : VolumeQuiet;
        VolumeText.FontWeight = boosted ? FontWeights.SemiBold : FontWeights.Normal;
    }
    private static readonly Brush VolumeQuiet = (Brush)new SolidColorBrush(Color.FromRgb(0xA9, 0xA9, 0xB1)).GetAsFrozen();
    private void HideVolumeSoon() { _volumeHide.Stop(); _volumeHide.Start(); }
    private void SetVolumeReveal(bool shown)
    {
        if (shown == _volumeShown) return;
        _volumeShown = shown;
        VolumeReveal.BeginAnimation(WidthProperty, new DoubleAnimation(shown ? VolumeRevealWidth : 0, TimeSpan.FromMilliseconds(Motion.Reduced ? 0 : shown ? 190 : 150)) { EasingFunction = Motion.EaseOut }, HandoffBehavior.SnapshotAndReplace);
    }

    private void Tracks_Click(object sender, RoutedEventArgs e) => TogglePlayerMenu(PlayerTracksButton);
    private void More_Click(object sender, RoutedEventArgs e) => TogglePlayerMenu(PlayerMoreButton);
    /// <summary>Opens the audio and subtitles menu, or the ⋮ menu, above its button; the same button closes it.</summary>
    private void TogglePlayerMenu(Button anchor)
    {
        if (_mpv is null) return;
        if (PlayerOptionsPopup.IsOpen) { var same = ReferenceEquals(_menuAnchor, anchor); PlayerOptionsPopup.IsOpen = false; if (same) return; }
        _menuAnchor = anchor;
        var tracks = ReferenceEquals(anchor, PlayerTracksButton);
        TracksMenu.Visibility = tracks ? Visibility.Visible : Visibility.Collapsed;
        MoreMenu.Visibility = tracks ? Visibility.Collapsed : Visibility.Visible;
        _menuBuilding = true;
        try { if (tracks) BuildTrackMenu(); else BuildMoreMenu(); } finally { _menuBuilding = false; }
        ShowPlayerControls(); PlayerOptionsPopup.IsOpen = true;
        // The current choice takes focus: arrows and Enter work at once, and a mouse opening shows no focus ring.
        Dispatcher.BeginInvoke(() => { if (PlayerOptionsPopup.IsOpen) CurrentMenuChoice()?.Focus(); }, DispatcherPriority.Input);
    }
    private bool _menuBuilding;
    private RadioButton? CurrentMenuChoice() => TracksMenu.Visibility == Visibility.Visible
        ? AudioTrackList.Children.OfType<RadioButton>().FirstOrDefault(IsChosen) ?? SubtitleTrackList.Children.OfType<RadioButton>().FirstOrDefault(IsChosen)
        : SpeedList.Children.OfType<RadioButton>().FirstOrDefault(IsChosen);
    private static bool IsChosen(RadioButton choice) => choice.IsChecked == true;
    private void BuildTrackMenu()
    {
        if (_mpv is null) return;
        AudioTrackList.Children.Clear(); SubtitleTrackList.Children.Clear();
        var tracks = _mpv.Tracks(); var audio = _mpv.Get("aid") ?? ""; var subtitle = _mpv.Get("sid") ?? "no";
        foreach (var track in tracks.Where(t => t.Type == "audio"))
            AudioTrackList.Children.Add(MenuChoice("audio", track.Label, track.Details, track.Id == audio, () => ChooseTrack("aid", track.Id)));
        if (AudioTrackList.Children.Count == 0) AudioTrackList.Children.Add(new TextBlock { Text = "Aucune piste audio", Foreground = Brush("#8E8E97"), FontSize = 13, Margin = new Thickness(10, 8, 10, 8) });
        SubtitleTrackList.Children.Add(MenuChoice("subtitles", "Désactivés", "", subtitle is "no" or "" or "false", () => ChooseTrack("sid", "no")));
        foreach (var track in tracks.Where(t => t.Type == "sub"))
            SubtitleTrackList.Children.Add(MenuChoice("subtitles", track.Label, track.Details, track.Id == subtitle, () => ChooseTrack("sid", track.Id)));
    }
    /// <summary>The menu stays open, so that the other kind of track can be picked too.</summary>
    private void ChooseTrack(string property, string id) { _mpv?.Set(property, id); ShowPlayerControls(); }
    private void BuildMoreMenu()
    {
        if (_mpv is null) return;
        SpeedList.Children.Clear();
        var current = _mpv.Number("speed", 1);
        foreach (var speed in Speeds)
        {
            var chip = new RadioButton { Style = (Style)FindResource("PlayerSpeedChoice"), GroupName = "speed", Content = PlayerText.Speed(speed), Tag = speed, IsChecked = Math.Abs(current - speed) < .001 };
            System.Windows.Automation.AutomationProperties.SetName(chip, "Vitesse " + PlayerText.Speed(speed));
            chip.Checked += (_, _) => { if (!_menuBuilding) ChooseSpeed(speed); };
            SpeedList.Children.Add(chip);
        }
        SubtitleDelayReset.Content = DelayLabel(_mpv.Number("sub-delay"));
        // Short names ("H.264", "AAC"): mpv's long codec descriptions wrap over several lines.
        var width = _mpv.Number("width"); var height = _mpv.Number("height");
        var video = string.Join(" · ", new[] { PlayerText.Codec(_mpv.Get("video-format")), width > 0 && height > 0 ? $"{width:0} × {height:0}" : null }.Where(x => x is not null));
        var decoding = _mpv.Get("hwdec-current") is { Length: > 0 } hwdec and not "no" ? $"matériel ({hwdec})" : "logiciel";
        PlaybackInfo.Text = $"Vidéo  {(video.Length > 0 ? video : "—")}\nDécodage  {decoding}\nAudio  {PlayerText.Codec(_mpv.Get("audio-codec-name")) ?? "—"}\nImages perdues  {_mpv.Get("frame-drop-count") ?? "0"}";
    }
    private void ChooseSpeed(double speed)
    {
        _mpv?.Set("speed", speed.ToString(CultureInfo.InvariantCulture));
        // Also when called from code: the chip of that speed shows as chosen.
        _menuBuilding = true;
        try { foreach (var chip in SpeedList.Children.OfType<RadioButton>()) if (chip.Tag is double value && Math.Abs(value - speed) < .001) chip.IsChecked = true; }
        finally { _menuBuilding = false; }
        ShowPlayerControls();
    }
    /// <summary>A track row: a radio button of its group, checked for the current track, with details (codec, channels…) beneath.</summary>
    private RadioButton MenuChoice(string group, string text, string details, bool chosen, Action choose)
    {
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis });
        if (details.Length > 0) label.Children.Add(new TextBlock { Text = details, FontSize = 11.5, FontWeight = FontWeights.Normal, Foreground = Brush("#8E8E97"), Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        var full = details.Length > 0 ? text + " · " + details : text;
        var choice = new RadioButton { Style = (Style)FindResource("PlayerMenuChoice"), GroupName = group, Content = label, IsChecked = chosen, ToolTip = full.Length > 34 ? full : null };
        System.Windows.Automation.AutomationProperties.SetName(choice, full);
        choice.Checked += (_, _) => { if (!_menuBuilding) choose(); };
        return choice;
    }
    private static string DelayLabel(double seconds) => PlayerText.Delay(seconds);
    private void PlayerOptions_Opened(object? sender, EventArgs e) => ShowPlayerControls();
    private void PlayerOptions_Closed(object? sender, EventArgs e)
    {
        _lastPlayerInteraction = DateTimeOffset.UtcNow;
        // The click that closed the menu went to the popup, not to the button under it: a click on the other menu's
        // button opens that menu, as the viewer meant (a click on the same button just closes it).
        if (Mouse.LeftButton != MouseButtonState.Pressed || !_playing) return;
        var other = ReferenceEquals(_menuAnchor, PlayerTracksButton) ? PlayerMoreButton : PlayerTracksButton;
        if (!other.IsVisible || PresentationSource.FromVisual(other) is null) return;
        var point = Mouse.GetPosition(other);
        if (point.X >= 0 && point.Y >= 0 && point.X < other.ActualWidth && point.Y < other.ActualHeight)
            Dispatcher.BeginInvoke(() => TogglePlayerMenu(other), DispatcherPriority.Input);
    }
    private void PlayerOptions_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        PlayerOptionsPopup.IsOpen = false; e.Handled = true;
        // Back on the button that opened the menu, so the keyboard carries on from there.
        if (_keyboardNavigation) _menuAnchor?.Focus();
    }
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
        var value = delta == 0 ? 0 : Math.Round((_mpv?.Number("sub-delay") ?? 0) + delta, 1);
        _mpv?.Set("sub-delay", value.ToString(CultureInfo.InvariantCulture)); SubtitleDelayReset.Content = DelayLabel(value);
        ShowPlayerControls();
    }
}
