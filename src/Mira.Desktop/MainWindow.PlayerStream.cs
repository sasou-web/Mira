using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// The stream of a title from Jellyfin (<see cref="ServerStreams"/>): the file as it is, or Jellyfin's conversion under the
/// quality chosen (Réglages → Lecture, and ⋮ in the player), with its own tracks when converted; a stream that stops
/// before the end of its title (the connection, never the end) waited for and opened again; the spinner while mpv waits
/// for data. As Mira web (player/stream.js).
/// </summary>
public partial class MainWindow
{
    /// <summary>The connection's speed to Jellyfin, for « Automatique » away from home: measured once an hour.</summary>
    private readonly ConnectionSpeed _speed = new();
    /// <summary>The stream playing from Jellyfin; null for a copy kept on this PC and for the demo.</summary>
    private ServerStream? _stream;
    /// <summary>Its audio and subtitles when Jellyfin converts it.</summary>
    private ConvertedTracks? _converted;
    private readonly StreamRecovery _recovery = new();
    /// <summary>
    /// What a stream opened again starts with: the tracks playing before (by language and name), the text subtitles of a
    /// conversion chosen in the menu (-1: none), and whether the person chose them (then kept for the series).
    /// </summary>
    private TrackMemory? _reopenTracks; private int? _reopenText; private bool _reopenChosen;
    /// <summary>The tracks playing, kept while the file plays: mpv forgets them with a file that stops.</summary>
    private (string? Audio, string? Subtitle) _trackIds; private TrackMemory? _playingTracks;
    /// <summary>The stream opened again after a cut is loading: its failure is the cut going on.</summary>
    private bool _recovering, _recoveryLoad;
    /// <summary>Since when mpv waits for data (paused-for-cache), and whether the spinner says so.</summary>
    private DateTimeOffset? _starvedSince; private bool _buffering;
    private static readonly TimeSpan BufferingShown = TimeSpan.FromSeconds(1);
    /// <summary>How often Jellyfin is asked again while it does not answer.</summary>
    private static readonly TimeSpan LostRetry = TimeSpan.FromSeconds(3);
    private DispatcherTimer? _statusHide;

    /// <summary>A title from Jellyfin: PlaybackInfo with mpv's profile at the quality chosen, the series' tracks for a conversion.</summary>
    private async Task<ServerStream> OpenStreamAsync(JellyfinClient client, MediaItem item, double start) =>
        await ServerStreams.OpenAsync(client, _speed, _settings.Quality, item, new StreamWish(start, Memory: await SeriesMemoryAsync(item)), _settings.AudioLanguage, ReachableFileAsync);
    private async Task<TrackMemory?> SeriesMemoryAsync(MediaItem item) =>
        _store is { } store && item.SeriesId is { Length: > 0 } series ? await Task.Run(() => TryCache(() => store.TracksOf(series))) : null;

    /// <summary>The stream of the playback starting, once the previous one stopped: nothing opened again yet.</summary>
    private void UseStream(ServerStream? stream)
    {
        _stream = stream; _converted = stream is { Converted: true } ? new(stream) : null;
        _recovery.Reset(); _reopenTracks = null; _reopenText = null; _reopenChosen = false; _recoveryLoad = false;
        _trackIds = default; _playingTracks = null;
    }
    /// <summary>The playback stops: Jellyfin's conversion with it (Jellyfin ends it on « stop » too, but later).</summary>
    private void ForgetStream()
    {
        if (_stream is { Converted: true } converted && _client is { } client) _ = StopEncodingAsync(client, converted.PlaySessionId);
        _stream = null; _converted = null; _recoveryLoad = false; _starvedSince = null; _buffering = false; ShowPlayerStatus(null);
    }
    private static async Task StopEncodingAsync(JellyfinClient client, string playSessionId)
    {
        try { await client.StopEncodingAsync(playSessionId); }
        catch (Exception ex) when (IsExpected(ex) || ex is ObjectDisposedException) { }
    }

