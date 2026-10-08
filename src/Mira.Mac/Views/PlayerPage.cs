using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Playback;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>
/// The player, over the whole window: mpv's picture, then Mira's controls, hidden after a moment without movement.
/// Reports start, progress and end to Jellyfin through the same queue as the Windows app, so progress is never lost
/// offline, and plays the next episode at the end of one.
/// </summary>
public sealed class PlayerPage : UserControl
{
    private readonly MainWindow _shell;
    private readonly Grid _root = new() { Background = Brushes.Black };
    private readonly Panel _videoHost = new();
    private readonly Grid _chrome = new();
    private readonly Border _top, _bottom;
    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _subtitle = new() { FontSize = 12.5, Foreground = Ui.Brush("#A9A9B1"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _chapter = new() { FontSize = 12.5, Foreground = Ui.Brush("#A9A9B1"), HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 };
    private readonly TextBlock _time = new() { FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) };
    private readonly Timeline _timeline = new();
    private readonly Icon _pauseIcon = new("pause", 26), _volumeIcon = new("volume", 24), _fullscreenIcon = new("fullscreen", 22);
    private readonly Slider _volume = new() { Minimum = 0, Maximum = VolumeBoost.Maximum, SmallChange = 5, LargeChange = 5, Width = 112, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _volumeText = new() { FontSize = 12, Width = 42, Foreground = Ui.Brush("#A9A9B1"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _next, _tracks, _more, _skip, _skipCancel;
    private readonly TextBlock _skipText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly LoadingArc _spinner = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromMilliseconds(2600) };
    private readonly DispatcherTimer _click = new() { Interval = TimeSpan.FromMilliseconds(230) };
    private MpvPlayer? _mpv;
    private IVideoSurface? _surface;
    private MediaItem? _item;
    private MediaSource? _source;
    private string _playSession = "";
    private bool _loaded, _paused, _reported, _stopped, _controlsShown = true, _initializing;
    private double _position, _duration, _pendingSeek, _lastAudible = 80;
    private DateTimeOffset _pendingUntil, _lastReport;
    private string? _failure;
    private int _generation;
    private List<SkipSegment> _serverSegments = [], _segments = [];
    private List<ChapterMark> _fileChapters = [], _serverChapters = [];
    private List<MediaItem> _episodes = [];
    private MediaItem? _nextItem;
    private SkipSegment? _activeSkip;
    private bool _skipToNext, _autoNextCancelled;
    private double? _countdownEnd;
    private const double NextEpisodeLead = 20, CountdownLength = 10;
    /// <summary>For the self-check.</summary>
    public MpvPlayer? Engine => _mpv;
    public IVideoSurface? Surface => _surface;
    public bool IsFileLoaded => _loaded;
    public double Position => _position;
    public MediaItem? Item => _item;
    public MediaItem? NextItem => _nextItem;
    public string Volume => _volumeText.Text ?? "";

    public PlayerPage(MainWindow shell)
    {
        _shell = shell;
        _root.Children.Add(_videoHost);
        _root.Children.Add(_spinner);

        var back = Ui.IconButton("back", "Retour · Échap", () => _ = _shell.StopPlaybackAsync(), "player", 24);
        var heading = Ui.Column(0, _title, _subtitle); heading.VerticalAlignment = VerticalAlignment.Center; heading.Margin = new Thickness(10, 0, 0, 0);
        var topRow = Ui.Row(0, back, heading);
        _top = new Border
        {
            VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(24, 18 + shell.TitleBarInset, 24, 40), Child = topRow,
            Background = Fade(true)
        };

        _next = Ui.IconButton("next-episode", "Épisode suivant · N", () => { if (_nextItem is { } next) _ = PlayNextAsync(next); }, "player", 24);
        _tracks = Ui.IconButton("subtitles", "Audio et sous-titres", () => { }, "player", 23);
        _more = Ui.IconButton("more", "Vitesse et décalage des sous-titres", () => { }, "player", 24);
        _tracks.Flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedRight, ShowMode = FlyoutShowMode.Standard };
        _more.Flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedRight, ShowMode = FlyoutShowMode.Standard };
        ((Flyout)_tracks.Flyout).Opening += (_, _) => ((Flyout)_tracks.Flyout).Content = TracksMenu();
        ((Flyout)_more.Flyout).Opening += (_, _) => ((Flyout)_more.Flyout).Content = MoreMenu();
        var play = new Button { Content = _pauseIcon, Classes = { "player" } }; play.Click += (_, _) => TogglePause();
        ToolTip.SetTip(play, "Lecture / pause · Espace");
        var rewind = Ui.IconButton("rewind", "−10 s · ←", () => SeekBy(-10), "player", 23);
        var forward = Ui.IconButton("forward", "+10 s · →", () => SeekBy(10), "player", 23);
        var mute = new Button { Content = _volumeIcon, Classes = { "player" } }; mute.Click += (_, _) => ToggleMute();
        ToolTip.SetTip(mute, "Couper le son · M");
        // 100 % sits at the middle, marked under the track; to the right, the sound is raised without clipping.
        var volumeTrack = new Grid { Width = 112, Margin = new Thickness(4, 0, 0, 0) };
        volumeTrack.Children.Add(new Border { Width = 2, Height = 10, CornerRadius = new CornerRadius(1), Background = Ui.Brush("#77777D"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        volumeTrack.Children.Add(_volume);
        ToolTip.SetTip(_volume, "Au-delà de 100 %, le son est amplifié sans saturer");
        Avalonia.Automation.AutomationProperties.SetName(_volume, "Volume");
        var volumeGroup = Ui.Row(0, mute, volumeTrack, _volumeText);
        volumeGroup.Background = Brushes.Transparent;
        volumeGroup.PointerWheelChanged += (_, e) => { ChangeVolume(e.Delta.Y > 0 ? 5 : -5); e.Handled = true; };
        _volume.ValueChanged += (_, e) => VolumeChanged(e.NewValue);
        var fullscreen = new Button { Content = _fullscreenIcon, Classes = { "player" } }; fullscreen.Click += (_, _) => ToggleFullscreen();
        ToolTip.SetTip(fullscreen, "Plein écran · F");

        var left = Ui.Row(4, play, rewind, forward, _next, volumeGroup);
        var right = Ui.Row(4, _tracks, _more, fullscreen);
        var transport = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(-8, 4, -8, 0) };
        transport.Children.Add(left); Grid.SetColumn(right, 2); transport.Children.Add(right);
        var info = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 4) };
        var timeColumn = Ui.Column(0, _chapter, _time); timeColumn.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(timeColumn, 1); info.Children.Add(timeColumn);
        _timeline.Seek += SeekTo;
        var body = Ui.Column(2, info, _timeline, transport);
        _bottom = new Border { VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(28, 90, 28, 16), Child = body, Background = Fade(false) };
        _chrome.Children.Add(_top); _chrome.Children.Add(_bottom);
        foreach (var part in new Control[] { _top, _bottom }) part.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) }];
        _root.Children.Add(_chrome);

        _skip = new Button { Classes = { "pill" }, Content = Ui.Row(10, _skipText, new Icon("skip", 16) { VerticalAlignment = VerticalAlignment.Center }), IsVisible = false };
        _skip.Click += (_, _) => Skip();
        _skipCancel = new Button { Classes = { "pill" }, Content = "Annuler", IsVisible = false, Margin = new Thickness(0, 0, 10, 0) };
        _skipCancel.Click += (_, _) => { _countdownEnd = null; _autoNextCancelled = true; _skipText.Text = PlaybackMarkers.Label(SkipKind.Outro, true, true); _skipCancel.IsVisible = false; };
        ToolTip.SetTip(_skipCancel, "Rester sur cet épisode");
        var skipHost = Ui.Row(0, _skipCancel, _skip);
        skipHost.HorizontalAlignment = HorizontalAlignment.Right; skipHost.VerticalAlignment = VerticalAlignment.Bottom; skipHost.Margin = new Thickness(0, 0, 36, 150);
        _root.Children.Add(skipHost);
        Content = _root;

        _tick.Tick += (_, _) => Tick();
        _hide.Tick += (_, _) => { _hide.Stop(); SetControls(false); };
        _click.Tick += (_, _) => { _click.Stop(); TogglePause(); };
        _videoHost.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(_videoHost).Properties.IsLeftButtonPressed) return;
            // A double click toggles fullscreen; a single one, after a short wait, pauses or resumes.
            if (e.ClickCount >= 2) { _click.Stop(); ToggleFullscreen(); } else _click.Start();
        };
        PointerMoved += (_, _) => ShowControls();
        _initializing = true; _volume.Value = VolumeBoost.Clamp(shell.Settings.Volume); _initializing = false;
        if (_volume.Value > 0) _lastAudible = _volume.Value;
        ShowVolume();
    }
    private static LinearGradientBrush Fade(bool top) => new()
    {
        StartPoint = new RelativePoint(0, top ? 0 : 1, RelativeUnit.Relative), EndPoint = new RelativePoint(0, top ? 1 : 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(top ? "#B0000000" : "#EB000000"), 0), new GradientStop(Color.Parse(top ? "#00000000" : "#8C000000"), top ? 1 : 0.5), new GradientStop(Color.Parse("#00000000"), 1) }
    };

    // —— Start and stop ——
    /// <summary>Opens <paramref name="item"/> (a series: its next episode); false when it could not start.</summary>
    public async Task<bool> StartAsync(MediaItem item, bool fromStart = false)
    {
        if (_shell.Session is not { } session) return false;
        var generation = ++_generation;
        _stopped = false; _loaded = false; _reported = false; _failure = null; ResetMarkers();
        _spinner.IsVisible = true; ShowControls();
        try
        {
            if (item.Type == "Series")
            {
                var next = await session.NextEpisodeAsync(item.Id);
                if (next is null)
                {
                    var episodes = (await session.EpisodesAsync(item.Id)).Items;
                    await Task.Run(() => session.Store.ApplyLocalProgress(episodes));
                    next = episodes.FirstOrDefault(x => x.Progress > 0 && !x.UserData.Played) ?? episodes.FirstOrDefault(x => !x.UserData.Played) ?? episodes.FirstOrDefault();
                }
                item = next ?? throw new IOException("Aucun épisode disponible.");
            }
            var playback = await session.Client.PlaybackAsync(item.Id);
            var source = playback.MediaSources.FirstOrDefault() ?? throw new IOException("Aucune source lisible disponible.");
            string location; string? authorization = null;
            // Jellyfin on this same Mac: its file directly; otherwise the original stream, never transcoded.
            if (new Uri(session.Connection.Server).IsLoopback && string.Equals(source.Protocol, "File", StringComparison.OrdinalIgnoreCase) && source.Path is { } path && File.Exists(path)) location = path;
            else { location = session.Client.StreamUri(item.Id, source.Id).AbsoluteUri; authorization = session.Client.AuthorizationHeader; }
            if (generation != _generation) return false;
            _item = item; _source = source; _playSession = string.IsNullOrEmpty(playback.PlaySessionId) ? Guid.NewGuid().ToString("N") : playback.PlaySessionId;
            var finished = item.UserData.Played || item.Progress >= LibraryStore.WatchedThreshold;
            _position = fromStart || finished || !_shell.Settings.RememberPosition ? 0 : TimeSpan.FromTicks(item.UserData.PlaybackPositionTicks).TotalSeconds;
            _duration = TimeSpan.FromTicks(item.RunTimeTicks ?? 0).TotalSeconds;
            _title.Text = item.DisplayTitle; _subtitle.Text = PlayerText.Subtitle(item);
            _next.IsEnabled = false;

            await OpenEngineAsync();
            if (_mpv is null || generation != _generation) return false;
            _mpv.Load(location, _position, authorization);
            _lastReport = DateTimeOffset.Now;
            _tick.Start();
            _ = LoadMarkersAsync(session, item, generation);
            return true;
        }
        catch (Exception ex) when (Errors.Expected(ex) || ex is InvalidOperationException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            await StopAsync();
            _shell.Notice(ex is IOException or FileNotFoundException ? ex.Message : ex is InvalidOperationException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException
                ? "La lecture n’a pas pu démarrer : le moteur mpv de Mira.app ne s’est pas chargé." : Errors.Friendly(ex));
            return false;
        }
    }
    /// <summary>mpv and the view it draws into: OpenGL, else (no OpenGL, or MIRA_SOFTWARE_VIDEO set) in software.</summary>
    private async Task OpenEngineAsync()
    {
        await ReleaseEngineAsync();
        var player = new MpvPlayer(_shell.Settings);
        _mpv = player;
        player.FileLoaded += FileLoaded;
        player.PlaybackRestarted += () => _lastReport = DateTimeOffset.MinValue;
        var generation = _generation;
        player.Ended += eof => Dispatcher.UIThread.Post(() => _ = EndedAsync(eof, generation));
        player.Error += message => _failure = message;
        IVideoSurface surface = Environment.GetEnvironmentVariable("MIRA_SOFTWARE_VIDEO") is { Length: > 0 } ? new SoftwareVideoView() : new VideoView();
        if (!await AttachAsync(surface, player) && surface is VideoView)
        {
            // This window has no OpenGL: the software view always works.
            await DetachAsync(surface);
            surface = new SoftwareVideoView();
            if (!await AttachAsync(surface, player)) throw new InvalidOperationException("Aucun rendu vidéo disponible.");
        }
        player.SetLevel(_volume.Value);
    }
    private async Task<bool> AttachAsync(IVideoSurface surface, MpvPlayer player)
    {
        _surface = surface;
        _videoHost.Children.Add(surface.View);
        try { return await surface.AttachAsync(player).WaitAsync(TimeSpan.FromSeconds(4)); }
        catch (TimeoutException) { return false; }
    }
    private async Task DetachAsync(IVideoSurface surface)
    {
        // Leaving the window frees mpv's render context with OpenGL current (VideoView.OnOpenGlDeinit).
        _videoHost.Children.Remove(surface.View);
        surface.Release();
        await Task.Yield();
        if (_surface == surface) _surface = null;
    }
    private async Task ReleaseEngineAsync()
    {
        _tick.Stop();
        if (_surface is { } surface) await DetachAsync(surface);
        if (_mpv is { } mpv) { _mpv = null; mpv.Dispose(); }
    }

    /// <summary>Stops playback, reporting where it stopped (or that it ended) and remembering it for Continuer à regarder.</summary>
    public async Task StopAsync(bool completed = false)
    {
        if (_stopped) return;
        _stopped = true; ++_generation;
        _tick.Stop(); _hide.Stop(); Cursor = Cursor.Default;
        SleepGuard.Set(false);
        var session = _shell.Session;
        if (_loaded && _mpv is not null && !completed) _position = Reported(_mpv.Number("time-pos", _position));
        completed = _loaded && (completed || _duration > 0 && _position >= _duration * LibraryStore.WatchedThreshold);
        if (_reported && session is not null) { _reported = false; await session.Sync.RecordAsync(completed ? "complete" : "stop", Report()); }
        if (_loaded && _item is { } item && session is not null)
        {
            item.UserData.Played = item.UserData.Played || completed;
            item.UserData.PlaybackPositionTicks = completed ? 0 : TimeSpan.FromSeconds(Math.Max(0, _position)).Ticks;
            item.UserData.LastPlayedDate = DateTimeOffset.UtcNow;
            var snapshot = item with { UserData = item.UserData with { } };
            await Task.Run(() => session.Store.RememberPlayback(snapshot));
        }
        if (_mpv is not null) _shell.Settings.Volume = _mpv.Level;
        _loaded = false;
        await ReleaseEngineAsync();
    }
    private async Task EndedAsync(bool eof, int generation)
    {
        if (generation != _generation || _stopped) return;
        var error = _failure; var next = _nextItem; var stay = _autoNextCancelled;
        if (eof && _duration > 0) _position = _duration;
        if (eof && _shell.Settings.AutoNext && !stay && _item?.Type == "Episode" && (next ?? await NextAfterAsync(_item)) is { } following)
        { await PlayNextAsync(following, completed: true); return; }
        await StopAsync(eof);
        await _shell.CloseOverlayPlayerAsync(this);
        if (error is not null) _shell.Notice(error);
    }
    private async Task<MediaItem?> NextAfterAsync(MediaItem current)
    {
        if (_shell.Session is not { } session || current.SeriesId is null) return null;
        try { var episodes = (await session.EpisodesAsync(current.SeriesId)).Items; var index = episodes.FindIndex(x => x.Id == current.Id); return index >= 0 ? episodes.ElementAtOrDefault(index + 1) : null; }
        catch (Exception ex) when (Errors.Expected(ex)) { return null; }
    }
    /// <summary>The next episode in this same player: the window stays as it is (fullscreen included).</summary>
    private async Task PlayNextAsync(MediaItem next, bool completed = false)
    {
        await StopAsync(completed || _skipToNext && _activeSkip is not null);
        if (!await StartAsync(next)) await _shell.CloseOverlayPlayerAsync(this);
    }

    private async void FileLoaded()
    {
        if (_mpv is not { } mpv || _item is null || _shell.Session is not { } session) return;
        var generation = _generation;
        _loaded = true; _spinner.IsVisible = false;
        _paused = mpv.Flag("pause"); _duration = mpv.Number("duration", _duration); _position = mpv.Number("time-pos", _position);
        _fileChapters = mpv.Chapters(); Rebuild();
        ShowControls();
        _lastReport = DateTimeOffset.Now;
        _reported = true; await session.Sync.RecordAsync("start", Report());
        if (generation != _generation || _mpv is null || _source is null) return;
        // External subtitles supplied by Jellyfin, fetched with the same authorization as the video.
        foreach (var stream in _source.MediaStreams.Where(s => s.Type == "Subtitle" && s.IsExternal && s.DeliveryUrl is not null))
        {
            var server = new Uri(session.Connection.Server);
            var uri = new Uri(server, stream.DeliveryUrl!);
            if (uri.GetLeftPart(UriPartial.Authority) != server.GetLeftPart(UriPartial.Authority)) continue;
            _mpv.Set("http-header-fields", "Authorization: " + session.Client.AuthorizationHeader.Replace(",", "\\,"));
            _mpv.TryCommand("sub-add", uri.AbsoluteUri, "auto", stream.DisplayTitle ?? "", stream.Language ?? "");
        }
    }
    private PlaybackReport Report() => new()
    {
        ItemId = _item?.Id ?? "", MediaSourceId = _source?.Id ?? "", PlaySessionId = _playSession,
        PositionTicks = TimeSpan.FromSeconds(Math.Max(0, _position)).Ticks, IsPaused = _paused,
        VolumeLevel = VolumeBoost.Reported(_mpv?.Level ?? _volume.Value), IsMuted = _mpv?.Flag("mute") ?? false
    };

    // —— Every 100 ms ——
    private bool _tickBusy;
    private async void Tick()
    {
        if (_tickBusy || _mpv is not { } mpv) return;
        _tickBusy = true;
        try
        {
            mpv.Poll();
            if (_mpv is null || !_loaded || _stopped) return;
            _position = Reported(mpv.Number("time-pos", _position)); _duration = mpv.Number("duration", _duration);
            var paused = mpv.Flag("pause");
            SleepGuard.Set(!paused);
            if (paused != _paused) { _paused = paused; _pauseIcon.Kind = paused ? "play" : "pause"; if (paused) ShowControls(); _lastReport = DateTimeOffset.MinValue; }
            _timeline.SetPlayback(_position, _duration, mpv.Number("demuxer-cache-time", 0));
            _time.Text = $"{Timeline.Clock(_position)} / {Timeline.Clock(_duration)}";
            var chapters = _fileChapters.Count > 0 ? _fileChapters : _serverChapters;
            _chapter.Text = chapters.LastOrDefault(c => c.Start <= _position + .25).Title ?? "";
            _chapter.IsVisible = _chapter.Text.Length > 0;
            UpdateSkip();
            if (_item is not null) _item.UserData.PlaybackPositionTicks = TimeSpan.FromSeconds(Math.Max(0, _position)).Ticks;
            if (_reported && _shell.Session is { } session && DateTimeOffset.Now - _lastReport >= TimeSpan.FromSeconds(3))
            { _lastReport = DateTimeOffset.Now; await session.Sync.RecordAsync("progress", Report()); }
        }
        catch (Exception ex) when (Errors.Expected(ex)) { }
        finally { _tickBusy = false; }
    }
    /// <summary>mpv may report the pre-seek time for a poll or two; the requested time holds meanwhile.</summary>
    private double Reported(double reported) => DateTimeOffset.UtcNow < _pendingUntil && Math.Abs(reported - _pendingSeek) > 1.5 ? _pendingSeek : reported;

    // —— Controls ——
    private void SeekTo(double seconds)
    {
        if (_mpv is null || !_loaded) return;
        var target = Math.Clamp(seconds, 0, _duration > 0 ? Math.Max(0, _duration - .5) : double.MaxValue);
        if (!_mpv.TryCommand("seek", target.ToString(CultureInfo.InvariantCulture), "absolute+keyframes")) return;
        Hold(target);
    }
    private void SeekBy(double seconds)
    {
        if (_mpv is null || !_loaded) return;
        // From the last requested position: repeated presses add up, and the bar moves at once.
        var target = Math.Clamp(_position + seconds, 0, _duration > 0 ? Math.Max(0, _duration - .5) : double.MaxValue);
        if (!_mpv.TryCommand("seek", target.ToString(CultureInfo.InvariantCulture), "absolute+exact")) return;
        Hold(target); ShowControls();
    }
    private void Hold(double target)
    {
        _position = target; _pendingSeek = target; _pendingUntil = DateTimeOffset.UtcNow.AddMilliseconds(800);
        _timeline.Hold(target); _time.Text = $"{Timeline.Clock(target)} / {Timeline.Clock(_duration)}"; _lastReport = DateTimeOffset.MinValue;
    }
    private void TogglePause()
    {
        if (_mpv is null) return;
        var pause = !_mpv.Flag("pause");
        _mpv.Set("pause", pause ? "yes" : "no");
        _paused = pause; _pauseIcon.Kind = pause ? "play" : "pause";
        _lastReport = DateTimeOffset.MinValue; ShowControls();
    }
    private void VolumeChanged(double value)
    {
        ShowVolume();
        if (_initializing) return;
        _shell.Settings.Volume = value; _mpv?.SetLevel(value);
        if (value > 0) { _lastAudible = value; _mpv?.Set("mute", "no"); }
        ShowVolume();
    }
    private void ChangeVolume(double delta) { _volume.Value = Math.Clamp(_volume.Value + delta, 0, VolumeBoost.Maximum); ShowControls(); }
    private void ToggleMute()
    {
        if (_mpv is null) return;
        if (_mpv.Flag("mute") || _volume.Value <= 0) { if (_volume.Value <= 0) _volume.Value = _lastAudible; _mpv.Set("mute", "no"); }
        else _mpv.Set("mute", "yes");
        ShowVolume(); ShowControls();
    }
    /// <summary>The level beside the slider, brighter above 100 % where the sound is amplified.</summary>
    private void ShowVolume()
    {
        var muted = _mpv?.Flag("mute") == true || _volume.Value <= 0;
        _volumeIcon.Kind = muted ? "mute" : _volume.Value < 50 ? "volume-low" : "volume";
        var boosted = _volume.Value > 100.5;
        _volumeText.Text = VolumeBoost.Text(_volume.Value);
        _volumeText.Foreground = boosted ? Brushes.White : Ui.Brush("#A9A9B1");
        _volumeText.FontWeight = boosted ? FontWeight.SemiBold : FontWeight.Normal;
    }
    private void ToggleFullscreen()
    {
        _shell.WindowState = _shell.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
        _fullscreenIcon.Kind = _shell.WindowState == WindowState.FullScreen ? "collapse" : "fullscreen";
        ShowControls();
    }

    private void ShowControls()
    {
        SetControls(true);
        _hide.Stop();
        _hide.Interval = TimeSpan.FromMilliseconds(_paused ? 4000 : 2600);
        _hide.Start();
    }
    private void SetControls(bool shown)
    {
        // The controls stay while a menu is open or the bar is being dragged.
        if (!shown && (_timeline.Dragging || _tracks.Flyout?.IsOpen == true || _more.Flyout?.IsOpen == true || _bottom.IsPointerOver)) { _hide.Start(); return; }
        _controlsShown = shown;
        _top.Opacity = _bottom.Opacity = shown ? 1 : 0;
        _top.IsHitTestVisible = _bottom.IsHitTestVisible = shown;
        Cursor = shown ? Cursor.Default : new Cursor(StandardCursorType.None);
        UpdateSkip();
    }

    public void HandleKey(KeyEventArgs e)
    {
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.Space or Key.K: TogglePause(); break;
            case Key.Left or Key.J: SeekBy(-10); break;
            case Key.Right or Key.L: SeekBy(10); break;
            case Key.Up: ChangeVolume(5); break;
            case Key.Down: ChangeVolume(-5); break;
            case Key.M: ToggleMute(); break;
            case Key.F: ToggleFullscreen(); break;
            case Key.S: if (_activeSkip is not null) Skip(); break;
            case Key.N: if (_nextItem is { } next) _ = PlayNextAsync(next); break;
            case Key.Escape:
                if (_shell.WindowState == WindowState.FullScreen) ToggleFullscreen(); else _ = _shell.StopPlaybackAsync();
                break;
            case Key.OemOpenBrackets when command: _ = _shell.StopPlaybackAsync(); break;
            default: return;
        }
        e.Handled = true;
    }

    // —— Menus ——
    private Control TracksMenu()
    {
        var tracks = _mpv?.Tracks() ?? [];
        var current = (Audio: _mpv?.Get("aid"), Sub: _mpv?.Get("sid"));
        Control Column(string title, string type, string? selected, bool off)
        {
            var list = new StackPanel { Spacing = 2, MinWidth = 240 };
            list.Children.Add(new TextBlock { Text = title, Classes = { "label" }, Margin = new Thickness(10, 0, 0, 8) });
            if (off) list.Children.Add(Entry("Désactivés", null, selected is "no" or null, () => _mpv?.Set("sid", "no")));
            foreach (var track in tracks.Where(x => x.Type == type))
                list.Children.Add(Entry(track.Label, track.Details, selected == track.Id, () => _mpv?.Set(type == "audio" ? "aid" : "sid", track.Id)));
            if (type == "sub") list.Children.Add(Entry("Charger un fichier…", null, false, () => _ = LoadSubtitleAsync()));
            return list;
        }
        return Ui.Row(18, Column("AUDIO", "audio", current.Audio, false), Column("SOUS-TITRES", "sub", current.Sub, true));
    }
    private Control MoreMenu()
    {
        var speed = _mpv?.Number("speed", 1) ?? 1; var delay = _mpv?.Number("sub-delay", 0) ?? 0;
        var speeds = new StackPanel { Spacing = 2, MinWidth = 180 };
        speeds.Children.Add(new TextBlock { Text = "VITESSE", Classes = { "label" }, Margin = new Thickness(10, 0, 0, 8) });
        foreach (var value in new[] { 0.75, 1, 1.25, 1.5, 2 })
            speeds.Children.Add(Entry(PlayerText.Speed(value), null, Math.Abs(speed - value) < .01, () => _mpv?.Set("speed", value.ToString(CultureInfo.InvariantCulture))));
        var label = new TextBlock { Text = PlayerText.Delay(delay), Width = 64, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        void Shift(double change)
        {
            if (_mpv is null) return;
            var value = change == 0 ? 0 : Math.Round(_mpv.Number("sub-delay", 0) + change, 1);
            _mpv.Set("sub-delay", value.ToString(CultureInfo.InvariantCulture)); label.Text = PlayerText.Delay(value);
        }
        var shift = Ui.Row(6, Ui.IconButton("minus", "Sous-titres plus tôt", () => Shift(-0.5), "icon", 18), label, Ui.IconButton("plus", "Sous-titres plus tard", () => Shift(0.5), "icon", 18), Ui.IconButton("refresh", "Remettre à zéro", () => Shift(0), "icon", 18));
        var delays = Ui.Column(8, new TextBlock { Text = "DÉCALAGE DES SOUS-TITRES", Classes = { "label" }, Margin = new Thickness(10, 0, 0, 0) }, shift);
        return Ui.Row(22, speeds, delays);
    }
    private Button Entry(string label, string? details, bool selected, Action choose)
    {
        var text = Ui.Column(1, new TextBlock { Text = label, FontSize = 14, FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal });
        if (!string.IsNullOrEmpty(details)) text.Children.Add(new TextBlock { Text = details, FontSize = 12, Foreground = Ui.Muted });
        var row = new DockPanel();
        var check = new Icon("check", 16) { Opacity = selected ? 1 : 0, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(check, Dock.Left); row.Children.Add(check); row.Children.Add(text);
        var button = new Button { Content = row, Classes = { "quiet" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 8), FontWeight = FontWeight.Normal };
        // The menu stays open, so the other track can be chosen too; it refreshes its marks.
        button.Click += (_, _) => { choose(); RefreshMenus(); };
        return button;
    }
    private void RefreshMenus()
    {
        if (_tracks.Flyout is Flyout { IsOpen: true } tracks) tracks.Content = TracksMenu();
        if (_more.Flyout is Flyout { IsOpen: true } more) more.Content = MoreMenu();
    }
    private async Task LoadSubtitleAsync()
    {
        (_tracks.Flyout as Flyout)?.Hide();
        var files = await _shell.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choisir un fichier de sous-titres", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Sous-titres") { Patterns = ["*.srt", "*.ass", "*.ssa", "*.vtt", "*.sub"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path && _mpv?.TryCommand("sub-add", path, "select") != true)
            _shell.Notice("Ce fichier de sous-titres n’a pas pu être chargé.");
    }

    // —— Chapters, skippable passages and the next episode ——
    private void ResetMarkers()
    {
        _serverSegments = []; _segments = []; _fileChapters = []; _serverChapters = []; _episodes = [];
        _activeSkip = null; _nextItem = null; _skipToNext = false; _countdownEnd = null; _autoNextCancelled = false;
        _skip.IsVisible = false; _skipCancel.IsVisible = false; _timeline.SetChapters([]); _chapter.Text = "";
    }
    private async Task LoadMarkersAsync(Session session, MediaItem item, int generation)
    {
        try { var segments = await session.Client.SegmentsAsync(item.Id); if (generation == _generation) _serverSegments = PlaybackMarkers.FromJellyfin(segments); }
        catch (Exception ex) when (Errors.Expected(ex)) { }
        try { var chapters = await session.Client.ChaptersAsync(item.Id); if (generation == _generation) _serverChapters = PlaybackMarkers.FromJellyfin(chapters); }
        catch (Exception ex) when (Errors.Expected(ex)) { }
        if (item.Type == "Episode" && item.SeriesId is { } series)
        {
            try
            {
                var episodes = (await session.EpisodesAsync(series)).Items;
                if (generation != _generation) return;
                _episodes = episodes;
                var index = episodes.FindIndex(x => x.Id == item.Id);
                _nextItem = index >= 0 ? episodes.ElementAtOrDefault(index + 1) : null;
                _subtitle.Text = PlayerText.Subtitle(item, episodes);
            }
            catch (Exception ex) when (Errors.Expected(ex)) { }
        }
        if (generation != _generation) return;
        _next.IsEnabled = _nextItem is not null;
        ToolTip.SetTip(_next, _nextItem is null ? "Dernier épisode disponible" : "Épisode suivant · " + PlayerText.Subtitle(_nextItem, _episodes));
        Rebuild();
    }
    /// <summary>File chapters first (the source), Jellyfin's chapter list otherwise; typed server segments win.</summary>
    private void Rebuild()
    {
        var chapters = _fileChapters.Count > 0 ? _fileChapters : _serverChapters;
        _timeline.SetChapters(chapters);
        _segments = PlaybackMarkers.Merge(_serverSegments, PlaybackMarkers.FromChapters(chapters, _duration));
    }
    private void UpdateSkip()
    {
        SkipSegment? active = null;
        if (_loaded && !_stopped)
        {
            active = PlaybackMarkers.Active(_segments, _position);
            // Without a credits marker, offer the next episode during the last seconds.
            if (active is null && _nextItem is not null && _duration > 120 && _position > 0 && _duration - _position <= NextEpisodeLead)
                active = new SkipSegment(SkipKind.Outro, _duration - NextEpisodeLead, _duration);
        }
        if (active != _activeSkip)
        {
            _activeSkip = active; _countdownEnd = null;
            if (active is { } segment)
            {
                _skipToNext = _nextItem is not null && PlaybackMarkers.EndsPlayback(_segments, segment, _duration);
                _skipText.Text = PlaybackMarkers.Label(segment.Kind, _item?.Type == "Episode", _skipToNext);
                ToolTip.SetTip(_skip, _skipText.Text + " · S");
                if (_skipToNext && _shell.Settings.AutoNext && !_autoNextCancelled) _countdownEnd = Math.Max(_position, segment.Start) + CountdownLength;
            }
        }
        // Playback time drives the countdown, so pausing pauses it too.
        var counting = _countdownEnd is not null && _activeSkip is not null;
        if (counting)
        {
            var left = (int)Math.Ceiling(_countdownEnd!.Value - _position);
            if (left <= 0 && _nextItem is { } next) { _countdownEnd = null; _ = PlayNextAsync(next, completed: true); return; }
            _skipText.Text = $"Épisode suivant dans {Math.Max(1, left)} s";
        }
        _skipCancel.IsVisible = counting;
        _skip.IsVisible = _activeSkip is not null && (_controlsShown || counting || !_paused);
    }
    private void Skip()
    {
        if (_activeSkip is not { } segment || _mpv is null || !_loaded) return;
        if (_skipToNext && _nextItem is { } next) { _ = PlayNextAsync(next, completed: true); return; }
        SeekTo(segment.End); _skip.IsVisible = false;
    }
}
