using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Playback;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// The stream of a title from Jellyfin, as on Windows (MainWindow.PlayerStream.cs): the file as it is, or Jellyfin's
/// conversion under « Qualité » (Réglages, and the player's menu), with its own tracks when converted; a stream that stops
/// before the end of its title (the connection, never the end) waited for and opened again; the spinner while mpv waits
/// for data.
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>The stream playing from Jellyfin, and its audio and subtitles when Jellyfin converts it.</summary>
    private ServerStream? _stream;
    private ConvertedTracks? _converted;
    private readonly StreamRecovery _recovery = new();
    /// <summary>
    /// What a stream opened again starts with: the tracks playing before (by number on the same file, else by language and
    /// name), the text subtitles of a conversion chosen in the menu (-1: none), and whether the person chose them.
    /// </summary>
    private (string? Audio, string? Subtitle)? _reloadTracks;
    private TrackMemory? _reopenTracks; private int? _reopenText; private bool _reopenChosen;
    /// <summary>The tracks playing, kept while the file plays: mpv forgets them with a file that stops.</summary>
    private (string? Audio, string? Subtitle) _trackIds; private TrackMemory? _playingTracks;
    /// <summary>A file given to mpv whose end is still to come; the ends of files replaced by their reopening are skipped.</summary>
    private bool _fileOpen;
    private int _skipEnds;
    /// <summary>A stream opened again is loading; after a cut, its failure is the cut going on.</summary>
    private bool _reloading, _recovering, _recoveryLoad;
    /// <summary>Since when mpv waits for data (paused-for-cache), and whether the spinner says so.</summary>
    private DateTimeOffset? _starvedSince; private bool _buffering;
    private static readonly TimeSpan BufferingShown = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LostRetry = TimeSpan.FromSeconds(3);
    private readonly TextBlock _statusText = new() { FontSize = 13, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 460 };
    private Border? _status;
    private DispatcherTimer? _statusHide;

    /// <summary>Under the spinner: why the video waits.</summary>
    private Border StatusPill()
    {
        AutomationProperties.SetLiveSetting(_statusText, AutomationLiveSetting.Polite);
        return _status = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(40, 132, 40, 0),
            Padding = new Thickness(16, 9), CornerRadius = new CornerRadius(18), Background = Ui.Brush("#E0151517"), BorderBrush = Ui.Brush("#30FFFFFF"), BorderThickness = new Thickness(1),
            IsHitTestVisible = false, IsVisible = false, Child = _statusText,
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) }]
        };
    }
    /// <summary>A line under the spinner; null hides it, and <paramref name="seconds"/> after that long.</summary>
    private void ShowStatus(string? text, double seconds = 0)
    {
        _statusHide?.Stop();
        if (_status is null) return;
        if (text is null) { _status.IsVisible = false; return; }
        _statusText.Text = text; _status.IsVisible = true;
        if (seconds <= 0) return;
        if (_statusHide is null) { _statusHide = new DispatcherTimer(); _statusHide.Tick += (_, _) => ShowStatus(null); }
        _statusHide.Interval = TimeSpan.FromSeconds(seconds); _statusHide.Start();
    }

    private static Task<bool> FileHere(string path) => Task.FromResult(File.Exists(path));
    private static async Task<TrackMemory?> SeriesMemoryAsync(Session session, MediaItem item) =>
        item.SeriesId is { Length: > 0 } series ? await Task.Run(() => Errors.TryCache(() => session.Store.TracksOf(series))) : null;
    /// <summary>The stream of the playback starting: nothing opened again yet.</summary>
    private void UseStream(ServerStream stream)
    {
        _stream = stream; _converted = stream.Converted ? new(stream) : null;
        _recovery.Reset(); _reloadTracks = null; _reopenTracks = null; _reopenText = null; _reopenChosen = false;
        _trackIds = default; _playingTracks = null; _fileOpen = false; _skipEnds = 0; _reloading = false; _recoveryLoad = false;
    }
    /// <summary>The playback stops: Jellyfin's conversion with it (Jellyfin ends it on « stop » too, but later).</summary>
    private void ForgetStream()
    {
        if (_stream is { Converted: true } converted && _shell.Session is { } session) _ = StopEncodingAsync(session.Client, converted.PlaySessionId);
        _stream = null; _converted = null; _skipEnds = 0; _fileOpen = false; _reloading = false; _recoveryLoad = false;
        _starvedSince = null; _buffering = false; ShowStatus(null);
    }
    private static async Task StopEncodingAsync(JellyfinClient client, string playSessionId)
    {
        try { await client.StopEncodingAsync(playSessionId); }
        catch (Exception ex) when (Errors.Expected(ex) || ex is ObjectDisposedException) { }
    }

    /// <summary>
    /// The same title on a new stream from Jellyfin, where it is: another quality, another audio track or picture subtitles
    /// of a conversion (Jellyfin's index, -1 for none), the connection back. False when nothing was opened: the same file as
    /// it is (unless <paramref name="always"/>), or the playback gone meanwhile. Throws when Jellyfin cannot be asked.
    /// </summary>
    private async Task<bool> ReopenStreamAsync(int? audio = null, int? burn = null, int? text = null, bool chosen = false, bool always = false)
    {
        if (_shell.Session is not { } session || _mpv is null || _session.Item is not { } item || _stream is not { } before) return false;
        var generation = _generation; var keepPaused = _mpv.Flag("pause");
        var memory = _loaded ? CurrentTracks() : _playingTracks; var ids = _loaded ? (_mpv.Get("aid"), _mpv.Get("sid")) : _trackIds;
        _spinner.IsVisible = true;
        var stream = await ServerStreams.OpenAsync(session.Client, _shell.Speed, _shell.Settings.Quality, item, new StreamWish(_session.Position, audio, burn, memory), _shell.Settings.AudioLanguage, FileHere);
        if (generation != _generation || _stopped || _mpv is null || !ReferenceEquals(before, _stream)) return false;
        if (!always && !stream.Converted && !before.Converted && stream.Location == before.Location) { EndReopenLoading(); return false; }
        // Where it is now: it played on while Jellyfin answered.
        var position = _loaded ? Reported(_mpv.Number("time-pos", _session.Position)) : _session.Position;
        _reloadTracks = !stream.Converted && stream.Location == before.Location ? ids : null;
        _reopenTracks = memory; _reopenText = text; _reopenChosen = chosen;
        if (before.Converted && before.PlaySessionId != stream.PlaySessionId) _ = StopEncodingAsync(session.Client, before.PlaySessionId);
        _stream = stream; _converted = stream.Converted ? new(stream) : null;
        _session.Reopened(stream.Source, stream.PlaySessionId, stream.PlayMethod); _session.Position = position;
        if (_fileOpen) _skipEnds++;
        _fileOpen = true; _loaded = false; _reloading = true;
        _mpv.Load(stream.Location, position, stream.Authorization, keepPaused);
        return true;
    }
    /// <summary>Nothing opened again: the spinner goes, unless the video waits for data.</summary>
    private void EndReopenLoading() { if (_loaded && !_buffering) _spinner.IsVisible = false; }
    /// <summary>The tracks playing, as a series keeps them.</summary>
    private TrackMemory? CurrentTracks()
    {
        if (_mpv is null) return null;
        var sid = _mpv.Get("sid");
        return _converted is { } converted ? converted.Remember(sid) : SeriesTracks.Remember(_mpv.Get("track-list"), _mpv.Get("aid"), sid);
    }
    /// <summary>Each tick: the tracks playing, read again only when mpv's choice changes.</summary>
    private void KeepTracks(MpvPlayer mpv)
    {
        var ids = (mpv.Get("aid"), mpv.Get("sid"));
        if (ids == _trackIds) return;
        _trackIds = ids; _playingTracks = CurrentTracks();
    }
    /// <summary>
    /// The tracks a file starts with: the series' or the settings' languages; opened again, those playing before, or those
    /// chosen in a conversion's menu, then kept for the series.
    /// </summary>
    private async Task ChooseStartTracksAsync(Session session, bool reopened, int generation)
    {
        if (_session.Item is not { } item) return;
        if (_converted is { } converted)
        {
            var memory = reopened ? _reopenTracks : await SeriesMemoryAsync(session, item);
            if (generation != _generation || !ReferenceEquals(converted, _converted)) return;
            ShowConvertedText(reopened && _reopenText is { } chosen ? chosen >= 0 ? chosen : null : converted.StartText(memory, _shell.Settings.SubtitleLanguage));
        }
        else if (reopened && _reloadTracks is { } ids)
        {
            if (!string.IsNullOrEmpty(ids.Audio)) _mpv?.Set("aid", ids.Audio);
            if (!string.IsNullOrEmpty(ids.Subtitle)) _mpv?.Set("sid", ids.Subtitle);
        }
        else if ((reopened ? _reopenTracks : await SeriesMemoryAsync(session, item)) is { } tracks && generation == _generation && _mpv is not null)
        {
            // The audio and subtitles last chosen in this series, where this episode has them.
            var (audio, subtitle) = SeriesTracks.Choose(_mpv.Get("track-list"), tracks);
            if (audio is not null) _mpv.Set("aid", audio);
            if (subtitle is not null) _mpv.Set("sid", subtitle);
        }
        if (generation != _generation) return;
        if (reopened && _reopenChosen) RememberTracks();
        _reloadTracks = null; _reopenText = null; _reopenChosen = false;
    }
    /// <summary>The tracks playing, kept for the next episodes of this series (<see cref="SeriesTracks"/>).</summary>
    private void RememberTracks()
    {
        if (_session.Item?.SeriesId is not { Length: > 0 } series || _shell.Session is not { } session || CurrentTracks() is not { } tracks) return;
        _ = Task.Run(() => Errors.TryCache(() => session.Store.RememberTracks(series, tracks)));
        _shell.ChoicesChanged();
    }

    /// <summary>A stream from Jellyfin that stopped while it played, before the end of its title, or one opened again after it that could not load.</summary>
    private bool StreamCut() => _stream is not null && Uri.TryCreate(_stream.Location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        && (_loaded && StreamRecovery.CutShort(_session.Position, _session.Duration) || _recoveryLoad);
    /// <summary>
    /// A cut stream (<see cref="StreamRecovery"/>): Jellyfin silent, waited for, as said under the spinner; answering, the
    /// stream is opened again where it was; stopped again before it played on, the player closes and says so, with « Réessayer ».
    /// </summary>
    private async Task RecoverStreamAsync(int generation)
    {
        if (_recovering) return;
        _recovering = true;
        try
        {
            _loaded = false; _recoveryLoad = false; _session.Failure = null; _spinner.IsVisible = true;
            // Back after Jellyfin was silent: a new start, not a stream that failed again.
            var waited = false;
            while (generation == _generation && !_stopped)
            {
                var up = _shell.Session is { } session && await AnswersAsync(session.Client);
                if (generation != _generation || _stopped) return;
                var step = up && waited ? RecoveryStep.Reopen : _recovery.Step(up);
                if (step == RecoveryStep.Fail) { await GiveUpStreamAsync(); return; }
                if (step == RecoveryStep.Wait) { waited = true; ShowStatus(StreamRecovery.LostText); await Task.Delay(LostRetry); continue; }
                if (!waited) _recovery.Reopening(_session.Position);
                try
                {
                    if (await ReopenStreamAsync(always: true)) _recoveryLoad = true;
                    return;
                }
                catch (Exception ex) when (Errors.Expected(ex)) { waited = true; ShowStatus(StreamRecovery.LostText); await Task.Delay(LostRetry); }
            }
        }
        finally { _recovering = false; }
    }
    private static async Task<bool> AnswersAsync(JellyfinClient client)
    {
        try { return await client.PingAsync(); }
        catch (Exception ex) when (Errors.Expected(ex) || ex is ObjectDisposedException) { return false; }
    }
    private async Task GiveUpStreamAsync()
    {
        var item = _session.Item;
        await StopAsync();
        await _shell.CloseOverlayPlayerAsync(this);
        _shell.Notice(StreamRecovery.StuckText, item is null ? null : L("Réessayer", "Try again"), item is null ? null : () => _ = _shell.PlayAsync(item), sticky: true);
    }

    /// <summary>mpv waiting for data (a slow connection, a seek far ahead): the spinner after a second, as at the opening.</summary>
    private void ShowBuffering(MpvPlayer mpv)
    {
        _starvedSince = mpv.Flag("paused-for-cache") ? _starvedSince ?? DateTimeOffset.UtcNow : null;
        var buffering = _starvedSince is { } since && DateTimeOffset.UtcNow - since > BufferingShown;
        if (buffering == _buffering) return;
        _buffering = buffering; _spinner.IsVisible = buffering;
    }

    /// <summary>« Qualité » in the menu: the choices of Réglages, and what Jellyfin sends now.</summary>
    private Control QualityColumn(ServerStream stream)
    {
        var column = new StackPanel { Spacing = 2, MinWidth = 200, MaxWidth = 240 };
        column.Children.Add(new TextBlock { Text = L("QUALITÉ", "QUALITY"), Classes = { "label" }, Margin = new Thickness(10, 0, 0, 8) });
        var current = StreamQuality.Known(_shell.Settings.Quality);
        foreach (var (key, label, _, details) in StreamQuality.Choices)
            column.Children.Add(Entry(label, details, key == current, () => _ = ChooseQualityAsync(key)));
        column.Children.Add(new TextBlock { Text = StreamQuality.Describe(stream), FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 6, 0, 0) });
        return column;
    }
    /// <summary>Another quality, kept in the settings: the title goes on from where it is on the stream it gives.</summary>
    private async Task ChooseQualityAsync(string quality)
    {
        if (StreamQuality.Known(_shell.Settings.Quality) == quality) return;
        _shell.Settings.Quality = quality;
        try { _shell.Profile.SaveSettings(_shell.Settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        ShowControls();
        // A stream already being opened again (the connection back) takes the new quality.
        if (_recovering) return;
        try { await ReopenStreamAsync(); }
        catch (Exception ex) when (Errors.Expected(ex)) { EndReopenLoading(); ShowStatus(Errors.Friendly(ex), 6); }
        RefreshMenus();
    }

    /// <summary>
    /// The audio and subtitles of a conversion, from the source: another audio track or picture subtitles open another
    /// conversion; text subtitles show at once, fetched beside it; files of the person's (« Charger un fichier… ») stay mpv's.
    /// </summary>
    private Control ConvertedTracksMenu(ConvertedTracks converted)
    {
        var stream = converted.Stream; var sid = _mpv?.Get("sid"); var shown = converted.Shown(sid);
        var audio = new StackPanel { Spacing = 2, MinWidth = 240 };
        audio.Children.Add(new TextBlock { Text = L("AUDIO", "AUDIO"), Classes = { "label" }, Margin = new Thickness(10, 0, 0, 8) });
        foreach (var track in converted.Audio)
        {
            var index = ConvertedTracks.Index(track);
            audio.Children.Add(Entry(track.Label, track.Details, index == stream.Audio, () => _ = ChooseConvertedAsync(audio: index)));
        }
        var subtitles = new StackPanel { Spacing = 2, MinWidth = 240 };
        subtitles.Children.Add(new TextBlock { Text = L("SOUS-TITRES", "SUBTITLES"), Classes = { "label" }, Margin = new Thickness(10, 0, 0, 8) });
        subtitles.Children.Add(Entry(L("Désactivés", "Off"), null, shown is null && !converted.ShowsOther(sid), () => _ = ChooseConvertedAsync(text: -1)));
        foreach (var track in converted.Subtitles)
        {
            var index = ConvertedTracks.Index(track); var picture = converted.IsPicture(index);
            // Text that Jellyfin serves from another address: never fetched with the token.
            if (!picture && converted.Path(index) is null) continue;
            var details = picture ? string.Join(" · ", new[] { track.Details, L("incrustés par Jellyfin", "burned in by Jellyfin") }.Where(x => x.Length > 0)) : track.Details;
            subtitles.Children.Add(Entry(track.Label, details, index == shown, () => _ = ChooseConvertedAsync(text: picture ? null : index, burn: picture ? index : null)));
        }
        foreach (var track in (_mpv?.Tracks() ?? []).Where(t => t.Type == "sub" && !converted.Fetched(t.Id)))
            subtitles.Children.Add(Entry(track.Label, track.Details, track.Id == sid, () => ChooseTrack("sid", track.Id)));
        subtitles.Children.Add(Entry(L("Charger un fichier…", "Load a file…"), null, false, () => _ = LoadSubtitleAsync()));
        return Ui.Row(18, audio, subtitles);
    }
    /// <summary>A track of a conversion chosen: text subtitles at once; another audio track or pictures, another conversion from where it is.</summary>
    private async Task ChooseConvertedAsync(int? audio = null, int? text = null, int? burn = null)
    {
        if (_converted is not { } converted || _mpv is null || _recovering || !_loaded) return;
        var stream = converted.Stream; ShowControls();
        if (audio is null && burn is null && stream.Burned is null) { ShowConvertedText(text is >= 0 ? text : null); RememberTracks(); return; }
        var shownText = converted.Shown(_mpv.Get("sid")) is { } shown && !converted.IsPicture(shown) ? shown : -1;
        // The menu would show the tracks of the conversion that ends.
        (_tracks.Flyout as Flyout)?.Hide();
        try { await ReopenStreamAsync(audio: audio ?? stream.Audio, burn: burn ?? (audio is not null ? stream.Burned ?? -1 : -1), text: burn is not null ? -1 : text ?? shownText, chosen: true, always: true); }
        catch (Exception ex) when (Errors.Expected(ex)) { EndReopenLoading(); ShowStatus(Errors.Friendly(ex), 6); }
    }
    /// <summary>Text subtitles of a conversion (Jellyfin's index; null: none), fetched beside it the first time they show.</summary>
    private void ShowConvertedText(int? index)
    {
        if (_mpv is null || _converted is not { } converted) return;
        if (index is not { } chosen) { _mpv.Set("sid", "no"); return; }
        if (converted.MpvId(chosen) is { } id) { _mpv.Set("sid", id); return; }
        if (_shell.Session is not { } session || converted.Path(chosen) is not { } path || converted.Stream.Source.MediaStreams.FirstOrDefault(s => s.Index == chosen) is not { } track) return;
        if (!_mpv.TryCommand("sub-add", session.Client.ServerUri(path).AbsoluteUri, "select", track.DisplayTitle ?? "", track.Language ?? "")) return;
        if (_mpv.Get("sid") is { } added && !ConvertedTracks.Off(added)) converted.Added(chosen, added);
    }
}