    /// <summary>
    /// The same title on a new stream from Jellyfin, where it is: another quality, another audio track or picture subtitles
    /// of a conversion (<paramref name="audio"/>, <paramref name="burn"/>: Jellyfin's index, -1 for none), the connection
    /// back. Jellyfin is asked again, its conversion may have ended. False when nothing was opened: the same file as it is
    /// (unless <paramref name="always"/>), or the playback gone meanwhile. Throws when Jellyfin cannot be asked.
    /// </summary>
    private async Task<bool> ReopenStreamAsync(int? audio = null, int? burn = null, int? text = null, bool chosen = false, bool always = false, bool? paused = null)
    {
        if (_client is not { } client || _mpv is null || _session.Item is not { } item || _stream is not { } before) return false;
        var generation = _playGeneration; var keepPaused = paused ?? _mpv.Flag("pause");
        var memory = _loaded ? CurrentTracks() : _playingTracks; var ids = _loaded ? (_mpv.Get("aid"), _mpv.Get("sid")) : _trackIds;
        SetPlayerLoading(true);
        var stream = await ServerStreams.OpenAsync(client, _speed, _settings.Quality, item, new StreamWish(_session.Position, audio, burn, memory), _settings.AudioLanguage, ReachableFileAsync);
        if (generation != _playGeneration || !_playing || _mpv is null || !ReferenceEquals(before, _stream)) return false;
        if (!always && !stream.Converted && !before.Converted && stream.Location == before.Location) { EndReopenLoading(); return false; }
        // Where it is now: it played on while Jellyfin answered.
        var position = _loaded ? ReportedPosition(_mpv.Number("time-pos", _session.Position)) : _session.Position;
        // The tracks by their number on the same file; by language and name on another stream (FileLoaded).
        _reloadTracks = !stream.Converted && stream.Location == before.Location ? ids : null;
        _reopenTracks = memory; _reopenText = text; _reopenChosen = chosen;
        if (before.Converted && before.PlaySessionId != stream.PlaySessionId) _ = StopEncodingAsync(client, before.PlaySessionId);
        _stream = stream; _converted = stream.Converted ? new(stream) : null;
        _session.Reopened(stream.Source, stream.PlaySessionId, stream.PlayMethod); _session.Position = position;
        if (_fileOpen) _skipEnds++;
        _fileOpen = true; _loaded = false; SeekBar.IsEnabled = false; _wake = WakeState.Reloading;
        _playLocation = stream.Location; _playAuthorization = stream.Authorization;
        _mpv.Load(stream.Location, position, stream.Authorization, keepPaused);
        return true;
    }
    /// <summary>Nothing opened again: the spinner goes, unless the video waits for data.</summary>
    private void EndReopenLoading() { if (_loaded && !_buffering) SetPlayerLoading(false); }
    /// <summary>The tracks playing, as a series keeps them.</summary>
    private TrackMemory? CurrentTracks()
    {
        if (_mpv is null) return null;
        var sid = _mpv.Get("sid");
        return _converted is { } converted ? converted.Remember(sid) : SeriesTracks.Remember(_mpv.Get("track-list"), _mpv.Get("aid"), sid);
    }
    /// <summary>Each tick: the tracks playing, read again only when mpv's choice changes.</summary>
    private void KeepTracks()
    {
        if (_mpv is null) return;
        var ids = (_mpv.Get("aid"), _mpv.Get("sid"));
        if (ids == _trackIds) return;
        _trackIds = ids; _playingTracks = CurrentTracks();
    }

