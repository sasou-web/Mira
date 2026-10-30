using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
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
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// The player, over the whole window: mpv's picture, then Mira's controls, hidden after a moment without movement.
/// Reports start, progress and end to Jellyfin through the same queue as the Windows app, so progress is never lost
/// offline, and plays the next episode at the end of one.
/// </summary>
public sealed partial class PlayerPage : UserControl
{
    private readonly MainWindow _shell;
    private readonly Grid _root = new() { Background = Brushes.Black };
    private readonly Panel _videoHost = new();
    private readonly Grid _chrome = new();
    private readonly Border _top, _bottom;
    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _subtitle = new() { FontSize = 12, Foreground = Ui.TextTertiary, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _chapter = new() { FontSize = 12, Foreground = Ui.TextTertiary, HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 };
    // As on Windows: the position in white, the duration a step down, the bar between them fainter still.
    private readonly TextBlock _position = new() { FontSize = 13, Foreground = Brushes.White, FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") } };
    private readonly TextBlock _duration = new() { FontSize = 13, Foreground = Ui.Brush("#C5C5CA"), FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") } };
    private readonly Timeline _timeline = new();
    private readonly Icon _pauseIcon = new("pause", 24), _volumeIcon = new("volume", 22), _fullscreenIcon = new("fullscreen", 22);
    // The volume slider slides out of the speaker while the pointer is on it, as on Windows (VolumeRevealWidth).
    private readonly Border _volumeReveal = new() { Width = 0, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _volumeHide = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = VolumeBoost.Maximum, SmallChange = 5, LargeChange = 5, Width = 112, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _volumeText = new() { FontSize = 12, Width = 40, Foreground = Ui.TextTertiary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _next, _tracks, _more, _skip, _skipCancel;
    private readonly StackPanel _skipHost;
    private readonly TextBlock _skipText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly LoadingArc _spinner = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromMilliseconds(2600) };
    private readonly DispatcherTimer _click = new() { Interval = TimeSpan.FromMilliseconds(230) };
    private MpvPlayer? _mpv;
    private IVideoSurface? _surface;
    /// <summary>What plays, where it is and what Jellyfin was told, as on Windows.</summary>
    private readonly PlaybackSession _session = new();
    private bool _loaded, _stopped, _controlsShown = true, _initializing;
    private double _lastAudible = 80;
    private int _generation;
    private readonly SkipPlanner _skips = new();
    private readonly SleepTimer _sleep = new();
    private List<MediaItem> _episodes = [];
    // « Regarder ensemble » (WatchGroup), for this playback.
    // Arrow keys pressed in a row are one seek for the group.
    private readonly DispatcherTimer _groupSeekSoon = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _groupPlaySoon = new();
    private double _groupPlayAt;
    /// <summary>Loaded or moved for the group: Jellyfin hears « ready » once the picture is there.</summary>
    private bool _groupReadyPending;
    /// <summary>Until then, a seek that ends is one the group asked for, not one to send it.</summary>
    private DateTimeOffset _groupQuietUntil;
    private DateTimeOffset? _groupStallSince;
    private bool _groupStalled;
    private readonly Border _groupPill;
    private readonly TextBlock _groupText = new() { FontSize = 13, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>« Marc a rejoint le groupe », said by the pill until <see cref="_groupMemberUntil"/>.</summary>
    private string? _groupMember;
    private DateTimeOffset _groupMemberUntil;
    private WatchGroup Group => _shell.Group;
    /// <summary>For the self-check.</summary>
    public MpvPlayer? Engine => _mpv;
    /// <summary>« ⋮ » of the bar, for the self-check (the menu opens above it).</summary>
    public Button MoreButton => _more;
    public IVideoSurface? Surface => _surface;
    /// <summary>Why the software view replaced OpenGL, when it did (diagnostics, self-check).</summary>
    public string? VideoFallback { get; private set; }
    public bool IsFileLoaded => _loaded;
    /// <summary>Stopped, its file closed: between two episodes, or before it closes.</summary>
    public bool IsStopped => _stopped;
    public double Position => _session.Position;
    public MediaItem? Item => _session.Item;
    public MediaItem? NextItem => _skips.Next;
    /// <summary>What mpv opened: Jellyfin's address, or a file of this Mac (a copy kept, Jellyfin on this Mac).</summary>
    public string? Location => _stream?.Location;
    /// <summary>The copy kept on this Mac playing, for its subtitle files.</summary>
    private OfflineCopy? _playingCopy;
    public string Volume => _volumeText.Text ?? "";

    public PlayerPage(MainWindow shell)
    {
        _shell = shell;
        _root.Children.Add(_videoHost);
        _root.Children.Add(_spinner);
        _root.Children.Add(StatusPill());

        // Top: back to the library only; the title sits in the bottom bar, as on Windows.
        // « Retour » keeps the film going in the mini-player, as on Windows; « Fermer » there stops it.
        var back = Ui.IconButton("back", L("Retour · Échap", "Back · Esc"), () => SetMini(true), "player", 22);
        back.HorizontalAlignment = HorizontalAlignment.Left;
        _top = new Border
        {
            VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(24, 18 + shell.TitleBarInset, 24, 40), Child = back,
            Background = Fade(true)
        };

        _next = Ui.IconButton("next-episode", L("Épisode suivant · N", "Next episode · N"), () => { if (_skips.Next is { } next) _ = PlayNextAsync(next); }, "player", 22);
        BuildEpisodeButtons();
        _tracks = Ui.IconButton("subtitles", L("Audio et sous-titres", "Audio and subtitles"), () => { }, "player", 22);
        _more = Ui.IconButton("more", L("Vitesse, sous-titres et minuterie", "Speed, subtitles and sleep timer"), () => { }, "player", 22);
        _tracks.Flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedRight, ShowMode = FlyoutShowMode.Standard };
        _more.Flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedRight, ShowMode = FlyoutShowMode.Standard };
        ((Flyout)_tracks.Flyout).Opening += (_, _) => { _subtitleView = false; ((Flyout)_tracks.Flyout).Content = TracksMenu(); };
        ((Flyout)_more.Flyout).Opening += (_, _) => ((Flyout)_more.Flyout).Content = MoreMenu();
        var play = new Button { Content = _pauseIcon, Classes = { "player" } }; play.Click += (_, _) => TogglePause();
        ToolTip.SetTip(play, L("Lecture / pause · Espace", "Play / pause · Space"));
        var mute = new Button { Content = _volumeIcon, Classes = { "player" } }; mute.Click += (_, _) => ToggleMute();
        ToolTip.SetTip(mute, L("Couper le son · M", "Mute · M"));
        // 100 % sits at the middle, marked under the track; to the right, the sound is raised without clipping.
        var volumeTrack = new Grid { Width = 112, Margin = new Thickness(6, 0, 8, 0) };
        volumeTrack.Children.Add(new Border { Width = 2, Height = 10, CornerRadius = new CornerRadius(1), Background = Ui.Brush("#77777D"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        volumeTrack.Children.Add(_volume);
        ToolTip.SetTip(_volume, L("Au-delà de 100 %, le son est amplifié sans saturer", "Above 100%, the sound is boosted without clipping"));
        Avalonia.Automation.AutomationProperties.SetName(_volume, L("Volume", "Volume"));
        var revealed = Ui.Row(0, volumeTrack, _volumeText); revealed.HorizontalAlignment = HorizontalAlignment.Left;
        _volumeReveal.Child = revealed;
        _volumeReveal.Transitions = [new DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(190), Easing = new Avalonia.Animation.Easings.CubicEaseOut() }];
        var volumeGroup = Ui.Row(0, mute, _volumeReveal);
        volumeGroup.Background = Brushes.Transparent; volumeGroup.Margin = new Thickness(6, 0, 0, 0);
        volumeGroup.PointerEntered += (_, _) => { _volumeHide.Stop(); RevealVolume(true); };
        volumeGroup.PointerExited += (_, _) => HideVolumeSoon();
        _volumeHide.Tick += (_, _) => { _volumeHide.Stop(); if (!volumeGroup.IsPointerOver) RevealVolume(false); };
        volumeGroup.PointerWheelChanged += (_, e) => { ChangeVolume(e.Delta.Y > 0 ? 5 : -5); e.Handled = true; };
        _volume.ValueChanged += (_, e) => VolumeChanged(e.NewValue);
        var fullscreen = new Button { Content = _fullscreenIcon, Classes = { "player" } }; fullscreen.Click += (_, _) => ToggleFullscreen();
        ToolTip.SetTip(fullscreen, L("Plein écran · F", "Full screen · F"));
        // Their icons say what a click does: VoiceOver reads the same, with the shortcut as help.
        Speak(play, _pauseIcon, kind => kind == "play" ? L("Lecture", "Play") : L("Pause", "Pause"), L("Espace", "Space"));
        Speak(mute, _volumeIcon, kind => kind == "mute" ? L("Rétablir le son", "Unmute") : L("Couper le son", "Mute"), "M");
        Speak(fullscreen, _fullscreenIcon, kind => kind == "collapse" ? L("Quitter le plein écran", "Exit full screen") : L("Plein écran", "Full screen"), "F");

        // Bottom bar, as on Windows: title and episode on the left, chapter and time on the right, the timeline, then
        // play, next episode and volume on the left, the menus and fullscreen on the right.
        var left = Ui.Row(6, play, _previous, _next, volumeGroup);
        var miniButton = Ui.IconButton("mini", L("Mini-lecteur · I", "Mini player · I"), () => SetMini(true), "player", 22);
        var right = Ui.Row(6, _episodesButton, _more, _tracks, miniButton, fullscreen);
        var transport = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(-8, 2, -8, 0), Height = 44 };
        left.VerticalAlignment = right.VerticalAlignment = VerticalAlignment.Center;
        transport.Children.Add(left); Grid.SetColumn(right, 2); transport.Children.Add(right);
        var info = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 5) };
        var heading = Ui.Column(0, _title, _subtitle); heading.VerticalAlignment = VerticalAlignment.Bottom; heading.Margin = new Thickness(0, 0, 32, 0);
        info.Children.Add(heading);
        var time = Ui.Row(0, _position, new TextBlock { Text = " / ", FontSize = 13, Foreground = Ui.Faint }, _duration);
        time.HorizontalAlignment = HorizontalAlignment.Right; time.Margin = new Thickness(0, 2, 0, 0);
        var timeColumn = Ui.Column(0, _chapter, time); timeColumn.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(timeColumn, 1); info.Children.Add(timeColumn);
        _timeline.Seek += seconds => SeekTo(seconds);
        var body = Ui.Column(0, info, _timeline, transport);
        _bottom = new Border { VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(24, 110, 24, 14), Child = body, Background = Fade(false) };
        _chrome.Children.Add(_top); _chrome.Children.Add(_bottom);
        foreach (var part in new Control[] { _top, _bottom }) part.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) }];
        _root.Children.Add(_chrome);

