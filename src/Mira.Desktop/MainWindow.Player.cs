using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Mira.Core;
using Mira.Desktop.Playback;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    private readonly DispatcherTimer _playerTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private MpvEngine? _mpv;
    private VideoHost? _videoHost;
    private IntPtr _videoHandle;
    private MediaItem? _playingItem;
    private MediaSource? _mediaSource;
    private string _playSession = "";
    private bool _playing, _loaded, _paused, _seeking, _fullscreen, _closing, _playBusy, _playWasReported;
    /// <summary>From the first close request: the window is hidden and its services are being stopped.</summary>
    internal bool IsClosing => _closing;
    private double _position, _duration, _pendingSeek;
    private DateTimeOffset _pendingSeekUntil;
    private DateTimeOffset _lastReport;
    private int _playGeneration;
    private bool _tickBusy;
    private string? _playFailure;

    private async Task PlayAsync(MediaItem item, bool keepMini = false, bool completePrevious = false)
    {
        ClosePreview();
        if (_playBusy) return; _playBusy = true;
        // Opening a stream can take a moment: say so right away instead of looking frozen.
        if (!_playing) Mouse.OverrideCursor = Cursors.AppStarting;
        if (DetailOverlay.Visibility == Visibility.Visible && _detail is not null) _returnToDetail = _detail;
        try
        {
            // Without an engine nothing can play: say so before any request, with the one click that fixes it.
            if (MpvEngine.FindLibrary(_settings.MpvPath) is null)
            {
                SetNotice("Pour lire tes vidéos, Mira a besoin du moteur mpv (31 Mo).", "Installer", () => _ = InstallEngineThenPlayAsync(item, keepMini));
                return;
            }
            if (item.Type == "Series" && !_demo && _client is not null)
            {
                // Jellyfin's own "next up" for the series: the episode in progress, else the one after the last watched.
                var next = await NextEpisodeAsync(_client, item.Id);
                if (next is null)
                {
                    var episodes = _metadata is { } metadata ? await metadata.EpisodesAsync(item.Id) : await _client.EpisodesAsync(item.Id);
                    if (_store is { } store) await Task.Run(() => store.ApplyLocalProgress(episodes.Items));
                    next = episodes.Items.FirstOrDefault(x => x.Progress > 0 && !x.UserData.Played) ?? episodes.Items.FirstOrDefault(x => !x.UserData.Played) ?? episodes.Items.FirstOrDefault();
                }
                item = next ?? throw new IOException("Aucun épisode disponible.");
            }
            string location; string? authorization = null; MediaSource? source = null; string session = "";
            if (_demo)
            {
                if (_testMedia is null)
                {
                    var dialog = new OpenFileDialog { Title = "Choisir une vidéo locale pour essayer le lecteur", Filter = "Vidéos|*.mkv;*.mp4;*.avi;*.webm;*.mov;*.m4v;*.ts|Tous les fichiers|*.*" };
                    if (dialog.ShowDialog(this) != true) return;
                    location = dialog.FileName;
                }
                else location = _testMedia;
            }
            else
            {
                if (_client is null) return;
                var info = await _client.PlaybackAsync(item.Id);
                source = info.MediaSources.FirstOrDefault() ?? throw new IOException("Aucune source lisible disponible.");
                session = string.IsNullOrEmpty(info.PlaySessionId) ? Guid.NewGuid().ToString("N") : info.PlaySessionId;
                var isLocalServer = new Uri(_client.Connection.Server).IsLoopback;
                if (isLocalServer && string.Equals(source.Protocol, "File", StringComparison.OrdinalIgnoreCase) && source.Path is { } path && File.Exists(path)) location = path;
                else { location = _client.StreamUri(item.Id, source.Id).AbsoluteUri; authorization = _client.AuthorizationHeader; }
            }
            await StopPlaybackAsync(completePrevious);
            _playingItem = item; _mediaSource = source; _playSession = session; _loaded = false; _playWasReported = false; _playFailure = null;
            // A finished title starts over instead of resuming on its last seconds.
            var finished = item.UserData.Played || item.Progress >= LibraryStore.WatchedThreshold;
            _position = _demo || !_settings.RememberPosition || finished ? 0 : TimeSpan.FromTicks(item.UserData.PlaybackPositionTicks).TotalSeconds;
            _duration = TimeSpan.FromTicks(item.RunTimeTicks ?? 0).TotalSeconds;
            // "Épisode 3" at once; the total of the season follows with the episode list (LoadServerMarkersAsync).
            PlayingTitle.Text = MiniTitle.Text = item.DisplayTitle; PlayingSubtitle.Text = _demo ? "Vidéo locale · mode démonstration" : PlayerText.Subtitle(item);
            DetailOverlay.Visibility = Visibility.Collapsed; SettingsOverlay.Visibility = Visibility.Collapsed;
            LibraryShell.Visibility = Visibility.Collapsed; PlayerShell.Visibility = Visibility.Visible;
            NavigationRail.Visibility = Visibility.Collapsed;
            // The player's keys must reach Mira, not the TorLink terminal left open underneath.
            if (TorLinkOverlay.IsKeyboardFocusWithin) Focus();
            NextButton.IsEnabled = !_demo && item.Type == "Episode";
            if (_videoHost is null)
            {
                var ready = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
                _videoHost = new VideoHost(); _videoHost.Ready += hwnd => { _videoHandle = hwnd; ready.TrySetResult(hwnd); };
                VideoContainer.Children.Add(_videoHost); _videoHandle = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            ResetMarkers();
            _mpv = new MpvEngine(_videoHandle, _settings);
            PublishWindowsTrack(item);
            _mpv.FileLoaded += FileLoaded;
            _mpv.PlaybackRestarted += () => _lastReport = DateTimeOffset.MinValue;
            var generation = ++_playGeneration;
            _ = LoadServerMarkersAsync(item, generation);
            _mpv.Ended += eof => PlaybackEnded(eof, generation);
            _mpv.Error += message => { _playFailure = message; };
            _mpv.Message += message => Dispatcher.BeginInvoke(() => { if (generation == _playGeneration) HandlePlayerMessage(message); });
            _playing = true; UpdateHeroClock(); _lastReport = DateTimeOffset.Now; SetPlayerLoading(true); _mpv.Load(location, _position, authorization);
            if (_demo && _testMedia is not null && _args.Contains("--loop-test-media")) _mpv.Set("loop-file", "inf");
            SetMiniPlayer(keepMini, preserveFullscreen: true); _playerTimer.Start();
        }
        catch (Exception ex) when (IsExpected(ex) || ex is TimeoutException)
        {
            await StopPlaybackAsync(); ReturnToLibrary();
            SetNotice(ex is IOException ? ex.Message : Friendly(ex));
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // An incompatible engine DLL must not leave a black screen with no way back.
            await StopPlaybackAsync(); ReturnToLibrary();
            SetNotice("La lecture n’a pas pu démarrer : le moteur mpv choisi semble incompatible. Vérifie son emplacement dans Réglages → Lecture.");
        }
        finally { _playBusy = false; Mouse.OverrideCursor = null; }
    }
    private async void FileLoaded()
    {
        if (_mpv is null || _playingItem is null) return;
        var generation = _playGeneration;
        _loaded = true; SeekBar.IsEnabled = true; SetPlayerLoading(false); ShowPlayerControls(); _paused = _mpv.Flag("pause"); _duration = _mpv.Number("duration", _duration); _position = _mpv.Number("time-pos", _position);
        SeekBar.SetPlayback(_position, _duration, !_paused, _mpv.Number("speed", 1)); LoadFileChapters();
        UpdateWindowsPlayback();
        _lastReport = DateTimeOffset.Now;
        if (!_demo && _sync is not null) { _playWasReported = true; await _sync.RecordAsync("start", Report()); }
        if (generation != _playGeneration || _mpv is null) return;
        await RememberCurrentPlaybackAsync();
        if (generation != _playGeneration || _mpv is null) return;
        // External subtitles supplied by Jellyfin may not share the video's filename.
        if (!_demo && _client is not null && _mediaSource is not null)
        {
            foreach (var stream in _mediaSource.MediaStreams.Where(s => s.Type == "Subtitle" && s.IsExternal && s.DeliveryUrl is not null))
            {
                var uri = new Uri(new Uri(_client.Connection.Server), stream.DeliveryUrl!);
                if (uri.GetLeftPart(UriPartial.Authority) != new Uri(_client.Connection.Server).GetLeftPart(UriPartial.Authority)) continue;
                // Local files need the same authorization header for separately fetched subtitle tracks.
                _mpv.Set("http-header-fields", "Authorization: " + _client.AuthorizationHeader.Replace(",", "\\,"));
                try { _mpv.Command("sub-add", uri.AbsoluteUri, "auto", stream.DisplayTitle ?? "", stream.Language ?? ""); } catch (IOException) { }
            }
        }
    }
    private async void PlayerTick(object? sender, EventArgs e)
    {
        if (_tickBusy || _mpv is null || !_playing) return; _tickBusy = true;
        try
        {
            UpdatePlayerControls(); _mpv.Poll(); if (_mpv is null || !_loaded || !_playing) return;
            _position = ReportedPosition(_mpv.Number("time-pos", _position)); _duration = _mpv.Number("duration", _duration);
            var paused = _mpv.Flag("pause"); ShowPauseState(paused);
            // The timeline interpolates between these polls on every frame.
            if (!_seeking) { SeekBar.SetPlayback(_position, _duration, !paused, _mpv.Number("speed", 1)); PositionText.Text = TimeLabel(_position); UpdateChapterText(_position); }
            SeekBar.SetBuffered(_mpv.Number("demuxer-cache-time", 0)); DurationText.Text = TimeLabel(_duration);
            var changed = paused != _paused; _paused = paused; if (changed && paused) ShowPlayerControls(); UpdateMuteIcon();
            if (changed && _miniStowed) MiniTabPulse.Opacity = paused ? .35 : 1;
            UpdateSkip();
            UpdateWindowsPlayback();
            var vol = _mpv.Level; if (Math.Abs(VolumeSlider.Value - vol) > 1) VolumeSlider.Value = vol;
            if (_playingItem is not null) _playingItem.UserData.PlaybackPositionTicks = TimeSpan.FromSeconds(Math.Max(0, _position)).Ticks;
            if (!_demo && _sync is not null && _playWasReported && (changed || DateTimeOffset.Now - _lastReport >= TimeSpan.FromSeconds(3)))
            { _lastReport = DateTimeOffset.Now; await _sync.RecordAsync("progress", Report()); }
        }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); }
        finally { _tickBusy = false; }
    }
    private PlaybackReport Report() => new()
    {
        ItemId = _playingItem?.Id ?? "",
        MediaSourceId = _mediaSource?.Id ?? "",
        PlaySessionId = _playSession,
        PositionTicks = TimeSpan.FromSeconds(Math.Max(0, _position)).Ticks,
        IsPaused = _paused,
        VolumeLevel = VolumeBoost.Reported(_mpv?.Level ?? _settings.Volume),
        IsMuted = _mpv?.Flag("mute") ?? false
    };
    private void PlaybackEnded(bool eof, int generation)
    {
        // mpv invalidates the native event buffer on the next poll. Defer disposal until Poll has returned.
        Dispatcher.BeginInvoke(async () =>
        {
            if (!_playing || generation != _playGeneration) return;
            var previous = _playingItem; var error = _playFailure; var keepMini = _miniPlayer; var stay = _autoNextCancelled; var following = _nextItem;
            if (eof && _duration > 0) _position = _duration;
            await StopPlaybackAsync(eof);
            // Watched to the end: shown as seen right away, and the next play starts from the beginning.
            if (eof && previous is not null) { previous.UserData.Played = true; previous.UserData.PlaybackPositionTicks = 0; }
            var stoppedGeneration = _playGeneration;
            if (eof && _settings.AutoNext && !_demo && !stay && previous?.Type == "Episode")
            {
                var next = following ?? await GetNextAsync(previous);
                if (stoppedGeneration != _playGeneration || _playing) return;
                if (next is not null) { await PlayAsync(next, keepMini); return; }
            }
            ReturnToLibrary();
            if (error is not null) SetNotice(error);
            else if (!_demo) await RefreshAsync(quiet: true);
            else RenderAfterUserDataChange();
        });
    }
    private void SetPlayerLoading(bool loading)
    {
        if (loading)
        {
            PlayerLoading.Visibility = Visibility.Visible; Motion.Fade(PlayerLoading, 1, 200, 0);
            LoadingSpin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(Motion.Reduced ? 2400 : 850)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
            return;
        }
        if (PlayerLoading.Visibility != Visibility.Visible) return;
        LoadingSpin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        PlayerLoading.Visibility = Visibility.Collapsed;
    }
    private async Task StopPlaybackAsync(bool completed = false)
    {
        var hadPlayback = _loaded;
        ++_playGeneration; ClosePlayerPopups(); _playerTimer.Stop(); SeekBar.IsEnabled = false; SetPlayerLoading(false);
        if (_playing && _loaded && _mpv is not null && !completed) _position = ReportedPosition(_mpv.Number("time-pos", _position));
        completed = _loaded && (completed || _duration > 0 && _position >= _duration * LibraryStore.WatchedThreshold);
        _playing = false;
        try
        {
            _windows?.Clear();
            if (_playWasReported && _sync is not null) { _playWasReported = false; await _sync.RecordAsync(completed ? "complete" : "stop", Report()); }
            if (completed && _playingItem is { } finished)
            {
                foreach (var copy in _items.Concat(_resume).Concat(_nextUp).Concat(_episodes).Append(finished).Where(x => x.Id == finished.Id))
                { copy.UserData.Played = true; copy.UserData.PlaybackPositionTicks = 0; }
                _resume.RemoveAll(x => x.Id == finished.Id); _nextUp.RemoveAll(x => x.Id == finished.Id);
                _metadata?.Clear(); _heroSeries.Clear();
            }
            if (_loaded && _playingItem is { } stopped)
            {
                if (!completed) stopped.UserData.PlaybackPositionTicks = TimeSpan.FromSeconds(Math.Max(0, _position)).Ticks;
                await RememberCurrentPlaybackAsync();
            }
        }
        // Whatever failed above, the engine is released: it would otherwise play on behind the library.
        finally { _loaded = false; if (_mpv is not null) { _settings.Volume = _mpv.Level; _mpv.Dispose(); _mpv = null; } }
        if (hadPlayback && !_closing) RenderAfterUserDataChange();
    }
    private async Task<MediaItem?> GetNextAsync(MediaItem current, bool quiet = false) => (await EpisodeContextAsync(current, quiet)).Next;
    /// <summary>The episode after <paramref name="current"/> and every episode of its series (for "Épisode 3 / 12").</summary>
    private async Task<(MediaItem? Next, List<MediaItem> Episodes)> EpisodeContextAsync(MediaItem current, bool quiet = false)
    {
        if (_client is null || current.SeriesId is null) return (null, []);
        try
        {
            var episodes = await _client.EpisodesAsync(current.SeriesId); var index = episodes.Items.FindIndex(x => x.Id == current.Id);
            return (index >= 0 ? episodes.Items.ElementAtOrDefault(index + 1) : null, episodes.Items);
        }
        catch (Exception ex) when (IsExpected(ex)) { if (!quiet) SetNotice(Friendly(ex)); return (null, []); }
    }
    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_playingItem is null) return; var next = _nextItem ?? await GetNextAsync(_playingItem);
        if (next is not null) await PlayAsync(next, _miniPlayer, completePrevious: _skipToNext && _activeSkip is not null); else PlayingSubtitle.Text = "Aucun épisode suivant disponible.";
    }
    private void BackFromPlayer_Click(object sender, RoutedEventArgs e)
    { if (_fullscreen) ToggleFullscreen(); else if (!_miniPlayer) SetMiniPlayer(true); }
    private void ReturnToLibrary()
    {
        if (_fullscreen) ToggleFullscreen(); ClosePlayerPopups(); _miniPlayer = false; ResetMiniPlacement();
        PlayerShell.Visibility = Visibility.Collapsed; LibraryShell.Visibility = NavigationRail.Visibility = Visibility.Visible;
        TitleBar.Visibility = Visibility.Visible;
        Motion.Reveal(LibraryScroll); RestoreDetailPage(); UpdateHeroClock(); ShowPendingUpdateNotice();
    }
    private MediaItem? _returnToDetail;
    /// <summary>Playback started from a title page comes back to that page, with progress and watched marks refreshed.</summary>
    private void RestoreDetailPage(bool refresh = true)
    {
        if (_returnToDetail is not { } page || _detail?.Id != page.Id) { _returnToDetail = null; return; }
        if (DetailOverlay.Visibility != Visibility.Visible) Motion.Reveal(DetailOverlay, 260, 10);
        if (refresh) _ = RefreshDetailAsync();
    }
    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_mpv is null) return;
        var pause = !_mpv.Flag("pause");
        _mpv.Set("pause", pause ? "yes" : "no");
        // Swap the glyph now rather than at the next poll, so the press and the new icon are one movement.
        ShowPauseState(pause);
        SeekBar.SetPlayback(_position, _duration, !pause, _mpv.Number("speed", 1));
        UpdateWindowsPlayback();
        ShowPlayerControls();
    }
    /// <summary>The button shows what a press does: play while paused, pause while playing.</summary>
    private void ShowPauseState(bool paused)
    {
        Motion.Swap(PauseIcon, paused ? "play" : "pause");
        var label = paused ? "Lecture · Espace" : "Pause · Espace";
        if (!Equals(PauseButton.ToolTip, label)) { PauseButton.ToolTip = label; System.Windows.Automation.AutomationProperties.SetName(PauseButton, paused ? "Lecture" : "Pause"); }
    }
    private void SeekRelative(double seconds)
    {
        if (_mpv is null || !_loaded) return;
        // Absolute target from the last requested position: repeated presses add up, and the bar moves at once.
        var target = Math.Clamp(_position + seconds, 0, _duration > 0 ? Math.Max(0, _duration - .5) : double.MaxValue);
        if (!TryCommand("seek", target.ToString(CultureInfo.InvariantCulture), "absolute+exact")) return;
        HoldPosition(target);
    }
    /// <summary>mpv may report the pre-seek time for a poll or two; keep the requested time meanwhile.</summary>
    private void HoldPosition(double target)
    {
        _position = target; _pendingSeek = target; _pendingSeekUntil = DateTimeOffset.UtcNow.AddMilliseconds(800);
        SeekBar.Hold(target); PositionText.Text = TimeLabel(target); _lastReport = DateTimeOffset.MinValue;
    }
    private double ReportedPosition(double reported) =>
        DateTimeOffset.UtcNow < _pendingSeekUntil && Math.Abs(reported - _pendingSeek) > 1.5 ? _pendingSeek : reported;
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { ShowVolumeLevel(); if (_initializing) return; _settings.Volume = e.NewValue; _mpv?.SetLevel(e.NewValue); if (e.NewValue > 0) { _lastAudibleVolume = e.NewValue; _mpv?.Set("mute", "no"); } }
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void ToggleFullscreen()
    {
        if (!_playing && !_fullscreen) return;
        if (_miniPlayer) SetMiniPlayer(false);
        PlayerOptionsPopup.IsOpen = false;
        _changingPlayerLayout = true;
        if (!_fullscreen)
        {
            _windowFrame!.SetFullscreen(true);
            _fullscreenWindow!.Enter(_args.Contains("--player-check") ? new Rect(-30000, -30000, 1600, 900) : null); _fullscreen = true;
        }
        else
        {
            _fullscreen = false; _fullscreenWindow!.Exit();
            _windowFrame!.SetFullscreen(false);
        }
        PlayerWindowChrome.Visibility = _fullscreen ? Visibility.Collapsed : Visibility.Visible;
        PlayerHeaderBody.Margin = new Thickness(24, _fullscreen ? 22 : 42, 24, 22);
        Motion.Swap(FullscreenIcon, _fullscreen ? "collapse" : "fullscreen");
        FullscreenButton.ToolTip = _fullscreen ? "Quitter le plein écran · Échap" : "Plein écran · F";
        _changingPlayerLayout = false; PositionPlayerControls(); ShowPlayerControls();
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        // Keys typed in TorLink belong to TorLink (Escape, arrows, shortcuts); Alt+← and the "back" key still leave its page.
        if (FromTorLinkTerminal(e) && !IsBackKey(e)) return;
        if (e.Key == Key.Escape)
        {
            foreach (var combo in new[] { GenreFilter, YearFilter, WatchedFilter, SortFilter, LibraryFilter, SeasonSelector, AudioLanguageChoice, SubtitleLanguageChoice, DensityChoice })
                if (combo.IsDropDownOpen) { combo.IsDropDownOpen = false; combo.Focus(); e.Handled = true; return; }
            GoBack(); e.Handled = true; return;
        }
        // Alt+← and the browser "back" key behave like Escape and the mouse's back button.
        if (IsBackKey(e)) { if (GoBack()) e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.K or Key.F && (!_playing || _miniPlayer)) { SearchNav_Click(this, new()); e.Handled = true; return; }
        if (e.Key == Key.F1 && (!_playing || _miniPlayer)) { OpenGuide(); e.Handled = true; return; }
        if (e.OriginalSource is TextBox or System.Windows.Controls.PasswordBox || !_playing) return;
        // Space, arrows, M, F and S are handled earlier by Player_PreviewKeyDown, before any focused control.
        if (e.Key == Key.I && !e.IsRepeat) { SetMiniPlayer(!_miniPlayer); e.Handled = true; }
    }
    /// <summary>Alt+← or the "back" key. WPF reports Alt+← as Key.System; keys forwarded by WebView2 carry the arrow itself.</summary>
    private static bool IsBackKey(KeyEventArgs e) => e.Key == Key.BrowserBack || Keyboard.Modifiers == ModifierKeys.Alt && (e.SystemKey == Key.Left || e.Key == Key.Left);
    /// <summary>One step back: menu, account screen, settings, title page, then the player.</summary>
    private bool GoBack()
    {
        if (PlayerOptionsPopup.IsOpen) { PlayerOptionsPopup.IsOpen = false; return true; }
        if (WhatsNewOverlay.Visibility == Visibility.Visible && WhatsNewOverlay.IsHitTestVisible) { _ = CloseWhatsNewAsync(); return true; }
        // From the welcome, one step back is the sign-in form it leads to.
        if (WelcomeOverlay.Visibility == Visibility.Visible && WelcomeOverlay.IsHitTestVisible) { WelcomeConnect_Click(this, new()); return true; }
        if (GuideOverlay.Visibility == Visibility.Visible && GuideOverlay.IsHitTestVisible) { _ = CloseGuideAsync(); return true; }
        if (LoginOverlay.Visibility == Visibility.Visible)
        {
            // From the Jellyfin setup, one step back is the sign-in form (nothing while an installation runs).
            if (ServerSetupPanel.Visibility == Visibility.Visible) { ShowServerSetup(false); return true; }
            if (BackToLibrary.Visibility != Visibility.Visible) return false; CloseAccount_Click(this, new()); return true;
        }
        if (SettingsOverlay.Visibility == Visibility.Visible) { _ = CloseSettingsAnimatedAsync(); return true; }
        if (DetailOverlay.Visibility == Visibility.Visible && (!_playing || _miniPlayer)) { _ = CloseDetailsAsync(); return true; }
        if (TorLinkOverlay.Visibility == Visibility.Visible && TorLinkOverlay.IsHitTestVisible && (!_playing || _miniPlayer)) { _ = CloseTorLinkAsync(); return true; }
        if (_playing && !_miniPlayer) { BackFromPlayer_Click(this, new()); return true; }
        // A person's titles, opened from a title page: back to that page.
        if (_person is not null && _personFrom is not null) { _ = BackFromPersonAsync(); return true; }
        return false;
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing) return; e.Cancel = true; _closing = true;
        // Each step on its own and the close guaranteed: a full disk or a failing service must not leave a hidden
        // process that keeps the single-instance lock (every relaunch would say "déjà ouvert"). Failures are logged.
        try
        {
            Attempt(RememberWindow);
            // Feels instant: the last progress report is still delivered while the window is already gone.
            Hide();
            // The settings page says changes are kept on leaving it: closing Mira leaves it too.
            Attempt(AutoSaveSettings);
            Attempt(() => { _windows?.Dispose(); _windows = null; });
            Attempt(() => { UpdateHeroClock(); ClosePreview(); SmoothScroll.Cancel(LibraryScroll); SmoothScroll.Cancel(DetailScroll); SmoothScroll.Cancel(SettingsScroll); SmoothScroll.Cancel(ResumeScroll); });
            _fallbackRefresh.Stop(); _searchTimer.Stop(); _externalRefresh.Stop();
            await AttemptAsync(() => StopPlaybackAsync()); await AttemptAsync(StopTorLinkAsync); Attempt(() => _profile.SaveSettings(_settings)); await AttemptAsync(DisconnectServicesAsync);
            Attempt(() => _videoHost?.Dispose()); Attempt(ApplyUpdateAtClose);
        }
        finally { _ = Dispatcher.BeginInvoke(Close); }
        static void Attempt(Action step) { try { step(); } catch (Exception ex) { ErrorLog.Append(AppFiles.ProfileDirectory, ex); } }
        static async Task AttemptAsync(Func<Task> step) { try { await step(); } catch (Exception ex) { ErrorLog.Append(AppFiles.ProfileDirectory, ex); } }
    }
    private static string TimeLabel(double seconds) { var time = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0)); return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}"; }
}