    /// <summary>
    /// A stream from Jellyfin that stopped while it played, before the end of its title (mpv calls a broken connection the
    /// end of the file), or the stream opened again after it that could not load: never the end of the title.
    /// </summary>
    private bool StreamCut() => _stream is not null && IsStream(_playLocation)
        && (_loaded && StreamRecovery.CutShort(_session.Position, _session.Duration) || _recoveryLoad);
    /// <summary>
    /// A cut stream (<see cref="StreamRecovery"/>): Jellyfin silent, waited for, as said under the spinner; answering, the
    /// stream is opened again where it was; stopped again before it played on, « Lecture impossible » with « Réessayer ».
    /// </summary>
    private async Task RecoverStreamAsync(int generation)
    {
        if (_recovering) return;
        _recovering = true;
        try
        {
            _loaded = false; _recoveryLoad = false; _session.Failure = null; SeekBar.IsEnabled = false; SetPlayerLoading(true);
            // Back after Jellyfin was silent: a new start, not a stream that failed again.
            var waited = false;
            while (generation == _playGeneration && _playing && !_closing)
            {
                var up = _client is { } client && await AnswersAsync(client);
                if (generation != _playGeneration || !_playing || _closing) return;
                var step = up && waited ? RecoveryStep.Reopen : _recovery.Step(up);
                if (step == RecoveryStep.Fail) { await GiveUpStreamAsync(); return; }
                if (step == RecoveryStep.Wait) { waited = true; _wake = WakeState.Waiting; ShowPlayerStatus(StreamRecovery.LostText); await Task.Delay(LostRetry); continue; }
                if (!waited) _recovery.Reopening(_session.Position);
                try
                {
                    if (await ReopenStreamAsync(always: true)) _recoveryLoad = true;
                    return;
                }
                catch (Exception ex) when (IsExpected(ex)) { waited = true; _wake = WakeState.Waiting; ShowPlayerStatus(StreamRecovery.LostText); await Task.Delay(LostRetry); }
            }
        }
        finally { _recovering = false; }
    }
    private async Task<bool> AnswersAsync(JellyfinClient client)
    {
        try { return await client.PingAsync(); }
        catch (Exception ex) when (IsExpected(ex) || ex is ObjectDisposedException) { return false; }
    }
    /// <summary>Opened again and stopped again at once: the title cannot play from here; « Réessayer » picks it up where it was.</summary>
    private async Task GiveUpStreamAsync()
    {
        var item = _session.Item; var mini = _miniPlayer;
        await RememberCurrentPlaybackAsync();
        await StopPlaybackAsync();
        PlaybackFailed(StreamRecovery.StuckText, item, mini, RetryText, item is null ? null : () => PlayAsync(item, mini));
    }

    /// <summary>mpv waiting for data (a slow connection, a seek far ahead): the spinner after a second, as at the opening.</summary>
    private void ShowBuffering()
    {
        if (_mpv is null) return;
        _starvedSince = _mpv.Flag("paused-for-cache") ? _starvedSince ?? DateTimeOffset.UtcNow : null;
        var buffering = _starvedSince is { } since && DateTimeOffset.UtcNow - since > BufferingShown;
        if (buffering == _buffering) return;
        _buffering = buffering; SetPlayerLoading(buffering);
    }
    /// <summary>A line under the spinner, why the video waits; null hides it, and <paramref name="seconds"/> after that long.</summary>
    private void ShowPlayerStatus(string? text, double seconds = 0)
    {
        _statusHide?.Stop();
        if (text is null) { PlayerStatus.Visibility = Visibility.Collapsed; return; }
        PlayerStatusText.Text = text;
        if (PlayerStatus.Visibility != Visibility.Visible) Motion.Reveal(PlayerStatus, 200, 6);
        if (seconds <= 0) return;
        if (_statusHide is null) { _statusHide = new DispatcherTimer(); _statusHide.Tick += (_, _) => ShowPlayerStatus(null); }
        _statusHide.Interval = TimeSpan.FromSeconds(seconds); _statusHide.Start();
    }

    /// <summary>« Qualité » in ⋮: the choices of Réglages, and what Jellyfin sends now.</summary>
    private void BuildQualityChoices()
    {
        QualityList.Children.Clear();
        QualitySection.Visibility = _stream is null ? Visibility.Collapsed : Visibility.Visible;
        if (_stream is not { } stream) return;
        var current = StreamQuality.Known(_settings.Quality);
        foreach (var (key, label, chip, details) in StreamQuality.Choices)
        {
            var choice = new RadioButton { Style = (Style)FindResource("PlayerSpeedChoice"), GroupName = "quality", Content = chip, Tag = key, ToolTip = $"{label} · {details}", IsChecked = key == current };
            System.Windows.Automation.AutomationProperties.SetName(choice, L("Qualité ", "Quality ") + label);
            choice.Checked += (_, _) => { if (!_menuBuilding) _ = ChooseQualityAsync(key); };
            QualityList.Children.Add(choice);
        }
        QualityStatus.Text = StreamQuality.Describe(stream);
    }
    /// <summary>Another quality, kept in the settings: the title goes on from where it is on the stream it gives.</summary>
    private async Task ChooseQualityAsync(string quality)
    {
        if (StreamQuality.Known(_settings.Quality) == quality) return;
        _settings.Quality = quality; TrySaveSettings(); ShowPlayerControls();
        // A stream already being opened again (the connection back) takes the new quality.
        if (_recovering) return;
        try { await ReopenStreamAsync(); }
        catch (Exception ex) when (IsExpected(ex)) { EndReopenLoading(); ShowPlayerStatus(Friendly(ex), 6); }
        if (PlayerOptionsPopup.IsOpen && MoreMenu.Visibility == Visibility.Visible && _stream is { } stream) QualityStatus.Text = StreamQuality.Describe(stream);
    }