        _skip = new Button { Classes = { "pill" }, Content = Ui.Row(10, _skipText, new Icon("skip", 16) { VerticalAlignment = VerticalAlignment.Center }), IsVisible = false };
        _skip.Click += (_, _) => Skip();
        _skipText.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) Avalonia.Automation.AutomationProperties.SetName(_skip, _skipText.Text); };
        _skipCancel = new Button { Classes = { "pill" }, Content = L("Annuler", "Cancel"), IsVisible = false, Margin = new Thickness(0, 0, 10, 0) };
        _skipCancel.Click += (_, _) => { _skipText.Text = _skips.Cancel(); _skipCancel.IsVisible = false; };
        ToolTip.SetTip(_skipCancel, L("Rester sur cet épisode", "Stay on this episode"));
        _skipHost = Ui.Row(0, _skipCancel, _skip);
        _skipHost.HorizontalAlignment = HorizontalAlignment.Right; _skipHost.VerticalAlignment = VerticalAlignment.Bottom; _skipHost.Margin = new Thickness(0, 0, 36, 150);
        _root.Children.Add(_skipHost);
        // « Regarder ensemble »: what the group is doing, top center, under the title (ShowGroup).
        _groupPill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(120, 76 + shell.TitleBarInset, 120, 0),
            Padding = new Thickness(13, 8, 16, 8), CornerRadius = new CornerRadius(18), Background = Ui.Brush("#E0151517"), BorderBrush = Ui.Brush("#30FFFFFF"), BorderThickness = new Thickness(1),
            IsHitTestVisible = false, IsVisible = false, Opacity = 0, Child = Ui.Row(9, new Icon("users", 16) { VerticalAlignment = VerticalAlignment.Center }, _groupText),
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) }]
        };
        AutomationProperties.SetLiveSetting(_groupText, AutomationLiveSetting.Polite);
        _root.Children.Add(_groupPill);
        Content = BuildMini(_root);

        _tick.Tick += (_, _) => Tick();
        _hide.Tick += (_, _) => { _hide.Stop(); SetControls(false); };
        _click.Tick += (_, _) => { _click.Stop(); TogglePause(); };
        _groupSeekSoon.Tick += (_, _) => { _groupSeekSoon.Stop(); if (Group.PlaysFor(this)) Group.Send("Seek", new JsonObject { ["PositionTicks"] = WatchGroup.Ticks(_session.Position) }); };
        _groupPlaySoon.Tick += (_, _) => { _groupPlaySoon.Stop(); if (Group.PlaysFor(this)) { MoveForGroup(_groupPlayAt); QuietPause(false); } };
        _videoHost.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(_videoHost).Properties.IsLeftButtonPressed) return;
            _click.Stop();
            // The click that brings Mira to the front, or one in the band of a bar while the controls are hidden (the
            // shown bars take their clicks): the controls show, the film never pauses.
            if (Environment.TickCount64 - _shell.ActivatedAt < 400 || !_mini && InBarBand(e.GetPosition(_chrome))) { ShowControls(); return; }
            if (_mini) { MiniPressed(e); return; }
            // A double click toggles fullscreen; a single one, after a short wait, pauses or resumes.
            if (e.ClickCount >= 2) ToggleFullscreen(); else _click.Start();
        };
        PointerMoved += (_, _) => ShowControls();
        _videoHost.PointerMoved += (_, e) => { if (_mini) MiniMoved(e); };
        _videoHost.PointerReleased += (_, e) => { if (_mini) MiniReleased(e); };
        PointerExited += (_, _) => { if (_mini) ShowMiniControls(false); };
        _initializing = true; _volume.Value = VolumeBoost.Clamp(shell.Settings.Volume); _initializing = false;
        if (_volume.Value > 0) _lastAudible = _volume.Value;
        ShowVolume();
    }
    /// <summary>The places of the top and bottom bars, their shades included, shown or not.</summary>
    public bool InBarBand(Point point) => _top.Bounds.Contains(point) || _bottom.Bounds.Contains(point);
    private static LinearGradientBrush Fade(bool top) => new()
    {
        StartPoint = new RelativePoint(0, top ? 0 : 1, RelativeUnit.Relative), EndPoint = new RelativePoint(0, top ? 1 : 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(top ? "#B0000000" : "#EB000000"), 0), new GradientStop(Color.Parse(top ? "#00000000" : "#8C000000"), top ? 1 : 0.5), new GradientStop(Color.Parse("#00000000"), 1) }
    };

    // —— Start and stop ——
    /// <summary>
    /// Opens <paramref name="item"/> (a series: its next episode); false when it could not start. With
    /// <paramref name="groupStart"/>, for the group: there, paused until Jellyfin says when to play.
    /// </summary>
    public async Task<bool> StartAsync(MediaItem item, bool fromStart = false, long? groupStart = null)
    {
        if (_shell.Session is not { } session) return false;
        var generation = ++_generation;
        _stopped = false; _loaded = false; _session.Reported = false; _session.Failure = null; ResetMarkers();
        _spinner.IsVisible = true; ShowControls();
        try
        {
            if (item.Type == "Series") item = await EpisodeOfAsync(session, item);
            var start = groupStart is { } ticks ? ticks / 1e7 : PlaybackSession.StartAt(item, remember: _shell.Settings.RememberPosition && !fromStart, back: _shell.Settings.ResumeBack);
            // Jellyfin on this same Mac: its file directly; otherwise the file as it is, or Jellyfin's conversion under the
            // quality chosen (PlayerPage.Stream.cs).
            // Kept on this Mac (« Garder sur ce Mac »): its file, with or without Jellyfin; the progress goes as usual.
            var kept = _shell.KeptCopy(item.Id);
            var stream = kept is not null ? new ServerStream(kept.Source ?? new MediaSource(), Guid.NewGuid().ToString("N"), kept.VideoPath, null, false, null, null, 0, [])
                : await ServerStreams.OpenAsync(session.Client, _shell.Speed, _shell.Settings.Quality, item, new StreamWish(start, Memory: await SeriesMemoryAsync(session, item)), _shell.Settings.AudioLanguage, FileHere);
            if (generation != _generation) return false;
            UseStream(stream); _playingCopy = kept;
            _session.Begin(item, stream.Source, stream.PlaySessionId, start, stream.PlayMethod);
            _title.Text = item.DisplayTitle; _subtitle.Text = PlayerText.Subtitle(item);
            _next.IsEnabled = false; ShowEpisodeButtons(item);

            await OpenEngineAsync();
            if (_mpv is null || generation != _generation) return false;
            // In a group: loaded paused, « ready » once there (GroupRestarted), played when Jellyfin says.
            _groupReadyPending = groupStart is not null; _groupStallSince = null; _groupStalled = false;
            if (groupStart is not null) { Group.SetAway(false); Group.Report("Buffering", _session.Position, false); }
            _fileOpen = true;
            _mpv.Load(stream.Location, _session.Position, stream.Authorization, paused: groupStart is not null);
            _session.LastReport = DateTimeOffset.Now;
            _tick.Start();
            _ = LoadMarkersAsync(session, item, generation);
            _ = LoadSubtitleSearchAsync(session, generation);
            return true;
        }
        catch (Exception ex) when (Errors.Expected(ex) || ex is InvalidOperationException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            await StopAsync();
            if (ex is IOException or FileNotFoundException) _shell.Notice(ex.Message);
            else if (ex is InvalidOperationException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
                _shell.Notice(L("La lecture n’a pas pu démarrer : le moteur mpv de Mira.app ne s’est pas chargé.", "Playback couldn’t start: Mira.app’s mpv engine didn’t load."));
            // Jellyfin not reached: through « Tailscale intégré », why, with what helps.
            else _shell.Say(ex);
            return false;
        }
    }
    /// <summary>The episode Lecture opens on a series: Jellyfin's next one, else one in progress, else the first unwatched.</summary>
    public static async Task<MediaItem> EpisodeOfAsync(Session session, MediaItem series)
    {
        var next = await session.NextEpisodeAsync(series.Id);
        if (next is null)
        {
            var episodes = (await session.EpisodesAsync(series.Id)).Items;
            await Task.Run(() => session.Store.ApplyLocalProgress(episodes));
            next = episodes.FirstOrDefault(x => x.Progress > 0 && !x.UserData.Played) ?? episodes.FirstOrDefault(x => !x.UserData.Played) ?? episodes.FirstOrDefault();
        }
        return next ?? throw new IOException(L("Aucun épisode disponible.", "No episode available."));
    }
    /// <summary>mpv and the view it draws into: OpenGL, else (no OpenGL, or MIRA_SOFTWARE_VIDEO set) in software.</summary>
    private async Task OpenEngineAsync()
    {
        await ReleaseEngineAsync();
        var player = new MpvPlayer(_shell.Settings, picture: PictureForPlayback());
        _mpv = player;
        player.FileLoaded += FileLoaded;
        player.PlaybackRestarted += () => { _session.LastReport = DateTimeOffset.MinValue; GroupRestarted(); };
        var generation = _generation;
        player.Ended += eof =>
        {
            // The stream replaced by its reopening (another quality or track, the connection back): not an end.
            if (_skipEnds > 0) { _skipEnds--; return; }
            _fileOpen = false;
            // The stream opened again could not load: an end, never that of the title.
            var ended = eof && !_reloading;
            Dispatcher.UIThread.Post(() => _ = EndedAsync(ended, generation));
        };
        player.Error += message => _session.Failure = message;
        IVideoSurface surface = Environment.GetEnvironmentVariable("MIRA_SOFTWARE_VIDEO") is { Length: > 0 } ? new SoftwareVideoView() : new VideoView();
        if (!await AttachAsync(surface, player) && surface is VideoView gl)
        {
            VideoFallback = gl.Problem ?? (gl.Initialized ? L("contexte de rendu mpv non créé", "mpv render context not created") : L("OpenGL indisponible dans cette fenêtre", "OpenGL unavailable in this window"));
            // This window has no OpenGL: the software view always works.
            await DetachAsync(surface);
            surface = new SoftwareVideoView();
            if (!await AttachAsync(surface, player)) throw new InvalidOperationException(L("Aucun rendu vidéo disponible.", "No video rendering available."));
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
        _stopped = true; ++_generation; ForgetStream();
        _tick.Stop(); _hide.Stop(); _click.Stop(); _groupSeekSoon.Stop(); _groupPlaySoon.Stop(); Cursor = Cursor.Default;
        SleepGuard.Set(false);
        var session = _shell.Session;
        if (_loaded && _mpv is not null && !completed) _session.Position = Reported(_mpv.Number("time-pos", _session.Position));
        completed = _session.Completes(_loaded, completed);
        if (_session.Reported && session is not null) { _session.Reported = false; await session.Sync.RecordAsync(completed ? "complete" : "stop", Report()); }
        if (_loaded && _session.Item is { } item && session is not null)
        {
            item.UserData.Played = item.UserData.Played || completed;
            item.UserData.PlaybackPositionTicks = completed ? 0 : TimeSpan.FromSeconds(Math.Max(0, _session.Position)).Ticks;
            item.UserData.LastPlayedDate = DateTimeOffset.UtcNow;
            var snapshot = item with { UserData = item.UserData with { } };
            // The engine is released below whatever the cache says: a full disk only loses this row for the next start.
            await Task.Run(() => Errors.TryCache(() => session.Store.RememberPlayback(snapshot)));
        }
        if (_mpv is not null) _shell.Settings.Volume = _mpv.Level;
        _loaded = false;
        await ReleaseEngineAsync();
    }
    private async Task EndedAsync(bool eof, int generation)
    {
        if (generation != _generation || _stopped) return;
        // A stream from Jellyfin that stopped before the end of its title: the connection, as on Windows and Mira web.
        if (StreamCut()) { _ = RecoverStreamAsync(generation); return; }
        var error = _session.Failure; var next = _skips.Next; var stay = _skips.Stay;
        if (eof && _session.Duration > 0) _session.Position = _session.Duration;
        // « Arrêter à la fin de l’épisode »: this one was the last.
        var last = eof && _sleep.AtEnd;
        if (last) _sleep.Clear();
        if (eof && _shell.Settings.AutoNext && !last && !stay && _session.Item?.Type == "Episode" && (next ?? await NextAfterAsync(_session.Item)) is { } following)
        { await PlayNextAsync(following, completed: true); return; }
        await StopAsync(eof);
        await _shell.CloseOverlayPlayerAsync(this);
        if (error is not null) _shell.Notice(error);
    }
    private async Task<MediaItem?> NextAfterAsync(MediaItem current)
    {
        if (_shell.Session is not { } session || current.SeriesId is null) return null;
        try { var episodes = (await session.EpisodesAsync(current.SeriesId)).Items; var index = episodes.FindIndex(x => x.Id == current.Id); return index >= 0 ? episodes.ElementAtOrDefault(index + 1) : null; }
        catch (Exception ex) when (Errors.Expected(ex)) { return OfflineCopies.NextEpisode(_shell.OfflineCopiesKept, current)?.Item; }
    }
    /// <summary>The next episode in this same player: the window stays as it is (fullscreen included).</summary>
    private async Task PlayNextAsync(MediaItem next, bool completed = false)
    {
        var forGroup = Group.PlaysFor(this);
        await StopAsync(completed || _skips.ToNext && _skips.Active is not null);
        if (forGroup)
        {
            // Jellyfin puts it on for the whole group, this Mac included (PlayForGroupAsync); the player waits for it.
            if (await Group.AskAsync(next, fromStart: false)) { WaitForGroupTitle(); return; }
            if (!await StartAsync(next, groupStart: Group.Queue?.StartTicks ?? 0)) await _shell.CloseOverlayPlayerAsync(this);
            return;
        }
        if (!await StartAsync(next)) await _shell.CloseOverlayPlayerAsync(this);
    }
    /// <summary>The group's next title, in this same player.</summary>
    public async Task PlayForGroupAsync(MediaItem item, long start)
    {
        await StopAsync();
        if (!await StartAsync(item, groupStart: start)) await _shell.CloseOverlayPlayerAsync(this);
    }
    /// <summary>Between two titles of the group: the player closes if Jellyfin never sends the next one.</summary>
    private void WaitForGroupTitle()
    {
        _spinner.IsVisible = true; ShowControls();
        var generation = _generation;
        DispatcherTimer.RunOnce(() => { if (_stopped && generation == _generation && ReferenceEquals(_shell.Player, this)) _ = _shell.CloseOverlayPlayerAsync(this); }, TimeSpan.FromSeconds(8));
    }

    private async void FileLoaded()
    {
        if (_mpv is not { } mpv || _session.Item is null || _shell.Session is not { } session) return;
        var generation = _generation;
        var reopened = _reloading; _reloading = false; _recoveryLoad = false; _starvedSince = null; _buffering = false; ShowStatus(null);
        _loaded = true; _spinner.IsVisible = false;
        SayAudioMissing();
        _session.Paused = mpv.Flag("pause"); _session.Duration = mpv.Number("duration", _session.Duration); _session.Position = mpv.Number("time-pos", _session.Position);
        _skips.FileChapters = mpv.Chapters(); Rebuild();
        ShowControls();
        // Opened again (another quality, the connection back): the same playback goes on for Jellyfin, told at the next
        // tick under its new session.
        _session.LastReport = reopened ? DateTimeOffset.MinValue : DateTimeOffset.Now;
        if (!reopened) { _session.Reported = true; await session.Sync.RecordAsync("start", Report()); }
        if (generation != _generation || _mpv is null || _stream is not { } stream) return;
        // Subtitles fetched from Jellyfin take the token in their header too (its file on this Mac opens without it), and
        // through Built-in Tailscale its proxy's credentials (MpvClient.SetAuthorization keeps them).
        _mpv.SetAuthorization(session.Client.AuthorizationHeader);
        // External subtitles supplied by Jellyfin; a conversion's come when chosen.
        // The subtitle files kept beside a copy.
        if (_playingCopy is { } copy && copy.Item.Id == _session.Item.Id)
            foreach (var subtitle in copy.Subtitles) _mpv.TryCommand("sub-add", Path.Combine(copy.Folder, subtitle.File), "auto", subtitle.Title ?? "", subtitle.Language ?? "");
        else if (!stream.Converted)
            foreach (var (track, path) in ServerStreams.Beside(stream.Source, converted: false))
                _mpv.TryCommand("sub-add", session.Client.ServerUri(path).AbsoluteUri, "auto", track.DisplayTitle ?? "", track.Language ?? "");
        await ChooseStartTracksAsync(session, reopened, generation);
    }
    private PlaybackReport Report() => _session.Report(_mpv?.Level ?? _volume.Value, _mpv?.Flag("mute") ?? false);

    // —— Every 100 ms ——
    private bool _tickBusy;
    private async void Tick()
    {
        if (_tickBusy || _mpv is not { } mpv) return;
        _tickBusy = true;
        try
        {
            mpv.Poll(); ShowGroup();
            if (_mpv is null || !_loaded || _stopped) return;
            ShowBuffering(mpv); KeepTracks(mpv);
            _session.Position = Reported(mpv.Number("time-pos", _session.Position)); _session.Duration = mpv.Number("duration", _session.Duration);
            _recovery.Played(_session.Position);
            var paused = mpv.Flag("pause");
            SleepGuard.Set(!paused);
            if (paused != _session.Paused) { _session.Paused = paused; _pauseIcon.Kind = paused ? "play" : "pause"; if (paused) ShowControls(); _session.LastReport = DateTimeOffset.MinValue; GroupPauseChanged(paused); }
            WatchGroupStall(mpv); WatchPicture(mpv, paused);
            _timeline.SetPlayback(_session.Position, _session.Duration, mpv.Number("demuxer-cache-time", 0));
            ShowTime(_session.Position); MiniProgress();
            _chapter.Text = _skips.Chapters.LastOrDefault(c => c.Start <= _session.Position + .25).Title ?? "";
            _chapter.IsVisible = _chapter.Text.Length > 0;
            UpdateSkip();
            // « Minuterie »: the minutes are over, the film pauses where it is.
            if (_sleep.Due(DateTimeOffset.Now)) { if (!paused) { mpv.Set("pause", "yes"); _shell.Notice(L("Minuterie : lecture en pause.", "Sleep timer: playback paused.")); } RefreshMenus(); }
            if (_session.Item is not null) _session.Item.UserData.PlaybackPositionTicks = TimeSpan.FromSeconds(Math.Max(0, _session.Position)).Ticks;
            // Every 10 s while it plays, once when it pauses or resumes (MinValue above), never while it stays paused.
            if (_session.Reported && _shell.Session is { } session && Cadence.ProgressDue(false, paused, _session.LastReport, DateTimeOffset.Now))
            { _session.LastReport = DateTimeOffset.Now; await session.Sync.RecordAsync("progress", Report()); }
        }
        catch (Exception ex) when (Errors.Expected(ex)) { }
        finally { _tickBusy = false; }
    }
    /// <summary>mpv may report the pre-seek time for a poll or two; the requested time holds meanwhile.</summary>
    private double Reported(double reported) => _session.Settled(reported, DateTimeOffset.UtcNow);

    // —— Controls ——
    /// <summary>To a keyframe, quick enough to follow the pointer; exact past a passage, else its last seconds play.</summary>
    private void SeekTo(double seconds, bool exact = false)
    {
        if (_mpv is null || !_loaded) return;
        var target = Math.Clamp(seconds, 0, _session.Duration > 0 ? Math.Max(0, _session.Duration - .5) : double.MaxValue);
        if (!_mpv.TryCommand("seek", target.ToString(CultureInfo.InvariantCulture), exact ? "absolute+exact" : "absolute+keyframes")) return;
        Hold(target);
    }
    private void SeekBy(double seconds)
    {
        if (_mpv is null || !_loaded) return;
        // From the last requested position: repeated presses add up, and the bar moves at once.
        var target = Math.Clamp(_session.Position + seconds, 0, _session.Duration > 0 ? Math.Max(0, _session.Duration - .5) : double.MaxValue);
        if (!_mpv.TryCommand("seek", target.ToString(CultureInfo.InvariantCulture), "absolute+exact")) return;
        Hold(target); ShowControls();
    }
    private void Hold(double target)
    {
        _session.Hold(target, DateTimeOffset.UtcNow);
        _timeline.Hold(target); ShowTime(target);
    }
    private void TogglePause()
    {
        if (_mpv is null) return;
        var pause = !_mpv.Flag("pause");
        // In a group, play waits for Jellyfin, which starts every device at the same moment.
        if (Group.PlaysFor(this) && _loaded) { if (pause) QuietPause(true); Group.Send(pause ? "Pause" : "Unpause"); ShowControls(); return; }
        _mpv.Set("pause", pause ? "yes" : "no");
        _session.Paused = pause; _pauseIcon.Kind = pause ? "play" : "pause";
        _session.LastReport = DateTimeOffset.MinValue; ShowControls();
    }
    private void VolumeChanged(double value)
    {
        ShowVolume();
        if (_initializing) return;
        _shell.Settings.Volume = value; _mpv?.SetLevel(value);
        if (value > 0) { _lastAudible = value; _mpv?.Set("mute", "no"); }
        ShowVolume();
    }
    private void ChangeVolume(double delta) { _volume.Value = Math.Clamp(_volume.Value + delta, 0, VolumeBoost.Maximum); ShowControls(); RevealVolume(true); HideVolumeSoon(); }
    private void RevealVolume(bool shown) => _volumeReveal.Width = shown ? 174 : 0;
    private void HideVolumeSoon() { _volumeHide.Stop(); _volumeHide.Start(); }
    private void ShowTime(double position) { _position.Text = Timeline.Clock(position); _duration.Text = Timeline.Clock(_session.Duration); }
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
        _volumeText.Foreground = boosted ? Brushes.White : Ui.TextTertiary;
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
        _hide.Interval = TimeSpan.FromMilliseconds(_session.Paused ? 4000 : 2600);
        _hide.Start();
    }
    private void SetControls(bool shown)
    {
        // The mini-player: its own controls while the pointer is on it.
        if (_mini) { ShowMiniControls(shown && IsPointerOver || _session.Paused); _controlsShown = shown; return; }
        // The controls stay while a menu is open or the bar is being dragged.
        if (!shown && (_timeline.Dragging || _tracks.Flyout?.IsOpen == true || _more.Flyout?.IsOpen == true || _episodesButton.Flyout?.IsOpen == true || _bottom.IsPointerOver)) { _hide.Start(); return; }
        _controlsShown = shown;
        _top.Opacity = _bottom.Opacity = shown ? 1 : 0;
        _top.IsHitTestVisible = _bottom.IsHitTestVisible = shown;
        Cursor = shown ? Cursor.Default : new Cursor(StandardCursorType.None);
        UpdateSkip(); ShowGroup();
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
            case Key.S: if (_skips.Active is not null) Skip(); break;
            case Key.N: if (_skips.Next is { } next) _ = PlayNextAsync(next); break;
            case Key.P: if (_previous.IsVisible && _previous.IsEnabled) PlayPrevious(); break;
            case Key.E: ToggleEpisodes(); break;
            case Key.I: SetMini(!_mini); break;
            // In the mini-player, Échap and ⌘[ are the library's.
            case Key.Escape when !_mini:
                if (_shell.WindowState == WindowState.FullScreen) ToggleFullscreen(); else SetMini(true);
                break;
            case Key.OemOpenBrackets when command && !_mini: SetMini(true); break;
            default: return;
        }
        e.Handled = true;
    }

    // —— Menus ——
    private Control TracksMenu()
    {
        if (_subtitleView) return SubtitleSearchMenu();
        if (_converted is { } converted) return ConvertedTracksMenu(converted);
        var tracks = _mpv?.Tracks() ?? [];
        var current = (Audio: _mpv?.Get("aid"), Sub: _mpv?.Get("sid"));
        Control Column(string title, string type, string? selected, bool off)
        {
            var list = new StackPanel { Spacing = 2, MinWidth = 240 };
            list.Children.Add(new TextBlock { Text = title, Classes = { "label" }, Margin = new Thickness(10, 0, 0, 8) });
            if (off) list.Children.Add(Entry(L("Désactivés", "Off"), null, selected is "no" or null, () => ChooseTrack("sid", "no")));
            foreach (var track in tracks.Where(x => x.Type == type))
                list.Children.Add(Entry(track.Label, track.Details, selected == track.Id, () => ChooseTrack(type == "audio" ? "aid" : "sid", track.Id)));
            if (type == "sub") { list.Children.Add(Entry(L("Charger un fichier…", "Load a file…"), null, false, () => _ = LoadSubtitleAsync())); AddSubtitleSearch(list); }
            else AddAudioDevices(list);
            return list;
        }
        return Ui.Row(18, Column(L("AUDIO", "AUDIO"), "audio", current.Audio, false), Column(L("SOUS-TITRES", "SUBTITLES"), "sub", current.Sub, true));
    }
    /// <summary>A track chosen in the menu, kept for the next episodes of this series (<see cref="SeriesTracks"/>).</summary>
    private void ChooseTrack(string property, string id)
    {
        if (_mpv is null) return;
        _mpv.Set(property, id);
        RememberTracks();
    }
    /// <summary>
    /// « ⋮ », laid out as Mira for Windows': one column of 300 px, speed, quality and sleep timer as rows of chips, the
    /// group, then the subtitle delay on a line of its own.
    /// </summary>
    private Control MoreMenu()
    {
        var speed = _mpv?.Number("speed", 1) ?? 1; var delay = _mpv?.Number("sub-delay", 0) ?? 0;
        var menu = new StackPanel { Width = 300 };
        menu.Children.Add(MenuHeading(L("VITESSE", "SPEED")));
        menu.Children.Add(Chips(new[] { 0.75, 1, 1.25, 1.5, 2 }.Select(value => (PlayerText.Speed(value), L("Vitesse ", "Speed ") + PlayerText.Speed(value), Math.Abs(speed - value) < .01,
            (Action)(() => _mpv?.Set("speed", value.ToString(CultureInfo.InvariantCulture))))), 0));
        // A copy kept on this Mac plays its own file: no quality to choose.
        if (_stream is { } stream && _playingCopy is null) { menu.Children.Add(MenuHeading(L("QUALITÉ", "QUALITY"))); menu.Children.AddRange(QualityChoices(stream)); }
        menu.Children.Add(MenuHeading(L("MINUTERIE", "SLEEP TIMER")));
        void Sleep(int? minutes)
        {
            _sleep.Choose(minutes, DateTimeOffset.Now);
            // A countdown to the next episode already running stops, as « Annuler » does.
            if (_sleep.AtEnd && _skips.Counting) { _skipText.Text = _skips.Cancel(); _skipCancel.IsVisible = false; }
        }
        var sleeps = new List<(string, string, bool, Action)> { (L("Non", "Off"), L("Pas de minuterie", "No sleep timer"), _sleep.Chosen is null, () => Sleep(null)) };
        sleeps.AddRange(SleepTimer.Choices.Select(m => (SleepTimer.Duration(m), L("Pause dans ", "Pause in ") + SleepTimer.Duration(m), _sleep.Chosen == m, (Action)(() => Sleep(m)))));
        if (_session.Item?.Type == "Episode") sleeps.Add((L("Fin", "End"), L("Arrêter à la fin de l’épisode", "Stop at the end of the episode"), _sleep.AtEnd, () => Sleep(0)));
        menu.Children.Add(Chips(sleeps, 0));
        if (_sleep.Status(DateTimeOffset.Now) is { } status) menu.Children.Add(MenuNote(status));
        if (Group.Active && Group.Group is { } group)
        {
            menu.Children.Add(MenuHeading(L("REGARDER ENSEMBLE", "WATCH TOGETHER")));
            var people = new TextBlock { Text = Together.GroupText(group), FontSize = 13, Foreground = Ui.Brush("#DEDEE4"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (group.Participants.Count > 0) ToolTip.SetTip(people, string.Join(", ", group.Participants));
            var leave = Chip(L("Quitter", "Leave"), L("Quitter le groupe", "Leave the group"), false, () => { (_more.Flyout as Flyout)?.Hide(); _ = Group.LeaveAsync(); });
            leave.MinWidth = 70; leave.Margin = new Thickness(0);
            var line = new DockPanel { Margin = new Thickness(10, 0, 6, 8) };
            DockPanel.SetDock(leave, Dock.Right); line.Children.Add(leave); line.Children.Add(people);
            menu.Children.Add(line);
        }
        if (PlaybackInfo() is { } info) { menu.Children.Add(MenuHeading(L("INFORMATIONS", "INFORMATION"))); menu.Children.Add(info); }
        menu.Children.Add(new Border { Height = 1, Background = Ui.Brush("#24FFFFFF"), Margin = new Thickness(10, 2, 10, 6) });
        Button? value = null;
        value = Chip(PlayerText.Delay(delay), L("Remettre à zéro", "Reset"), false, () => Shift(0));
        value.MinWidth = 58;
        void Shift(double change)
        {
            if (_mpv is null) return;
            var shifted = change == 0 ? 0 : Math.Round(_mpv.Number("sub-delay", 0) + change, 1);
            _mpv.Set("sub-delay", shifted.ToString(CultureInfo.InvariantCulture)); value!.Content = PlayerText.Delay(shifted);
        }
        Button Step(string sign, string tip, double change) { var step = Chip(sign, tip, false, () => Shift(change)); step.Width = 32; step.Margin = new Thickness(0); return step; }
        var steps = Ui.Row(0, Step("−", L("Sous-titres 0,5 s plus tôt", "Subtitles 0.5 s earlier"), -0.5), value, Step("+", L("Sous-titres 0,5 s plus tard", "Subtitles 0.5 s later"), 0.5));
        var delayLine = new DockPanel { Margin = new Thickness(10, 0, 6, 4) };
        DockPanel.SetDock(steps, Dock.Right); delayLine.Children.Add(steps);
        delayLine.Children.Add(new TextBlock { Text = L("Décalage des sous-titres", "Subtitle delay"), FontSize = 13, Foreground = Ui.Brush("#DEDEE4"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        menu.Children.Add(delayLine);
        return menu;
    }
    /// <summary>A section's name in a menu, small capitals as on Windows (« VITESSE »).</summary>
    private static TextBlock MenuHeading(string text) => new() { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Ui.Faint, Margin = new Thickness(10, 6, 10, 8) };
    private static TextBlock MenuNote(string text) => new() { Text = text, FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 2, 10, 8) };
    /// <summary>A row of chips of the same width, the chosen one white; <paramref name="columns"/> 0 = one row.</summary>
    private Control Chips(IEnumerable<(string Label, string Name, bool Chosen, Action Choose)> choices, int columns)
    {
        var list = choices.ToList();
        var grid = new UniformGrid { Margin = new Thickness(8, 0, 8, 10) };
        if (columns > 0) grid.Columns = columns; else grid.Rows = 1;
        foreach (var (label, name, chosen, choose) in list) grid.Children.Add(Chip(label, name, chosen, choose));
        return grid;
    }
    private Button Chip(string label, string name, bool chosen, Action choose)
    {
        var chip = new Button { Content = label, Classes = { "menuchip" } };
        chip.Classes.Set("on", chosen);
        ToolTip.SetTip(chip, name);
        Avalonia.Automation.AutomationProperties.SetName(chip, chosen ? name + L(", choisi", ", selected") : name);
        // The menu stays open: its marks follow the choice.
        chip.Click += (_, _) => { choose(); RefreshMenus(); };
        return chip;
    }
    /// <summary>The name of a button whose icon changes (« Lecture » or « Pause »), kept with the icon.</summary>
    private static void Speak(Button button, Icon icon, Func<string, string> name, string shortcut)
    {
        Avalonia.Automation.AutomationProperties.SetName(button, name(icon.Kind));
        Avalonia.Automation.AutomationProperties.SetHelpText(button, shortcut);
        icon.PropertyChanged += (_, e) => { if (e.Property == Icon.KindProperty) Avalonia.Automation.AutomationProperties.SetName(button, name(icon.Kind)); };
    }
    private Button Entry(string label, string? details, bool selected, Action choose)
    {
        var text = Ui.Column(1, new TextBlock { Text = label, FontSize = 14, FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal });
        if (!string.IsNullOrEmpty(details)) text.Children.Add(new TextBlock { Text = details, FontSize = 12, Foreground = Ui.Muted });
        var row = new DockPanel();
        var check = new Icon("check", 16) { Opacity = selected ? 1 : 0, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(check, Dock.Left); row.Children.Add(check); row.Children.Add(text);
        var button = new Button { Content = row, Classes = { "quiet" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 8), FontWeight = FontWeight.Normal };
        Avalonia.Automation.AutomationProperties.SetName(button, string.Join(", ", new[] { label, details, selected ? L("choisi", "selected") : null }.Where(x => !string.IsNullOrEmpty(x))));
        // The menu stays open, so the other track can be chosen too; it refreshes its marks.
        button.Click += (_, _) => { choose(); RefreshMenus(); };
        return button;
    }
    public void RefreshMenus()
    {
        if (_tracks.Flyout is Flyout { IsOpen: true } tracks) tracks.Content = TracksMenu();
        if (_more.Flyout is Flyout { IsOpen: true } more) more.Content = MoreMenu();
    }
    private async Task LoadSubtitleAsync()
    {
        (_tracks.Flyout as Flyout)?.Hide();
        var files = await _shell.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L("Choisir un fichier de sous-titres", "Choose a subtitle file"), AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(L("Sous-titres", "Subtitles")) { Patterns = ["*.srt", "*.ass", "*.ssa", "*.vtt", "*.sub"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        if (_mpv?.TryCommand("sub-add", path, "select") != true) { _shell.Notice(L("Ce fichier de sous-titres n’a pas pu être chargé.", "This subtitle file couldn’t be loaded.")); return; }
        await OfferToKeepSubtitleAsync(path);
    }
    /// <summary>
    /// The file plays here only: Mira offers to keep it on Jellyfin (« Sous-titres gardés sur Jellyfin », as Mira for
    /// Windows), for an account allowed to, its language read from its name (<see cref="JellyfinSubtitles.Guess"/>).
    /// </summary>
    private async Task OfferToKeepSubtitleAsync(string file)
    {
        if (_shell.Session is not { } session || _session.Item is not { } item) return;
        if (JellyfinSubtitles.Guess(Path.GetFileName(file), _shell.Settings.SubtitleLanguage) is not { } guess) return;
        var generation = _generation;
        bool? allowed;
        try { allowed = await session.Client.CanManageSubtitlesAsync(); }
        catch (Exception ex) when (Errors.Expected(ex)) { return; }
        if (allowed == false || generation != _generation) return;
        _shell.Notice(JellyfinSubtitles.LoadedText, L("Garder sur Jellyfin", "Keep on Jellyfin"), () => _ = KeepLoadedSubtitleAsync(session.Client, item.Id, file, guess));
    }
    private async Task KeepLoadedSubtitleAsync(JellyfinClient client, string itemId, string file, (string Format, string Language, bool Forced, bool HearingImpaired) guess)
    {
        SubtitleUpload? upload;
        try { upload = await JellyfinSubtitles.ReadAsync(file, guess); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _shell.Notice(JellyfinSubtitles.UnreadableText); return; }
        if (upload is null) { _shell.Notice(JellyfinSubtitles.TooBigText); return; }
        _shell.Notice(L("Envoi des sous-titres à Jellyfin…", "Sending the subtitles to Jellyfin…"));
        try { _shell.Notice(JellyfinSubtitles.KeptText(await client.KeepSubtitleAsync(itemId, upload), guess.Language)); }
        catch (Exception ex) when (Errors.Expected(ex)) { _shell.Notice(L("Jellyfin n’a pas gardé ces sous-titres. ", "Jellyfin didn’t keep these subtitles. ") + Errors.Friendly(ex)); }
    }

    // —— Chapters, skippable passages and the next episode ——
    private void ResetMarkers()
    {
        _skips.Reset(); _episodes = []; _trickplay?.Dispose(); _trickplay = null; _timeline.SetPreview(null, null);
        _skip.IsVisible = false; _skipCancel.IsVisible = false; _timeline.SetChapters([]); _chapter.Text = "";
    }
    private async Task LoadMarkersAsync(Session session, MediaItem item, int generation)
    {
        try { var segments = await session.Client.SegmentsAsync(item.Id); if (generation == _generation) _skips.ServerSegments = PlaybackMarkers.FromJellyfin(segments); }
        catch (Exception ex) when (Errors.Expected(ex)) { }
        try
        {
            var timeline = await session.Client.TimelineAsync(item.Id, _session.Source?.Id);
            if (generation == _generation) { _skips.ServerChapters = PlaybackMarkers.FromJellyfin(timeline.Chapters); ShowTrickplay(session.Client, timeline.Trickplay); }
        }
        catch (Exception ex) when (Errors.Expected(ex)) { }
        if (item.Type == "Episode" && item.SeriesId is { } series)
        {
            try
            {
                var episodes = (await session.EpisodesAsync(series)).Items;
                if (generation != _generation) return;
                _episodes = episodes;
                var index = episodes.FindIndex(x => x.Id == item.Id);
                _skips.Next = index >= 0 ? episodes.ElementAtOrDefault(index + 1) : null;
                _subtitle.Text = PlayerText.Subtitle(item, episodes);
            }
            // Without Jellyfin: the next episode kept on this Mac, if there is one.
            catch (Exception ex) when (Errors.Expected(ex)) { _skips.Next ??= OfflineCopies.NextEpisode(_shell.OfflineCopiesKept, item)?.Item; }
        }
        if (generation != _generation) return;
        _next.IsEnabled = _skips.Next is not null;
        if (_session.Item is { } current) SetEpisodes(current);
        ToolTip.SetTip(_next, _skips.Next is not { } next ? L("Dernier épisode disponible", "Last episode available") : L("Épisode suivant · ", "Next episode · ") + PlayerText.Subtitle(next, _episodes));
        Rebuild();
    }
    private TrickplayTiles<Avalonia.Media.Imaging.Bitmap>? _trickplay;
    /// <summary>Jellyfin's trickplay pictures over the timeline, as on Windows and in Mira web.</summary>
    private void ShowTrickplay(JellyfinClient client, TrickplaySet? set)
    {
        _trickplay?.Dispose(); _trickplay = null;
        if (set is null) { _timeline.SetPreview(null, null); return; }
        // Decoded no larger than the bubble shows on a Retina screen.
        var width = (int)Math.Round(set.TilePixelWidth * Math.Min(1, 2.0 * TrickplaySet.Wanted / set.Info.Width));
        var tiles = _trickplay = new TrickplayTiles<Avalonia.Media.Imaging.Bitmap>(set, (tile, ct) => client.TrickplayTileAsync(set, tile, ct), bytes => DecodeTile(bytes, width));
        tiles.Loaded += _timeline.InvalidateVisual;
        _timeline.SetPreview(set.Info.Width / (double)set.Info.Height, seconds =>
        {
            if (tiles.At(seconds) is not var (picture, cell)) return null;
            var ratio = picture.PixelSize.Width / (double)set.TilePixelWidth;
            var area = new Rect(cell.X * ratio, cell.Y * ratio, cell.Width * ratio, Math.Min(cell.Height * ratio, picture.PixelSize.Height - cell.Y * ratio));
            return area.Height > 0 ? (picture, area) : null;
        });
    }
    private static Avalonia.Media.Imaging.Bitmap? DecodeTile(byte[] bytes, int width)
    {
        try { using var stream = new MemoryStream(bytes); return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, width); }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException) { return null; }
    }
    /// <summary>File chapters first (the source), Jellyfin's chapter list otherwise; typed server segments win.</summary>
    private void Rebuild()
    {
        _timeline.SetChapters(_skips.Chapters);
        _skips.Rebuild(_session.Duration);
    }
    private void UpdateSkip()
    {
        // « Passer les intros et les récaps »: the playhead moves past it, and what follows sees it there.
        if (_loaded && !_stopped && !_timeline.Dragging && _skips.AutoSkip(_session.Position, _shell.Settings.AutoSkip && !Group.Active) is { } passage) SeekTo(passage.End, exact: true);
        // Without a credits marker, the next episode during the last seconds.
        if (_skips.Offer(_session.Position, _session.Duration, offering: _loaded && !_stopped, _shell.Settings.AutoNext && !_sleep.AtEnd) && _skips.Active is not null)
        {
            _skipText.Text = _skips.Label(_session.Item?.Type == "Episode");
            ToolTip.SetTip(_skip, _skipText.Text + " · S");
        }
        // Playback time drives the countdown, so pausing pauses it too.
        if (_skips.Due(_session.Position) is { } next) { _ = PlayNextAsync(next, completed: true); return; }
        if (_skips.CountdownText(_session.Position) is { } label) _skipText.Text = label;
        _skipCancel.IsVisible = _skips.Counting && !_mini;
        _skip.IsVisible = !_mini && _skips.Active is not null && (_controlsShown || _skips.Counting || !_session.Paused);
    }
    private void Skip()
    {
        if (_skips.Active is not { } segment || _mpv is null || !_loaded) return;
        if (_skips.ToNext && _skips.Next is { } next) { _ = PlayNextAsync(next, completed: true); return; }
        SeekTo(segment.End, exact: true); _skip.IsVisible = false;
    }

    // —— « Regarder ensemble » ——
    /// <summary>
    /// The pill, top center, as on Windows and in Mira web: « Ensemble · … » with the controls; a wait or a pause for the
    /// group, and someone who came or went, in sight whatever they do. Loading here (opening, a seek for the group, data
    /// awaited): Jellyfin's wait may be for this Mac rather than for the others.
    /// </summary>
    public void ShowGroup()
    {
        var status = Group.Active && Group.Group is { } group && _shell.Session is { } session ? Together.GroupStatus(group, session.Connection.UserName, !_loaded || _groupReadyPending || _groupStallSince is not null) : null;
        var said = status is not null && DateTimeOffset.UtcNow < _groupMemberUntil ? _groupMember : null;
        if (status is not null) { _groupText.Text = said ?? status.Text; _groupPill.IsVisible = true; }
        _groupPill.Opacity = !_mini && status is not null && (said is not null || status.Alert || _controlsShown) ? 1 : 0;
    }
    public void ShowMember(string said) { _groupMember = said; _groupMemberUntil = DateTimeOffset.UtcNow.AddSeconds(4); ShowGroup(); }
    /// <summary>A command of the group, for the title playing here only: the Stop sent on joining names none.</summary>
    public void GroupCommand(JsonElement data)
    {
        if (_mpv is null || Group.Queue is not { } queue) return;
        if (!data.TryGetProperty("PlaylistItemId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != queue.PlaylistItemId) return;
        if (Together.CommandPlan(data, Group.Offset, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) is not { } plan) return;
        _groupPlaySoon.Stop();
        switch (plan.Action)
        {
            case "play":
                _groupPlayAt = plan.Position;
                if (plan.Wait > 0) { _groupPlaySoon.Interval = TimeSpan.FromMilliseconds(plan.Wait); _groupPlaySoon.Start(); }
                else { MoveForGroup(plan.Position); QuietPause(false); }
                break;
            case "pause": QuietPause(true); MoveForGroup(plan.Position); break;
            case "seek": WaitForGroup(plan.Position); break;
            case "stop": _ = _shell.StopPlaybackAsync(); break;
        }
    }
    /// <summary>Paused where the group is, « ready » once there.</summary>
    public void WaitForGroup(double seconds)
    {
        QuietPause(true);
        if (!MoveForGroup(seconds, ready: true)) Group.Report("Ready", seconds, false);
    }
    /// <summary>Paused or played for the group: not a press of this Mac's to send it back.</summary>
    private void QuietPause(bool pause)
    {
        if (_mpv is null) return;
        _mpv.Set("pause", pause ? "yes" : "no"); _session.Paused = pause; _pauseIcon.Kind = pause ? "play" : "pause";
        _session.LastReport = DateTimeOffset.MinValue;
    }
    /// <summary>To where the group is, when it is far enough to be seen; true when it moves.</summary>
    private bool MoveForGroup(double seconds, bool ready = false)
    {
        if (_mpv is null || !_loaded || !Together.NeedsSeek(_mpv.Number("time-pos", _session.Position), seconds)) return false;
        _groupQuietUntil = DateTimeOffset.UtcNow.AddSeconds(2);
        if (!_mpv.TryCommand("seek", seconds.ToString(CultureInfo.InvariantCulture), "absolute+exact")) return false;
        _groupReadyPending |= ready; Hold(seconds);
        return true;
    }
    /// <summary>mpv played again after a load or a seek: ready for the group, or a seek of this Mac's that the group follows.</summary>
    private void GroupRestarted()
    {
        if (!Group.PlaysFor(this) || _mpv is null) return;
        if (_groupReadyPending) { _groupReadyPending = false; Group.Report("Ready", _mpv.Number("time-pos", _session.Position), !_mpv.Flag("pause")); return; }
        // The bar dragged, the arrows, « Passer l’intro », a chapter: sent once the pointer is released.
        if (_timeline.Dragging || DateTimeOffset.UtcNow < _groupQuietUntil) return;
        _groupSeekSoon.Stop(); _groupSeekSoon.Start();
    }
    /// <summary>Paused or played here otherwise than by the button (the timer): the group does it, at the same moment.</summary>
    private void GroupPauseChanged(bool paused)
    {
        if (!Group.PlaysFor(this) || !_loaded) return;
        if (paused) { Group.Send("Pause"); return; }
        QuietPause(true); Group.Send("Unpause");
    }
    /// <summary>Waiting for data more than a moment: the others wait too, then all go on together.</summary>
    private void WatchGroupStall(MpvPlayer mpv)
    {
        if (!Group.PlaysFor(this)) return;
        if (mpv.Flag("paused-for-cache"))
        {
            _groupStallSince ??= DateTimeOffset.UtcNow;
            if (!_groupStalled && DateTimeOffset.UtcNow - _groupStallSince > TimeSpan.FromSeconds(1.5)) { _groupStalled = true; Group.Report("Buffering", _session.Position, true); }
            return;
        }
        _groupStallSince = null;
        if (_groupStalled) { _groupStalled = false; Group.Report("Ready", _session.Position, !mpv.Flag("pause")); }
    }
}
