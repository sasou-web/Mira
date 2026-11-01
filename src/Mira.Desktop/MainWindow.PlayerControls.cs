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
        // In the background, the controls leave but their surface stays over the video: the click that brings Mira back
        // reaches it, not mpv's own window (MainWindow.PlayerSurface_MouseDown).
        Deactivated += (_, _) => { _fullscreenWindow.ActiveChanged(); LeavePlayerControls(); };
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
            PlayerOverlayLayer.Reposition(); PlayStateLayer.Reposition();
            if (PlayerOptionsPopup.IsOpen) { PlayerOptionsPopup.HorizontalOffset = .01; PlayerOptionsPopup.HorizontalOffset = 0; }
        }, DispatcherPriority.Render);
    }
    private Size _surfaceSize;
    private double _surfaceRadius = -1;
    private void ShowPlayerControls()
    {
        _lastPlayerInteraction = DateTimeOffset.UtcNow;
        if (!OpenPlayerSurface() || !PlayerInFront) return;
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
    private bool PlayerInFront => IsActive || _args.Contains("--player-check");
    /// <summary>
    /// Keeps a hit-testable surface above the native video while something plays in view, Mira in front or not, even
    /// after the controls fade (fully transparent pixels of a layered HWND would let mouse input pass through): a click
    /// on the picture always reaches Mira, never mpv's own window. Closed while the mini-player is dragged, gliding or
    /// tucked away.
    /// </summary>
    private bool OpenPlayerSurface()
    {
        if (!_playing || WindowState == WindowState.Minimized || (_miniPlayer && !MiniSettled)) return false;
        // Called on every mouse move: layout changes position the overlay themselves, so only a new window needs it.
        if (!PlayerOverlayLayer.IsOpen) { PositionPlayerControls(); PlayerOverlayLayer.IsOpen = true; }
        return true;
    }
    /// <summary>Mira went to the background: its menu closes and the controls fade, the pointer stays visible over it.</summary>
    private void LeavePlayerControls()
    {
        _surfaceClick.Stop(); PlayerOptionsPopup.IsOpen = false;
        if (_controlsVisible) { _controlsVisible = false; SetPlayerPanels(false, 240); }
        PlayerSurface.Cursor = Cursors.Arrow;
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
        _surfaceClick.Stop(); _lastSurfacePoint = null;
        // Opened again later, the surface starts as nothing shows it now: controls hidden.
        if (_controlsVisible) { _controlsVisible = false; SetPlayerPanels(false, 0); }
        PlayerOverlayLayer.IsOpen = PlayerOptionsPopup.IsOpen = false; HidePlayState();
        _volumeHide.Stop(); SetVolumeReveal(false);
        PlayerSurface.Cursor = Cursors.Arrow;
    }
    private void UpdatePlayerControls()
    {
        if (!_playing || WindowState == WindowState.Minimized) { ClosePlayerPopups(); return; }
        if (_miniPlayer && !MiniSettled) { if (PlayerOverlayLayer.IsOpen) ClosePlayerPopups(); return; }
        // Also when playback started with Mira in the background (the next episode, the group).
        OpenPlayerSurface();
        if (!PlayerInFront) { if (_controlsVisible || PlayerOptionsPopup.IsOpen) LeavePlayerControls(); return; }
        if (!_loaded || _seeking || PlayerOptionsPopup.IsOpen || VolumeSlider.IsMouseCaptureWithin || (_keyboardNavigation && (PlayerControls.IsKeyboardFocusWithin || PlayerHeader.IsKeyboardFocusWithin))) return;
        var idle = DateTimeOffset.UtcNow - _lastPlayerInteraction;
        if (_miniPlayer) { if (!PlayerSurface.IsMouseOver && idle > MiniIdle) HidePlayerControls(); return; }
        // While playing, a pointer resting on the controls keeps them. Once paused they leave anyway.
        if (!_session.Paused && _controlsVisible && (PointerOnBars() || SkipButton.IsMouseOver)) return;
        if (idle > (_session.Paused ? PausedIdle : PlayingIdle)) HidePlayerControls();
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
    /// <summary>The pointer rests on the bottom bar (from just above the title) or on the top band.</summary>
    private bool PointerOnBars() => PlayerSurface.IsMouseOver && InBarBand(Mouse.GetPosition(PlayerSurface));
    /// <summary>
    /// The bands of the top and bottom bars, shown or not: from just above the title to the bottom, from the top to just
    /// under the back button. The shades take no clicks, so the band is measured.
    /// </summary>
    private bool InBarBand(Point point)
    {
        var bottom = PlayerControlsBody.TranslatePoint(new Point(), PlayerSurface).Y - 12;
        var top = PlayerHeaderBody.TranslatePoint(new Point(0, PlayerHeaderBody.ActualHeight), PlayerSurface).Y + 12;
        return point.Y >= bottom || point.Y <= top;
    }
    /// <summary>
    /// A click on the picture pauses or resumes, a double click goes fullscreen. Never in the bands of the bars (a click
    /// that misses the subtitles or the volume), nor the click that brings Mira back to the front: they show the controls.
    /// </summary>
    private void PlayerSurface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var activating = PlayerOverlayLayer.TakeActivatingClick();
        if (_miniPlayer) { MiniPointerDown(e, activating); return; }
        if (e.OriginalSource != PlayerSurface) return;
        PlayerSurface.Focus(); ShowPlayerControls(); e.Handled = true;
        _surfaceClick.Stop();
        if (activating || InBarBand(e.GetPosition(PlayerSurface))) return;
        if (e.ClickCount == 2) ToggleFullscreen(); else _surfaceClick.Start();
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
        if (e.Handled || !_playing || PlayerOptionsPopup.IsOpen || FromTorLinkTerminal(e)) return;
        // Shift + ← / →: a longer jump (one minute). Ctrl and Alt keep their meaning (Ctrl + 1 to 5 are the rail's pages).
        if (Keyboard.Modifiers == ModifierKeys.Shift && e.Key is Key.Left or Key.Right && !IsTyping(e)) { SeekRelative(e.Key == Key.Left ? -LongJump : LongJump); ShowPlayerControls(); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (IsTyping(e)) return;
        // While browsing with the mini-player, the library keeps its keys unless the player has focus.
        if (_miniPlayer && !ReferenceEquals(sender, PlayerSurface) && !IsInPlayer(e.OriginalSource as DependencyObject)) return;
        switch (e.Key)
        {
            case Key.Space or Key.K: if (!e.IsRepeat) Pause_Click(this, new()); break;
            // J, K and L as on the Mac (and on video sites).
            case Key.Left or Key.J: SeekRelative(-10); break;
            case Key.Right or Key.L: SeekRelative(10); break;
            case Key.Up: ChangeVolume(5); break;
            case Key.Down: ChangeVolume(-5); break;
            case Key.M: if (!e.IsRepeat) Mute_Click(this, new()); break;
            case Key.F or Key.F11: if (!e.IsRepeat) ToggleFullscreen(); break;
            case Key.C: if (!e.IsRepeat) ToggleSubtitles(); break;
            // 0 to 9 (top row or numeric keypad): jump to 0 to 90 % of the title.
            case >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9: if (!e.IsRepeat) SeekToTenth(e.Key >= Key.NumPad0 ? e.Key - Key.NumPad0 : e.Key - Key.D0); break;
            case Key.S: if (!SkipAvailable) return; if (!e.IsRepeat) Skip_Click(this, new()); e.Handled = true; return;
            case Key.N: if (!NextButton.IsEnabled) return; if (!e.IsRepeat) Next_Click(this, new()); e.Handled = true; return;
            case Key.P: if (!PreviousButton.IsEnabled || PreviousButton.Visibility != Visibility.Visible) return; if (!e.IsRepeat) Previous_Click(this, new()); e.Handled = true; return;
            case Key.E: if (!PlayerEpisodesButton.IsEnabled || PlayerEpisodesButton.Visibility != Visibility.Visible || _miniPlayer) return; if (!e.IsRepeat) Episodes_Click(this, new()); e.Handled = true; return;
            default: return;
        }
        ShowPlayerControls(); e.Handled = true;
    }
    private const double LongJump = 60;
    private static bool IsTyping(KeyEventArgs e) => e.OriginalSource is TextBoxBase or System.Windows.Controls.PasswordBox or ComboBox or ComboBoxItem;
    /// <summary>The wheel over the picture changes the volume, as over the speaker.</summary>
    private void PlayerSurface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || !_playing || _miniPlayer || PlayerOptionsPopup.IsOpen || e.Delta == 0) return;
        ChangeVolume(e.Delta > 0 ? 5 : -5); ShowPlayerControls(); e.Handled = true;
    }
    /// <summary>0 to 9: jumps to that tenth of the title (0 = the start).</summary>
    private void SeekToTenth(int tenth)
    {
        if (_mpv is null || !_loaded || _session.Duration <= 0) return;
        var target = Math.Min(_session.Duration * tenth / 10, Math.Max(0, _session.Duration - .5));
        if (!TryCommand("seek", target.ToString(CultureInfo.InvariantCulture), "absolute+exact")) return;
        HoldPosition(target); ShowSeekPill($"{tenth * 10} %"); ShowPlayerControls();
    }
    private string? _subtitlesBeforeOff; private int? _convertedBeforeOff;
    /// <summary>C: subtitles off, and on again with the track that was showing (else the first one).</summary>
    private void ToggleSubtitles()
    {
        if (_mpv is null || !_loaded) return;
        if (_converted is { } converted)
        {
            var sid = _mpv.Get("sid"); var shown = converted.Shown(sid);
            if (shown is not null || converted.Stream.Burned is not null || converted.ShowsOther(sid))
            {
                _convertedBeforeOff = shown; _subtitlesBeforeOff = shown is null && sid is not (null or "no" or "" or "false") ? sid : null;
                if (shown is null && _subtitlesBeforeOff is not null) _mpv.Set("sid", "no"); else _ = ChooseConvertedAsync(text: -1);
                ShowSeekPill(L("Sous-titres désactivés", "Subtitles off"));
                return;
            }
            if (_subtitlesBeforeOff is not null) { _mpv.Set("sid", _subtitlesBeforeOff); ShowSeekPill(L("Sous-titres activés", "Subtitles on")); return; }
            var first = _convertedBeforeOff ?? converted.Subtitles.Select(ConvertedTracks.Index).FirstOrDefault(i => !converted.IsPicture(i) && converted.Path(i) is not null, -1);
            if (first < 0) { ShowSeekPill(L("Aucun sous-titre", "No subtitles")); return; }
            _ = ChooseConvertedAsync(text: first); ShowSeekPill(L("Sous-titres activés", "Subtitles on"));
            return;
        }
        var current = _mpv.Get("sid") ?? "no";
        if (current is not ("no" or "" or "false")) { _subtitlesBeforeOff = current; _mpv.Set("sid", "no"); ShowSeekPill(L("Sous-titres désactivés", "Subtitles off")); RememberTracks(); return; }
        var back = _subtitlesBeforeOff ?? _mpv.Tracks().FirstOrDefault(t => t.Type == "sub")?.Id;
        if (back is null) { ShowSeekPill(L("Aucun sous-titre", "No subtitles")); return; }
        _mpv.Set("sid", back); ShowSeekPill(L("Sous-titres activés", "Subtitles on")); RememberTracks();
    }
    private DispatcherTimer? _seekPillHide; private double _seekPillTotal;
    /// <summary>A short pill in the middle of the picture: the jump (« +10 s », adding up while the key repeats), the tenth, the subtitles.</summary>
    private void ShowSeekPill(string text)
    {
        if (_miniPlayer) return;
        SeekPillText.Text = text;
        HidePlayState();
        if (SeekPill.Visibility != Visibility.Visible) Motion.Reveal(SeekPill, 160, 4);
        if (_seekPillHide is null) { _seekPillHide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) }; _seekPillHide.Tick += (_, _) => { _seekPillHide.Stop(); _seekPillTotal = 0; SeekPill.Visibility = Visibility.Collapsed; }; }
        _seekPillHide.Stop(); _seekPillHide.Start();
    }
    /// <summary>« ±10 s », the sum of the jumps made within the same second.</summary>
    private void ShowSeekPill(double seconds)
    {
        if (_seekPillHide is not { IsEnabled: true } || Math.Sign(_seekPillTotal) != Math.Sign(seconds)) _seekPillTotal = 0;
        _seekPillTotal += seconds;
        ShowSeekPill((_seekPillTotal < 0 ? "−" : "+") + Math.Abs(_seekPillTotal).ToString("0", CultureInfo.InvariantCulture) + " s");
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
            case "mira-seek-back-long": SeekRelative(-LongJump); ShowPlayerControls(); break;
            case "mira-seek-forward-long": SeekRelative(LongJump); ShowPlayerControls(); break;
            case "mira-subtitles": ToggleSubtitles(); break;
            case var tenth when tenth.StartsWith("mira-tenth-", StringComparison.Ordinal) && tenth.Length == 12 && char.IsAsciiDigit(tenth[^1]): SeekToTenth(tenth[^1] - '0'); break;
            case "mira-volume-up": ShowPlayerControls(); ChangeVolume(5); break;
            case "mira-volume-down": ShowPlayerControls(); ChangeVolume(-5); break;
            case "mira-mute": Mute_Click(this, new()); break;
            case "mira-skip": if (SkipAvailable) Skip_Click(this, new()); break;
            case "mira-next": if (NextButton.IsEnabled) Next_Click(this, new()); break;
            case "mira-previous": if (PreviousButton.IsEnabled && PreviousButton.Visibility == Visibility.Visible) Previous_Click(this, new()); break;
            case "mira-episodes": if (PlayerEpisodesButton.IsEnabled && PlayerEpisodesButton.Visibility == Visibility.Visible && !_miniPlayer) Episodes_Click(this, new()); break;
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
        if (!mini) { AutoSaveSettings(); DetailOverlay.Visibility = SettingsOverlay.Visibility = LoginOverlay.Visibility = Visibility.Collapsed; }
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
        NextButton.Visibility = PreviousButton.Visibility = PlayerEpisodesButton.Visibility = !mini && !_demo && _session.Item?.Type == "Episode" ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Width = PauseButton.Height = mini ? 34 : 40; Motion.SetRadius(PauseButton, new CornerRadius(mini ? 17 : 20));
        PauseIcon.Width = PauseIcon.Height = mini ? 19 : 24;
        _changingPlayerLayout = false; PositionPlayerControls(); ShowPlayerControls(); UpdateSkip();
        if (mini) { Motion.Reveal(LibraryScroll, 220, 5); UpdateNavigation(); if (!wasMini) RestoreDetailPage(refresh: false); ShowPendingUpdateNotice(); }
        if (mini && !wasMini && _loaded) _ = RefreshMiniResumeAsync();
    }
    private async void StopPlayer_Click(object sender, RoutedEventArgs e)
    { await LeavePlayerAsync(); if (!_demo) await RefreshAsync(quiet: true); else RenderDemo(); }
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
        var label = muted ? L("Réactiver le son · M", "Unmute · M") : L("Couper le son · M", "Mute · M");
        if (!Equals(MuteButton.ToolTip, label)) { MuteButton.ToolTip = label; System.Windows.Automation.AutomationProperties.SetName(MuteButton, muted ? L("Réactiver le son", "Unmute") : L("Couper le son", "Mute")); }
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
    private static readonly Brush VolumeQuiet = Palette.Get("TextTertiary");
    private void HideVolumeSoon() { _volumeHide.Stop(); _volumeHide.Start(); }
    private void SetVolumeReveal(bool shown)
    {
        if (shown == _volumeShown) return;
        _volumeShown = shown;
        VolumeReveal.BeginAnimation(WidthProperty, new DoubleAnimation(shown ? VolumeRevealWidth : 0, TimeSpan.FromMilliseconds(Motion.Reduced ? 0 : shown ? 190 : 150)) { EasingFunction = Motion.EaseOut }, HandoffBehavior.SnapshotAndReplace);
    }

    private void Tracks_Click(object sender, RoutedEventArgs e) => TogglePlayerMenu(PlayerTracksButton);
    private void More_Click(object sender, RoutedEventArgs e) => TogglePlayerMenu(PlayerMoreButton);
    /// <summary>Opens the audio and subtitles menu, the episodes, or the ⋮ menu, above its button; the same button closes it.</summary>
    private void TogglePlayerMenu(Button anchor)
    {
        if (_mpv is null) return;
        if (PlayerOptionsPopup.IsOpen) { var same = ReferenceEquals(_menuAnchor, anchor); PlayerOptionsPopup.IsOpen = false; if (same) return; }
        _menuAnchor = anchor;
        var tracks = ReferenceEquals(anchor, PlayerTracksButton); var episodes = ReferenceEquals(anchor, PlayerEpisodesButton);
        TracksMenu.Visibility = tracks ? Visibility.Visible : Visibility.Collapsed;
        EpisodesMenu.Visibility = episodes ? Visibility.Visible : Visibility.Collapsed;
        SubtitleSearchMenu.Visibility = Visibility.Collapsed;
        MoreMenu.Visibility = tracks || episodes ? Visibility.Collapsed : Visibility.Visible;
        _menuBuilding = true;
        try { if (tracks) BuildTrackMenu(); else if (episodes) BuildEpisodesMenu(); else BuildMoreMenu(); } finally { _menuBuilding = false; }
        ShowPlayerControls(); PlayerOptionsPopup.IsOpen = true;
        if (episodes) ShowCurrentEpisodeRow();
        // The current choice takes focus: arrows and Enter work at once, and a mouse opening shows no focus ring.
        Dispatcher.BeginInvoke(() => { if (PlayerOptionsPopup.IsOpen) CurrentMenuChoice()?.Focus(); }, DispatcherPriority.Input);
    }
    private bool _menuBuilding;
    private Control? CurrentMenuChoice() => TracksMenu.Visibility == Visibility.Visible
        ? AudioTrackList.Children.OfType<RadioButton>().FirstOrDefault(IsChosen) ?? SubtitleTrackList.Children.OfType<RadioButton>().FirstOrDefault(IsChosen)
        : EpisodesMenu.Visibility == Visibility.Visible ? CurrentEpisodeRow()
        : SpeedList.Children.OfType<RadioButton>().FirstOrDefault(IsChosen);
    private static bool IsChosen(RadioButton choice) => choice.IsChecked == true;
    private void BuildTrackMenu()
    {
        if (_mpv is null) return;
        AudioTrackList.Children.Clear(); SubtitleTrackList.Children.Clear(); BuildAudioDeviceChoices();
        if (_converted is { } converted) { BuildConvertedTrackMenu(converted); _ = ShowSubtitleSearchAsync(); return; }
        var tracks = _mpv.Tracks(); var audio = _mpv.Get("aid") ?? ""; var subtitle = _mpv.Get("sid") ?? "no";
        foreach (var track in tracks.Where(t => t.Type == "audio"))
            AudioTrackList.Children.Add(MenuChoice("audio", track.Label, track.Details, track.Id == audio, () => ChooseTrack("aid", track.Id)));
        if (AudioTrackList.Children.Count == 0) AudioTrackList.Children.Add(new TextBlock { Text = L("Aucune piste audio", "No audio track"), Foreground = Palette.Get("Faint"), FontSize = 13, Margin = new Thickness(10, 8, 10, 8) });
        SubtitleTrackList.Children.Add(MenuChoice("subtitles", L("Désactivés", "Off"), "", subtitle is "no" or "" or "false", () => ChooseTrack("sid", "no")));
        foreach (var track in tracks.Where(t => t.Type == "sub"))
            SubtitleTrackList.Children.Add(MenuChoice("subtitles", track.Label, track.Details, track.Id == subtitle, () => ChooseTrack("sid", track.Id)));
        _ = ShowSubtitleSearchAsync();
    }
    /// <summary>The menu stays open, so that the other kind of track can be picked too. The next episodes start with the same.</summary>
    private void ChooseTrack(string property, string id) { _mpv?.Set(property, id); ShowPlayerControls(); RememberTracks(); }
    /// <summary>Keeps the audio and subtitles playing for the next episodes of this series (<see cref="SeriesTracks"/>).</summary>
    private void RememberTracks()
    {
        if (_mpv is null || _store is not { } store || _session.Item?.SeriesId is not { Length: > 0 } series) return;
        if (CurrentTracks() is not { } tracks) return;
        _ = Task.Run(() => TryCache(() => store.RememberTracks(series, tracks)));
        ChoicesChanged();
    }
    /// <summary>An episode starts with the audio and subtitles last chosen in its series, where it has them.</summary>
    private async Task ChooseSeriesTracksAsync(int generation)
    {
        if (_store is not { } store || _session.Item?.SeriesId is not { Length: > 0 } series) return;
        var tracks = await Task.Run(() => TryCache(() => store.TracksOf(series)));
        if (tracks is null || generation != _playGeneration || _mpv is null) return;
        ChooseTracks(tracks);
    }
    private void ChooseTracks(TrackMemory tracks)
    {
        if (_mpv is null) return;
        var (audio, subtitle) = SeriesTracks.Choose(_mpv.Get("track-list"), tracks);
        if (audio is not null) _mpv.Set("aid", audio);
        if (subtitle is not null) _mpv.Set("sid", subtitle);
    }
    private void BuildMoreMenu()
    {
        if (_mpv is null) return;
        SpeedList.Children.Clear();
        var current = _mpv.Number("speed", 1);
        foreach (var speed in Speeds)
        {
            var chip = new RadioButton { Style = (Style)FindResource("PlayerSpeedChoice"), GroupName = "speed", Content = PlayerText.Speed(speed), Tag = speed, IsChecked = Math.Abs(current - speed) < .001 };
            System.Windows.Automation.AutomationProperties.SetName(chip, L("Vitesse ", "Speed ") + PlayerText.Speed(speed));
            chip.Checked += (_, _) => { if (!_menuBuilding) ChooseSpeed(speed); };
            SpeedList.Children.Add(chip);
        }
        BuildQualityChoices(); BuildSleepChoices(); ShowGroupSection();
        SubtitleDelayReset.Content = DelayLabel(_mpv.Number("sub-delay")); AudioDelayReset.Content = DelayLabel(_mpv.Number("audio-delay"));
        // Short names ("H.264", "AAC"): mpv's long codec descriptions wrap over several lines.
        var width = _mpv.Number("width"); var height = _mpv.Number("height");
        var video = string.Join(" · ", new[] { PlayerText.Codec(_mpv.Get("video-format")), width > 0 && height > 0 ? $"{width:0} × {height:0}" : null }.Where(x => x is not null));
        var decoding = _mpv.Get("hwdec-current") is { Length: > 0 } hwdec and not "no" ? L($"matériel ({hwdec})", $"hardware ({hwdec})") : L("logiciel", "software");
        var shown = video.Length > 0 ? video : "—"; var audio = PlayerText.Codec(_mpv.Get("audio-codec-name")) ?? "—"; var dropped = _mpv.Get("frame-drop-count") ?? "0";
        var output = AudioOutputInfo();
        PlaybackInfo.Text = L($"Vidéo  {shown}\nDécodage  {decoding}\nImage  {PictureInfo}\nAudio  {audio}\nSortie  {output}\nImages perdues  {dropped}", $"Video  {shown}\nDecoding  {decoding}\nPicture  {PictureInfo}\nAudio  {audio}\nOutput  {output}\nDropped frames  {dropped}");
    }
    /// <summary>« Minuterie »: off, 15 min, 30 min, 1 h, or the end of an episode (<see cref="SleepTimer"/>).</summary>
    private void BuildSleepChoices()
    {
        SleepList.Children.Clear();
        var choices = new List<(int? Minutes, string Label, string Name)> { (null, L("Non", "Off"), L("Pas de minuterie", "No sleep timer")) };
        choices.AddRange(SleepTimer.Choices.Select(m => ((int?)m, SleepTimer.Duration(m), L("Pause dans ", "Pause in ") + SleepTimer.Duration(m))));
        if (_session.Item?.Type == "Episode") choices.Add((0, L("Fin", "End"), L("Arrêter à la fin de l’épisode", "Stop at the end of the episode")));
        foreach (var (minutes, label, name) in choices)
        {
            var chip = new RadioButton { Style = (Style)FindResource("PlayerSpeedChoice"), GroupName = "sleep", Content = label, ToolTip = name, IsChecked = _sleep.Chosen == minutes };
            System.Windows.Automation.AutomationProperties.SetName(chip, name);
            chip.Checked += (_, _) => { if (!_menuBuilding) ChooseSleep(minutes); };
            SleepList.Children.Add(chip);
        }
        ShowSleepStatus();
    }
    private void ChooseSleep(int? minutes)
    {
        _sleep.Choose(minutes, DateTimeOffset.Now); ShowSleepStatus(); ShowPlayerControls();
        // A countdown to the next episode already running stops, as « Annuler » does.
        if (_sleep.AtEnd && _skips.Counting) SkipCancel_Click(this, new RoutedEventArgs());
    }
    private void ShowSleepStatus()
    {
        var status = _sleep.Status(DateTimeOffset.Now);
        SleepStatus.Text = status ?? ""; SleepStatus.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
    }
    /// <summary>The minutes are over: the film pauses where it is, and says why.</summary>
    private void CheckSleepTimer()
    {
        if (!_sleep.Due(DateTimeOffset.Now)) return;
        if (_mpv is not null && !_mpv.Flag("pause")) { _mpv.Set("pause", "yes"); SetNotice(L("Minuterie : lecture en pause.", "Sleep timer: playback paused.")); }
        if (PlayerOptionsPopup.IsOpen && MoreMenu.Visibility == Visibility.Visible) BuildSleepChoices();
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
        if (details.Length > 0) label.Children.Add(new TextBlock { Text = details, FontSize = 11.5, FontWeight = FontWeights.Normal, Foreground = Palette.Get("Faint"), Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
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
        var dialog = new OpenFileDialog { Filter = L("Sous-titres|*.srt;*.ass;*.ssa;*.vtt;*.sub|Tous les fichiers|*.*", "Subtitles|*.srt;*.ass;*.ssa;*.vtt;*.sub|All files|*.*") };
        if (dialog.ShowDialog(this) == true)
        {
            if (!TryCommand("sub-add", dialog.FileName, "select")) SetNotice(L("Ce fichier de sous-titres n’a pas pu être chargé.", "This subtitle file couldn’t be loaded."));
            else _ = OfferToKeepSubtitleAsync(dialog.FileName);
        }
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