    /// <summary>
    /// The audio and subtitles of a conversion, from the source: another audio track or picture subtitles open another
    /// conversion; text subtitles show at once, fetched beside it; files of the person's (« Charger un fichier… ») stay mpv's.
    /// </summary>
    private void BuildConvertedTrackMenu(ConvertedTracks converted)
    {
        if (_mpv is null) return;
        var stream = converted.Stream; var sid = _mpv.Get("sid"); var shown = converted.Shown(sid);
        foreach (var track in converted.Audio)
        {
            var index = ConvertedTracks.Index(track);
            AudioTrackList.Children.Add(MenuChoice("audio", track.Label, track.Details, index == stream.Audio, () => _ = ChooseConvertedAsync(audio: index)));
        }
        if (AudioTrackList.Children.Count == 0) AudioTrackList.Children.Add(new TextBlock { Text = L("Aucune piste audio", "No audio track"), Foreground = Palette.Get("Faint"), FontSize = 13, Margin = new Thickness(10, 8, 10, 8) });
        SubtitleTrackList.Children.Add(MenuChoice("subtitles", L("Désactivés", "Off"), "", shown is null && !converted.ShowsOther(sid), () => _ = ChooseConvertedAsync(text: -1)));
        foreach (var track in converted.Subtitles)
        {
            var index = ConvertedTracks.Index(track); var picture = converted.IsPicture(index);
            // Text that Jellyfin serves from another address: never fetched with the token.
            if (!picture && converted.Path(index) is null) continue;
            var details = picture ? string.Join(" · ", new[] { track.Details, L("incrustés par Jellyfin", "burned in by Jellyfin") }.Where(x => x.Length > 0)) : track.Details;
            SubtitleTrackList.Children.Add(MenuChoice("subtitles", track.Label, details, index == shown, () => _ = ChooseConvertedAsync(text: picture ? null : index, burn: picture ? index : null)));
        }
        foreach (var track in _mpv.Tracks().Where(t => t.Type == "sub" && !converted.Fetched(t.Id)))
            SubtitleTrackList.Children.Add(MenuChoice("subtitles", track.Label, track.Details, track.Id == sid, () => ChooseTrack("sid", track.Id)));
    }
    /// <summary>A track of a conversion chosen: text subtitles at once; another audio track or pictures, another conversion from where it is.</summary>
    private async Task ChooseConvertedAsync(int? audio = null, int? text = null, int? burn = null)
    {
        if (_converted is not { } converted || _mpv is null || _recovering || !_loaded) return;
        var stream = converted.Stream; ShowPlayerControls();
        if (audio is null && burn is null && stream.Burned is null) { ShowConvertedText(text is >= 0 ? text : null); RememberTracks(); return; }
        var shownText = converted.Shown(_mpv.Get("sid")) is { } shown && !converted.IsPicture(shown) ? shown : -1;
        // The menu would show the tracks of the conversion that ends.
        PlayerOptionsPopup.IsOpen = false;
        try { await ReopenStreamAsync(audio: audio ?? stream.Audio, burn: burn ?? (audio is not null ? stream.Burned ?? -1 : -1), text: burn is not null ? -1 : text ?? shownText, chosen: true, always: true); }
        catch (Exception ex) when (IsExpected(ex)) { EndReopenLoading(); ShowPlayerStatus(Friendly(ex), 6); }
    }
    /// <summary>Text subtitles of a conversion (Jellyfin's index; null: none), fetched beside it the first time they show.</summary>
    private void ShowConvertedText(int? index)
    {
        if (_mpv is null || _converted is not { } converted) return;
        if (index is not { } chosen) { _mpv.Set("sid", "no"); return; }
        if (converted.MpvId(chosen) is { } id) { _mpv.Set("sid", id); return; }
        if (_client is null || converted.Path(chosen) is not { } path || converted.Stream.Source.MediaStreams.FirstOrDefault(s => s.Index == chosen) is not { } track) return;
        try { _mpv.Command("sub-add", _client.ServerUri(path).AbsoluteUri, "select", track.DisplayTitle ?? "", track.Language ?? ""); }
        catch (IOException) { return; }
        if (_mpv.Get("sid") is { } added && !ConvertedTracks.Off(added)) converted.Added(chosen, added);
    }
}
