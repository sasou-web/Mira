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
    /// <summary>
    /// The engine last released: mpv takes a few hundred milliseconds to end (its threads, its video window), off the UI
    /// thread so the library moves meanwhile; the next engine, and closing, wait for it.
    /// </summary>
    private Task _engineReleased = Task.CompletedTask;
    private IntPtr _videoHandle;
    /// <summary>What plays, where it is and what Jellyfin was told (Mira.Core, tested without the window).</summary>
    private readonly PlaybackSession _session = new();
    private bool _playing, _loaded, _seeking, _fullscreen, _closing, _playBusy;
    /// <summary>From the first close request: the window is hidden and its services are being stopped.</summary>
    internal bool IsClosing => _closing;
    private int _playGeneration;
    private bool _tickBusy;

    /// <param name="groupStart">Put on by « Regarder ensemble » (MainWindow.Together.cs): opened paused there, until the group plays.</param>
    /// <param name="chosen">Picked on a page (a title, a card, a menu), not by the player: in a group that has another title on, asked first.</param>
    private async Task PlayAsync(MediaItem item, bool keepMini = false, bool completePrevious = false, bool fromStart = false, long? groupStart = null, bool chosen = false)
    {
        ClosePreview(); var asked = item;
        if (_playBusy) return; _playBusy = true;
        // Opening a stream can take a moment: say so right away instead of looking frozen.
        if (!_playing) Mouse.OverrideCursor = Cursors.AppStarting;
        if (DetailOverlay.Visibility == Visibility.Visible && _detail is not null) _returnToDetail = _detail;
        try
        {
            // Without an engine nothing can play: say so before any request, with the one click that fixes it.
            if (MpvEngine.FindLibrary(_settings.MpvPath) is null)
            {
                SetNotice(L("Pour lire tes vidéos, Mira a besoin du moteur mpv (31 Mo).", "To play your videos, Mira needs the mpv video engine (31 MB)."), L("Installer", "Install"), () => _ = InstallEngineThenPlayAsync(item, keepMini, fromStart));
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
                item = next ?? throw new IOException(L("Aucun épisode disponible.", "No episodes available."));
            }
            if (InGroup && groupStart is null)
            {
                if (await AskGroupAsync(item, fromStart, completePrevious, chosen)) return;
                groupStart = _groupQueue?.StartTicks ?? 0;
            }
            string location; string? authorization = null; MediaSource? source = null; string session = ""; OfflineCopy? kept = null; ServerStream? stream = null;
            // A finished title starts over instead of resuming on its last seconds; « Depuis le début » too.
            double StartAt() => groupStart is { } ticks ? ticks / 1e7 : PlaybackSession.StartAt(item, remember: !_demo && _settings.RememberPosition && !fromStart, back: _settings.ResumeBack);
            if (_demo)
            {
                if (_testMedia is null)
                {
                    var dialog = new OpenFileDialog { Title = L("Choisir une vidéo locale pour essayer le lecteur", "Choose a local video to try the player"), Filter = L("Vidéos|*.mkv;*.mp4;*.avi;*.webm;*.mov;*.m4v;*.ts|Tous les fichiers|*.*", "Videos|*.mkv;*.mp4;*.avi;*.webm;*.mov;*.m4v;*.ts|All files|*.*") };
                    if (dialog.ShowDialog(this) != true) return;
                    location = dialog.FileName;
                }
                else location = _testMedia;
            }
            // Kept on this PC (« Garder sur ce PC »): its file, with or without Jellyfin; the progress goes as usual.
            else if (KeptCopy(item.Id) is { } copy) { kept = copy; location = copy.VideoPath; source = copy.Source; session = Guid.NewGuid().ToString("N"); }
            else
            {
                if (_client is null) return;
                // The file as it is, or Jellyfin's conversion under the quality chosen (MainWindow.PlayerStream.cs).
                stream = await OpenStreamAsync(_client, item, StartAt());
                source = stream.Source; session = stream.PlaySessionId; location = stream.Location; authorization = stream.Authorization;
            }
            await StopPlaybackAsync(completePrevious);
            _playingCopy = kept; UseStream(stream);
            // After the previous playback stopped: it may have been this title, now further on or watched.
            _session.Begin(item, source, session, StartAt(), stream?.PlayMethod ?? "DirectPlay"); _loaded = false;
            // "Épisode 3" at once; the total of the season follows with the episode list (LoadServerMarkersAsync).
            PlayingTitle.Text = MiniTitle.Text = item.DisplayTitle; PlayingSubtitle.Text = _demo ? L("Vidéo locale · mode démonstration", "Local video · demo mode") : PlayerText.Subtitle(item);
            // The next episode can start while the settings page is open over the mini-player: it leaves it.
            AutoSaveSettings(); DetailOverlay.Visibility = Visibility.Collapsed; SettingsOverlay.Visibility = Visibility.Collapsed;
            HidePlayerError(); LibraryShell.Visibility = Visibility.Collapsed; PlayerShell.Visibility = Visibility.Visible;
            NavigationRail.Visibility = Visibility.Collapsed;
            // The player's keys must reach Mira, not the TorLink terminal left open underneath.
            if (TorLinkOverlay.IsKeyboardFocusWithin) Focus();
            NextButton.IsEnabled = !_demo && item.Type == "Episode";
            // The episodes around this one follow with the series (LoadServerMarkersAsync).
            _playerEpisodes = []; PreviousButton.IsEnabled = PlayerEpisodesButton.IsEnabled = false;
            if (_videoHost is null)
            {
                var ready = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
                _videoHost = new VideoHost(); _videoHost.Ready += hwnd => { _videoHandle = hwnd; ready.TrySetResult(hwnd); };
                VideoContainer.Children.Add(_videoHost); _videoHandle = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            ResetMarkers();
            await _engineReleased;
            _mpv = new MpvEngine(_videoHandle, _settings, picture: PictureForPlayback());
            if (_audioDelay != 0) _mpv.Set("audio-delay", _audioDelay.ToString(CultureInfo.InvariantCulture));
            SayAudioMissing();
            // Kept to reopen the stream at its position after the PC slept (MainWindow.Power.cs) or a cut (MainWindow.PlayerStream.cs).
            // Someone starting a playback has the PC awake: no earlier sleep is left to handle.
            _playLocation = location; _playAuthorization = authorization; _fileOpen = false; _asleep = false; _lastTick = DateTime.UtcNow;
            PublishWindowsTrack(item);
            _mpv.FileLoaded += FileLoaded;
            _mpv.PlaybackRestarted += () => { _session.LastReport = DateTimeOffset.MinValue; GroupRestarted(); };
            var generation = ++_playGeneration;
            _ = LoadServerMarkersAsync(item, generation);
            _mpv.Ended += eof =>
            {
                // The stream replaced by its reopening (after sleep, another quality or track): not an end.
                if (_skipEnds > 0) { _skipEnds--; return; }
                _fileOpen = false;
                // The stream reopened after sleep could not load: an end, never that of the title.
                PlaybackEnded(eof && _wake != WakeState.Reloading, generation, interrupted: _asleep || _wake == WakeState.Waiting);
            };
            _mpv.Error += message => { _session.Failure = message; };
            _mpv.Message += message => Dispatcher.BeginInvoke(() => { if (generation == _playGeneration) HandlePlayerMessage(message); });
            _playing = true; UpdateHeroClock(); _session.LastReport = DateTimeOffset.Now; SetPlayerLoading(true); _fileOpen = true;
            // In a group: loaded paused, « ready » once there (GroupRestarted), played when Jellyfin says.
            _groupReadyPending = groupStart is not null; _groupStallSince = null; _groupStalled = false;
            if (groupStart is not null) { SetGroupAway(false); GroupReport("Buffering", _session.Position, false); }
            _mpv.Load(location, _session.Position, authorization, paused: groupStart is not null);
            if (_demo && _testMedia is not null && _args.Contains("--loop-test-media")) _mpv.Set("loop-file", "inf");
            SetMiniPlayer(keepMini, preserveFullscreen: true); _playerTimer.Start();
        }
        catch (Exception ex) when (IsExpected(ex) || ex is TimeoutException)
        {
            await StopPlaybackAsync();
            var reason = ex is IOException && FromMira(ex) ? ex.Message : ex is TimeoutException ? L("Le lecteur vidéo n’a pas pu s’ouvrir à temps.", "The video player couldn’t open in time.") : Friendly(ex);
            PlaybackFailed(L("La lecture n’a pas pu démarrer. ", "Playback couldn’t start. ") + reason, asked, keepMini, RetryText, () => PlayAsync(asked, keepMini));
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // An incompatible engine DLL must not leave a black screen with no way back.
            await StopPlaybackAsync();
            PlaybackFailed(L("Le moteur mpv choisi semble incompatible. Vérifie son emplacement dans Réglages → Lecture.", "The chosen mpv engine seems incompatible. Check its location in Settings → Playback."), asked, keepMini, L("Ouvrir les réglages", "Open settings"), () => { Settings_Click(this, new()); return Task.CompletedTask; });
        }
        finally { _playBusy = false; Mouse.OverrideCursor = null; }
    }
    /// <summary>
    /// The file Jellyfin plays, read directly when this PC sees it: checked off the UI thread, since it may be on a
    /// network share that takes seconds to answer, or never does. Then Jellyfin streams it instead.
    /// </summary>
    private static async Task<bool> ReachableFileAsync(string path)
    {
        try { return await Task.Run(() => File.Exists(path)).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { return false; }
    }
    private async void FileLoaded()
    {
        if (_mpv is null || _session.Item is null) return;
        var generation = _playGeneration;
        var reopened = _wake == WakeState.Reloading; _wake = WakeState.None; _recoveryLoad = false;
        _starvedSince = null; _buffering = false; ShowPlayerStatus(null);
        _loaded = true; SeekBar.IsEnabled = true; SetPlayerLoading(false); ShowPlayerControls(); _session.Paused = _mpv.Flag("pause"); _session.Duration = _mpv.Number("duration", _session.Duration); _session.Position = _mpv.Number("time-pos", _session.Position);
        _stallFrom = _session.Position;
        SeekBar.SetPlayback(_session.Position, _session.Duration, !_session.Paused, _mpv.Number("speed", 1)); LoadFileChapters();
        UpdateWindowsPlayback();
        // Reopened (after sleep, another quality, the connection back): the same playback goes on for Jellyfin, told at
        // the next tick under its new session.
        _session.LastReport = reopened ? DateTimeOffset.MinValue : DateTimeOffset.Now;
        if (!_demo && _sync is not null && !reopened) { _session.Reported = true; await _sync.RecordAsync("start", Report()); }
        if (generation != _playGeneration || _mpv is null) return;
        await RememberCurrentPlaybackAsync();
        if (generation != _playGeneration || _mpv is null) return;
        if (!_demo && _client is not null && _stream is { } stream)
        {
            // Subtitles fetched from Jellyfin take the token in their header too (its file on this PC opens without it).
            _mpv.SetAuthorization(_client.AuthorizationHeader);
            // External subtitles supplied by Jellyfin may not share the video's filename; a conversion's come when chosen.
            if (!stream.Converted)
                foreach (var (track, path) in ServerStreams.Beside(stream.Source, converted: false))
                {
                    try { _mpv.Command("sub-add", _client.ServerUri(path).AbsoluteUri, "auto", track.DisplayTitle ?? "", track.Language ?? ""); } catch (IOException) { }
                }
        }
        // The subtitle files kept beside a copy.
        if (_playingCopy is { } kept && kept.Item.Id == _session.Item.Id)
            foreach (var subtitle in kept.Subtitles)
            {
                try { _mpv.Command("sub-add", Path.Combine(kept.Folder, subtitle.File), "auto", subtitle.Title ?? "", subtitle.Language ?? ""); } catch (IOException) { }
            }
        // Whether « Chercher des sous-titres… » shows, known before the menu opens.
        _ = ShowSubtitleSearchAsync();
        await ChooseStartTracksAsync(reopened, generation);
    }
    /// <summary>
    /// The tracks a file starts with: the series' or the settings' languages; opened again, those playing before (by number
    /// on the same file, by language and name on another stream), or those chosen in a conversion's menu, then kept.
    /// </summary>
    private async Task ChooseStartTracksAsync(bool reopened, int generation)
    {
        if (_converted is { } converted)
        {
            var memory = reopened ? _reopenTracks : _session.Item is { } item ? await SeriesMemoryAsync(item) : null;
            if (generation != _playGeneration || !ReferenceEquals(converted, _converted)) return;
            ShowConvertedText(reopened && _reopenText is { } chosen ? chosen >= 0 ? chosen : null : converted.StartText(memory, _settings.SubtitleLanguage));
        }
        else if (reopened && _reloadTracks is not null) RestoreTracks();
        else if (reopened && _reopenTracks is { } tracks) ChooseTracks(tracks);
        else await ChooseSeriesTracksAsync(generation);
        if (generation != _playGeneration) return;
        if (reopened && _reopenChosen) RememberTracks();
        _reopenText = null; _reopenChosen = false; _reloadTracks = null;
    }
    private async void PlayerTick(object? sender, EventArgs e)
    {
        if (_mpv is null || !_playing) return;
        // Before polling mpv, and even while the previous tick still awaits its report: only a PC asleep stops the ticks.
        NoticeSleep();
        if (_tickBusy) return; _tickBusy = true;
        try
        {
            UpdatePlayerControls(); UpdateGroupPill(); _mpv.Poll(); if (_mpv is null || !_loaded || !_playing) return;
            ShowBuffering(); WatchStall(); KeepTracks();
            _session.Position = ReportedPosition(_mpv.Number("time-pos", _session.Position)); _session.Duration = _mpv.Number("duration", _session.Duration);
            var paused = _mpv.Flag("pause"); ShowPauseState(paused);
            // The timeline interpolates between these polls on every frame.
            if (!_seeking) { SeekBar.SetPlayback(_session.Position, _session.Duration, !paused, _mpv.Number("speed", 1)); PositionText.Text = TimeLabel(_session.Position); UpdateChapterText(_session.Position); }
            SeekBar.SetBuffered(_mpv.Number("demuxer-cache-time", 0)); DurationText.Text = TimeLabel(_session.Duration);
            var changed = paused != _session.Paused; _session.Paused = paused; if (changed && paused) ShowPlayerControls(); UpdateMuteIcon();
            if (changed) GroupPauseChanged(paused);
            WatchGroupStall(); WatchPicture(paused);
            if (changed && _miniStowed) MiniTabPulse.Opacity = paused ? .35 : 1;
            UpdateSkip(); CheckSleepTimer();
            UpdateWindowsPlayback();
            var vol = _mpv.Level; if (Math.Abs(VolumeSlider.Value - vol) > 1) VolumeSlider.Value = vol;
            if (_session.Item is not null) _session.Item.UserData.PlaybackPositionTicks = TimeSpan.FromSeconds(Math.Max(0, _session.Position)).Ticks;
            _recovery.Played(_session.Position);
            // Every 10 s while it plays, once when it pauses or resumes, never while it stays paused.
            if (!_demo && _sync is not null && _session.Reported && Cadence.ProgressDue(changed, paused, _session.LastReport, DateTimeOffset.Now))
            { _session.LastReport = DateTimeOffset.Now; await _sync.RecordAsync("progress", Report()); }
        }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); }
        finally { _tickBusy = false; }
    }
    private PlaybackReport Report() => _session.Report(_mpv?.Level ?? _settings.Volume, _mpv?.Flag("mute") ?? false);
    /// <param name="interrupted">Ended while the PC slept or before Jellyfin answered again: a broken connection, which
    /// the stream's reopening after sleep replaces, never the end of the title.</param>
    private void PlaybackEnded(bool eof, int generation, bool interrupted = false)
    {
        // mpv invalidates the native event buffer on the next poll. Defer disposal until Poll has returned.
        Dispatcher.BeginInvoke(async () =>
        {
            if (!_playing || generation != _playGeneration) return;
            if (interrupted) { _loaded = false; _session.Failure = null; SeekBar.IsEnabled = false; SetPlayerLoading(true); return; }
            // A stream from Jellyfin that stopped before the end of its title: the connection, as on Mira web.
            if (StreamCut()) { _ = RecoverStreamAsync(generation); return; }
            var previous = _session.Item; var error = _session.Failure; var keepMini = _miniPlayer; var stay = _skips.Stay; var following = _skips.Next;
            if (eof && _session.Duration > 0) _session.Position = _session.Duration;
            await StopPlaybackAsync(eof);
            // Watched to the end: shown as seen right away, and the next play starts from the beginning.
            if (eof && previous is not null) { previous.UserData.Played = true; previous.UserData.PlaybackPositionTicks = 0; }
            var stoppedGeneration = _playGeneration;
            // « Arrêter à la fin de l’épisode »: this one was the last.
            var last = eof && _sleep.AtEnd;
            if (last) _sleep.Clear();
            if (eof && _settings.AutoNext && !last && !_demo && !stay && previous?.Type == "Episode")
            {
                var next = following ?? await GetNextAsync(previous);
                if (stoppedGeneration != _playGeneration || _playing) return;
                if (next is not null) { await PlayAsync(next, keepMini); return; }
            }
            if (error is not null) { PlaybackFailed(error, previous, keepMini, RetryText, previous is null ? null : () => PlayAsync(previous, keepMini)); return; }
            ReturnToLibrary();
            if (!_demo) await RefreshAsync(quiet: true);
            else RenderAfterUserDataChange();
        });
    }
    /// <summary>
    /// A title that could not play. In the player, its own screen says so, with « Retour » and another try (as Mira
    /// web); in the mini-player, the person is browsing: the library stays, with a message that waits to be closed.
    /// </summary>
    private void PlaybackFailed(string message, MediaItem? item, bool mini, string action, Func<Task>? onAction)
    {
        if (mini) { ReturnToLibrary(); SetNotice(message, onAction is null ? null : action, onAction is null ? null : () => _ = onAction(), sticky: true); return; }
        if (_fullscreen) ToggleFullscreen();
        ClosePlayerPopups(); _miniPlayer = false; ResetMiniPlacement(); ClosePreview();
        DetailOverlay.Visibility = SettingsOverlay.Visibility = Visibility.Collapsed;
        LibraryShell.Visibility = NavigationRail.Visibility = Visibility.Collapsed;
        // The window's own buttons stay reachable: the player's are in its overlay, closed with the playback.
        PlayerShell.Visibility = TitleBar.Visibility = Visibility.Visible;
        // The native video surface would cover the message (airspace): hidden until the next playback.
        VideoContainer.Visibility = Visibility.Collapsed;
        PlayerErrorTitle.Text = item?.DisplayTitle ?? "";
        PlayerErrorTitle.Visibility = PlayerErrorTitle.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlayerErrorText.Text = _playerErrorMessage = message;
        _playerErrorAction = onAction; PlayerErrorAction.Content = action; PlayerErrorAction.IsEnabled = true;
        PlayerErrorAction.Visibility = onAction is null ? Visibility.Collapsed : Visibility.Visible;
        if (PlayerError.Visibility != Visibility.Visible) Motion.Reveal(PlayerError, 220, 8);
        Dispatcher.BeginInvoke(() => (onAction is null ? PlayerErrorBack : PlayerErrorAction).Focus(), DispatcherPriority.Input);
        Accessible.Say(PlayerErrorText);
    }
    private Func<Task>? _playerErrorAction;
    private string _playerErrorMessage = "";
    private static string RetryText => L("Réessayer", "Try again");
    private bool PlayerErrorShown => PlayerError.Visibility == Visibility.Visible;
    private void HidePlayerError()
    {
        if (!PlayerErrorShown) return;
        PlayerError.Visibility = Visibility.Collapsed; VideoContainer.Visibility = Visibility.Visible; _playerErrorAction = null;
    }
    private async void PlayerErrorBack_Click(object sender, RoutedEventArgs e)
    {
        HidePlayerError(); ReturnToLibrary();
        if (!_demo) await RefreshAsync(quiet: true); else RenderAfterUserDataChange();
    }
    private async void PlayerErrorAction_Click(object sender, RoutedEventArgs e)
    {
        if (_playerErrorAction is not { } action) return;
        // Another try keeps the screen until the player opens or fails again (which says why once more); settings leave it.
        if (!Equals(PlayerErrorAction.Content, RetryText)) { HidePlayerError(); ReturnToLibrary(); await action(); return; }
        PlayerErrorAction.IsEnabled = false; PlayerErrorText.Text = L("Nouvel essai…", "Trying again…"); Accessible.Say(PlayerErrorText);
        await action();
        // Neither (no account any more, a file choice cancelled in the demo): the screen comes back as it was.
        if (PlayerErrorShown && !PlayerErrorAction.IsEnabled) { PlayerErrorAction.IsEnabled = true; PlayerErrorText.Text = _playerErrorMessage; }
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
        if (_playing && _loaded && _mpv is not null && !completed) _session.Position = ReportedPosition(_mpv.Number("time-pos", _session.Position));
        completed = _session.Completes(_loaded, completed);
        _playing = false; _wake = WakeState.None; _skipEnds = 0; _reloadTracks = null; UpdateGroupPill(); ForgetStream();
        try
        {
            _windows?.Clear();
            if (_session.Reported && _sync is not null) { _session.Reported = false; await _sync.RecordAsync(completed ? "complete" : "stop", Report()); }
            if (completed && _session.Item is { } finished)
            {
                PlaybackSession.MarkWatched(finished, _catalog.Items.Concat(_catalog.Resume).Concat(_catalog.NextUp).Concat(_episodes), _catalog.Resume, _catalog.NextUp);
                _metadata?.Clear(); _heroSeries.Clear();
            }
            if (_loaded && _session.Item is { } stopped)
            {
                if (!completed) stopped.UserData.PlaybackPositionTicks = TimeSpan.FromSeconds(Math.Max(0, _session.Position)).Ticks;
                await RememberCurrentPlaybackAsync();
            }
        }
        // Whatever failed above, the engine is released: it would otherwise play on behind the library.
        finally { _loaded = false; if (_mpv is { } engine) { _settings.Volume = engine.Level; _mpv = null; _engineReleased = Task.Run(() => { var released = System.Diagnostics.Stopwatch.StartNew(); engine.Dispose(); FrameProbe.Finished("mpv released", released.Elapsed.TotalMilliseconds); }); } }
        if (hadPlayback && !_closing) using (FrameProbe.Time("RenderAfterUserDataChange")) RenderAfterUserDataChange();
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
        catch (Exception ex) when (IsExpected(ex))
        {
            // Without Jellyfin, the next episode kept on this PC.
            if (OfflineCopies.NextEpisode(_offline, current) is { } kept) return (kept.Item, []);
            if (!quiet) SetError(Friendly(ex));
            return (null, []);
        }
    }
    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Item is null) return; var next = _skips.Next ?? await GetNextAsync(_session.Item);
        if (next is not null) await PlayAsync(next, _miniPlayer, completePrevious: _skips.ToNext && _skips.Active is not null); else PlayingSubtitle.Text = L("Aucun épisode suivant disponible.", "No next episode available.");
    }
    private void BackFromPlayer_Click(object sender, RoutedEventArgs e)
    { if (_fullscreen) ToggleFullscreen(); else if (!_miniPlayer) SetMiniPlayerSmoothly(true); }
    /// <summary>Leaving the player for the library: the picture shrinks and fades over the page (<see cref="ClosePlayerSmoothlyAsync"/>).</summary>
    private async Task LeavePlayerAsync()
    {
        FrameProbe.Begin("stop");
        if (await ClosePlayerSmoothlyAsync()) return;
        if (_morphing) { ++_morphVersion; EndMorph(); }
        await StopPlaybackAsync(); ReturnToLibrary();
    }
    private void ReturnToLibrary(bool reveal = true)
    {
        if (_fullscreen) ToggleFullscreen(); ClosePlayerPopups(); _miniPlayer = false; ResetMiniPlacement(); _sleep.Clear();
        // The group plays on without this PC.
        SetGroupAway(true);
        PlayerShell.Visibility = Visibility.Collapsed; LibraryShell.Visibility = NavigationRail.Visibility = Visibility.Visible;
        TitleBar.Visibility = Visibility.Visible;
        if (reveal) Motion.Reveal(LibraryScroll); RestoreDetailPage(); UpdateHeroClock(); ShowPendingUpdateNotice();
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
        if (_loaded) ShowPlayState(pause);
        // In a group, play waits for Jellyfin, which starts every device at the same moment.
        if (PlayingForGroup && _loaded) { if (pause) QuietPause(true); SendGroup(pause ? "Pause" : "Unpause"); ShowPlayerControls(); return; }
        _mpv.Set("pause", pause ? "yes" : "no");
        // Swap the glyph now rather than at the next poll, so the press and the new icon are one movement.
        ShowPauseState(pause);
        SeekBar.SetPlayback(_session.Position, _session.Duration, !pause, _mpv.Number("speed", 1));
        UpdateWindowsPlayback();
        ShowPlayerControls();
    }
    /// <summary>The button shows what a press does: play while paused, pause while playing.</summary>
    private void ShowPauseState(bool paused)
    {
        Motion.Swap(PauseIcon, paused ? "play" : "pause");
        var label = paused ? L("Lecture · Espace", "Play · Space") : L("Pause · Espace", "Pause · Space");
        if (!Equals(PauseButton.ToolTip, label)) { PauseButton.ToolTip = label; System.Windows.Automation.AutomationProperties.SetName(PauseButton, paused ? L("Lecture", "Play") : "Pause"); }
    }
    private void SeekRelative(double seconds)
    {
        if (_mpv is null || !_loaded) return;
        // Absolute target from the last requested position: repeated presses add up, and the bar moves at once.
        var target = Math.Clamp(_session.Position + seconds, 0, _session.Duration > 0 ? Math.Max(0, _session.Duration - .5) : double.MaxValue);
        if (!TryCommand("seek", target.ToString(CultureInfo.InvariantCulture), "absolute+exact")) return;
        HoldPosition(target); ShowSeekPill(seconds);
    }
    /// <summary>mpv may report the pre-seek time for a poll or two; keep the requested time meanwhile.</summary>
    private void HoldPosition(double target)
    {
        _session.Hold(target, DateTimeOffset.UtcNow); SeekBar.Hold(target); PositionText.Text = TimeLabel(target); _stallFrom = target;
    }
    private double ReportedPosition(double reported) => _session.Settled(reported, DateTimeOffset.UtcNow);
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
            _fullscreenWindow!.Enter(_args.Contains("--player-check") ? CheckScreen() : null); _fullscreen = true;
        }
        else
        {
            _fullscreen = false; _fullscreenWindow!.Exit();
            _windowFrame!.SetFullscreen(false);
        }
        PlayerWindowChrome.Visibility = _fullscreen ? Visibility.Collapsed : Visibility.Visible;
        PlayerHeaderBody.Margin = new Thickness(24, _fullscreen ? 22 : 42, 24, 22);
        Motion.Swap(FullscreenIcon, _fullscreen ? "collapse" : "fullscreen");
        FullscreenButton.ToolTip = _fullscreen ? L("Quitter le plein écran · Échap", "Exit full screen · Esc") : L("Plein écran · F ou F11", "Full screen · F or F11");
        _changingPlayerLayout = false; PositionPlayerControls(); ShowPlayerControls();
    }
    /// <summary>The player check's screen, off view: 1600 × 900, or the monitor when smaller (Windows caps a window to it).</summary>
    private Rect CheckScreen()
    {
        var monitor = FullscreenWindow.ScreenBounds(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        return new Rect(-30000, -30000, Math.Min(1600, monitor.Width), Math.Min(900, monitor.Height));
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
        // The library's shortcuts wait for « Retour »: the error screen covers the library.
        if (PlayerErrorShown) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.K or Key.F && (!_playing || _miniPlayer)) { SearchNav_Click(this, new()); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && (!_playing || _miniPlayer) && RailShortcut(e.Key)) { e.Handled = true; return; }
        if (e.Key == Key.F1 && (!_playing || _miniPlayer)) { OpenGuide(); e.Handled = true; return; }
        if (e.OriginalSource is TextBox or System.Windows.Controls.PasswordBox || !_playing) return;
        // Space, arrows, M, F and S are handled earlier by Player_PreviewKeyDown, before any focused control.
        if (e.Key == Key.I && !e.IsRepeat) { SetMiniPlayerSmoothly(!_miniPlayer); e.Handled = true; }
    }
    /// <summary>Alt+← or the "back" key. WPF reports Alt+← as Key.System; keys forwarded by WebView2 carry the arrow itself.</summary>
    private static bool IsBackKey(KeyEventArgs e) => e.Key == Key.BrowserBack || Keyboard.Modifiers == ModifierKeys.Alt && (e.SystemKey == Key.Left || e.Key == Key.Left);
    /// <summary>One step back: menu, account screen, settings, title page, then the player.</summary>
    private bool GoBack()
    {
        if (PlayerOptionsPopup.IsOpen) { PlayerOptionsPopup.IsOpen = false; return true; }
        if (PlayerErrorShown) { PlayerErrorBack_Click(this, new()); return true; }
        if (ConfirmOverlay.Visibility == Visibility.Visible && ConfirmOverlay.IsHitTestVisible) { Answer(false); return true; }
        if (WhatsNewOverlay.Visibility == Visibility.Visible && WhatsNewOverlay.IsHitTestVisible) { _ = CloseWhatsNewAsync(); return true; }
        if (InviteOverlay.Visibility == Visibility.Visible && InviteOverlay.IsHitTestVisible) { _ = CloseInviteAsync(); return true; }
        if (IdentifyOverlay.Visibility == Visibility.Visible && IdentifyOverlay.IsHitTestVisible) { _ = CloseIdentifyAsync(); return true; }
        if (CollectionOverlay.Visibility == Visibility.Visible && CollectionOverlay.IsHitTestVisible) { _ = CloseCollectionsAsync(); return true; }
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
        if (DetailOverlay.Visibility == Visibility.Visible && (!_playing || _miniPlayer)) { _ = BackFromDetailsAsync(); return true; }
        if (TorLinkOverlay.Visibility == Visibility.Visible && TorLinkOverlay.IsHitTestVisible && (!_playing || _miniPlayer)) { _ = CloseTorLinkAsync(); return true; }
        if (_playing && !_miniPlayer) { BackFromPlayer_Click(this, new()); return true; }
        // A person's titles, opened from a title page or a search: back to it.
        if ((_catalog.Person is not null || _catalog.Saga is not null) && (_personFrom is not null || _personSearch is not null)) { _ = BackFromPersonAsync(); return true; }
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
            // Before the first await: when Windows shuts down or Setup closes Mira, WPF closes the window without
            // waiting for the rest (see SessionEnding).
            KeepForNextStart();
            Attempt(() => SystemEvents.PowerModeChanged -= PowerModeChanged);
            Attempt(() => { _windows?.Dispose(); _windows = null; });
            Attempt(() => { UpdateHeroClock(); ClosePreview(); SmoothScroll.Cancel(LibraryScroll); SmoothScroll.Cancel(DetailScroll); SmoothScroll.Cancel(SettingsScroll); SmoothScroll.Cancel(ResumeScroll); });
            _fallbackRefresh.Stop(); _searchTimer.Stop(); _externalRefresh.Stop();
            await AttemptAsync(() => StopPlaybackAsync()); await AttemptAsync(() => _engineReleased); await AttemptAsync(StopTorLinkAsync); Attempt(() => _profile.SaveSettings(_settings)); await AttemptAsync(DisconnectServicesAsync);
            // After the last reports to Jellyfin, which may go through it; before an update replaces its file.
            await AttemptAsync(StopTailnetAsync);
            Attempt(() => _videoHost?.Dispose()); await AttemptAsync(ApplyUpdateAtCloseAsync);
        }
        finally { _ = Dispatcher.BeginInvoke(Close); }
    }
    /// <summary>One step of closing: its failure is logged and the next step runs.</summary>
    private static void Attempt(Action step) { try { step(); } catch (Exception ex) { ErrorLog.Append(AppFiles.ProfileDirectory, ex); } }
    private static async Task AttemptAsync(Func<Task> step) { try { await step(); } catch (Exception ex) { ErrorLog.Append(AppFiles.ProfileDirectory, ex); } }
    private static string TimeLabel(double seconds) { var time = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0)); return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}"; }
}
