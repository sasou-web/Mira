using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>Chapters, skippable passages (opening, ending, recap…) and the next-episode shortcut.</summary>
public partial class MainWindow
{
    private readonly SkipPlanner _skips = new();
    private readonly SleepTimer _sleep = new();
    private readonly StillWatching _stillWatching = new();
    /// <summary>The next <see cref="PlayAsync"/> is the countdown's or the end of an episode's, not a press.</summary>
    private bool _nextByItself;
    /// <summary>
    /// « Tu regardes toujours ? » (<see cref="StillWatching"/>): the next episode would start by itself, and nobody has touched
    /// the PC (Windows' last input, a controller Mira read) for three episodes. Never in a group.
    /// </summary>
    private bool AsksStillWatching() => !InGroup && !_demo && _stillWatching.Asks(DateTimeOffset.Now - UserIdle(), _settings.StillWatching);
    /// <summary>The episode ended and the next one waits: the player's screen asks, or a notice over the library from the mini-player.</summary>
    private void ShowStillWatching(MediaItem next, bool mini)
    {
        var reason = StillWatching.Reason(PlayerText.Subtitle(next));
        if (mini) { ReturnToLibrary(); SetNotice(StillWatching.Question + " " + reason, StillWatching.Continue, () => _ = PlayAsync(next, keepMini: true), sticky: true); return; }
        ShowPlayerScreen(StillWatching.Question, "eye", reason, next, mini: false, StillWatching.Continue, () => PlayAsync(next));
    }
    private bool _skipShown;
    private double _skipLift = double.NaN;
    /// <summary>The pill already showed the countdown at the last tick: its width is the countdown's.</summary>
    private bool _skipCounted;
    /// <summary>S jumps over the passage under the playhead, pill shown or not (it hides a few seconds in, SkipPlanner.Shows).</summary>
    private bool SkipAvailable => _skips.Active is not null;
    private void ResetMarkers()
    {
        _skips.Reset(); _trickplay?.Dispose(); _trickplay = null;
        SeekBar.Reset(); SetSkipVisible(false, immediate: true); UpdateChapterText(0);
    }
    private void SkipCancel_Click(object sender, RoutedEventArgs e)
    {
        StayOnEpisode();
        // In a group every device counts down, and the first one done puts the next episode on for all: the whole group
        // stays (Together.StayModes, heard by GroupStays on the others).
        if (PlayingForGroup) foreach (var mode in Together.StayModes) SendGroup("SetRepeatMode", new JsonObject { ["Mode"] = mode });
    }
    /// <summary>Stay on this episode: no countdown, and no automatic jump when it ends.</summary>
    private void StayOnEpisode()
    {
        SkipText.Text = _skips.Cancel();
        _ = Motion.HideAsync(SkipCancel, 140);
    }
    /// <summary>
    /// Someone in the group pressed « Annuler »: this PC stays on the episode too, its countdown stopped (or never started:
    /// the devices are a moment apart), and the pill says why the card went.
    /// </summary>
    private void GroupStays(string playlistItemId)
    {
        if (_groupQueue?.PlaylistItemId != playlistItemId || !PlayingForGroup || _skips.Stay) return;
        if (_skips.Counting) { StayOnEpisode(); UpdateSkip(); }
        else _skips.Stay = true;
        _groupMember = Together.StayText; _groupMemberUntil = DateTimeOffset.UtcNow.AddSeconds(4);
        UpdateGroupPill();
    }
    private async Task LoadServerMarkersAsync(MediaItem item, int generation)
    {
        if (_demo || _client is not { } client) return;
        var segments = client.SegmentsAsync(item.Id);
        var timeline = client.TimelineAsync(item.Id, _session.Source?.Id);
        // A part of an episode in several files: the series of its title, and no next episode before its last part.
        var parts = _parts is { } playing && playing.Holds(item.Id) ? playing : null;
        var episode = parts?.Title ?? item;
        var next = episode.Type == "Episode" ? EpisodeContextAsync(episode, quiet: true) : Task.FromResult<(MediaItem? Next, List<MediaItem> Episodes)>((null, []));
        // Older servers, or servers without a segment provider, simply have no markers.
        try { var result = await segments; if (generation == _playGeneration) _skips.ServerSegments = PlaybackMarkers.FromJellyfin(result); }
        catch (Exception ex) when (IsExpected(ex)) { }
        try
        {
            var result = await timeline;
            if (generation == _playGeneration) { _skips.ServerChapters = PlaybackMarkers.FromJellyfin(result.Chapters); ShowTrickplay(client, result.Trickplay); }
        }
        catch (Exception ex) when (IsExpected(ex)) { }
        var (following, episodes) = await next;
        if (generation != _playGeneration) return;
        _skips.Next = parts?.After(item.Id) is null ? following : null;
        UpdateWindowsPlayback();
        if (episode.Type == "Episode")
        {
            NextButton.IsEnabled = following is not null;
            NextButton.ToolTip = following is null ? L("Dernier épisode disponible", "Last available episode") : L("Épisode suivant · ", "Next episode · ") + PlayerText.Subtitle(following, episodes);
            SetPlayerEpisodes(episode, episodes);
            if (episodes.Count > 0) PlayingSubtitle.Content = parts?.Subtitle(item.Id, episodes) ?? PlayerText.Subtitle(item, episodes);
        }
        RebuildSegments();
    }
    private TrickplayTiles<BitmapSource>? _trickplay;
    private (int Tile, int X, int Y, BitmapSource Picture)? _trickplayShown;
    /// <summary>Jellyfin's trickplay pictures over the timeline, as in Mira web: the moment pointed at, before jumping there.</summary>
    private void ShowTrickplay(JellyfinClient client, TrickplaySet? set)
    {
        _trickplay?.Dispose(); _trickplay = null; _trickplayShown = null;
        if (set is null) { SeekBar.SetPreview(null, null); return; }
        // Decoded at the size the bubble shows: a whole tile is otherwise some 20 MB.
        var scale = Math.Min(1, (double)TrickplaySet.Wanted / set.Info.Width);
        var tiles = _trickplay = new TrickplayTiles<BitmapSource>(set, (tile, ct) => client.TrickplayTileAsync(set, tile, ct), bytes => DecodeTile(bytes, (int)Math.Round(set.TilePixelWidth * scale)));
        tiles.Loaded += SeekBar.RefreshPreview;
        SeekBar.SetPreview(set.Info.Width / (double)set.Info.Height, seconds =>
        {
            if (tiles.At(seconds) is not var (picture, cell)) return null;
            if (_trickplayShown is { } shown && shown.Tile == cell.Tile && shown.X == cell.X && shown.Y == cell.Y) return shown.Picture;
            var ratio = picture.PixelWidth / (double)set.TilePixelWidth;
            var area = new Int32Rect((int)Math.Round(cell.X * ratio), (int)Math.Round(cell.Y * ratio), (int)Math.Round(cell.Width * ratio), (int)Math.Round(cell.Height * ratio));
            // A tile whose last row Jellyfin cut short.
            area.Width = Math.Min(area.Width, picture.PixelWidth - area.X); area.Height = Math.Min(area.Height, picture.PixelHeight - area.Y);
            if (area.Width <= 0 || area.Height <= 0) return null;
            var crop = new CroppedBitmap(picture, area); crop.Freeze();
            _trickplayShown = (cell.Tile, cell.X, cell.Y, crop);
            return crop;
        });
    }
    private static BitmapSource? DecodeTile(byte[] bytes, int pixelWidth)
    {
        try { return PictureBytes.Decode(bytes, pixelWidth); }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException) { return null; }
    }
    private void LoadFileChapters()
    {
        if (_mpv is null) return;
        _skips.FileChapters = _mpv.Chapters(); RebuildSegments();
    }
    /// <summary>File chapters first (the source), Jellyfin's chapter list otherwise; typed server segments win.</summary>
    private void RebuildSegments()
    {
        SeekBar.SetChapters(_skips.Chapters);
        _skips.Rebuild(_session.Duration);
        _skips.Active = null; UpdateSkip(); UpdateChapterText(_session.Position);
    }
    private void UpdateSkip()
    {
        if (SkipHost is null) return;
        // « Passer les intros et les récaps »: the playhead moves past it, and what follows sees it there. Not while the
        // bar is dragged: the pointer only passes through. Not in a group either: each device would send its own seek.
        if (_playing && _loaded && !_seeking && _skips.AutoSkip(_session.Position, _settings.AutoSkip && !InGroup) is { } passage)
        {
            if (TryCommand("seek", passage.End.ToString(CultureInfo.InvariantCulture), "absolute+exact")) HoldPosition(passage.End);
        }
        // Without a credits marker, the next episode during the last seconds.
        if (_skips.Offer(_session.Position, _session.Duration, offering: _playing && _loaded && !_miniPlayer, _settings.AutoNext && !_sleep.AtEnd && !AsksStillWatching()))
        {
            if (_skips.Active is not null)
            {
                SkipText.Text = _skips.Label(_session.Item?.Type == "Episode");
                AutomationProperties.SetName(SkipButton, SkipText.Text);
                SkipButton.ToolTip = SkipText.Text + " · S";
            }
        }
        // Playback time drives the countdown, so pausing pauses it too.
        if (_skips.Due(_session.Position) is { } next) { _nextByItself = true; _ = PlayAsync(next, _miniPlayer, completePrevious: true); return; }
        var counting = _skips.Counting;
        // The pill keeps the width of « … 10 s » while it counts down: otherwise « Annuler », beside it, moves at 10 → 9.
        SkipText.MinWidth = counting && _skipCounted ? Math.Max(SkipText.MinWidth, SkipText.ActualWidth) : 0;
        if (_skips.CountdownText(_session.Position) is { } label && SkipText.Text != label) SkipText.Text = label;
        _skipCounted = counting;
        // The countdown shows the next episode's card (maquette A); « Annuler » leaves the pill alone.
        var card = counting && _skips.Next is not null;
        // A player menu open over the bottom right: the pill and the card leave the corner to it (they touched the
        // Audio and subtitles menu); the countdown goes on and they are back when it closes.
        var menu = PlayerOptionsPopup.IsOpen;
        UpdateNextCard(card && !menu);
        if (counting && !card && SkipCancel.Visibility != Visibility.Visible) Motion.Reveal(SkipCancel, 200, 8);
        else if ((!counting || card) && SkipCancel.Visibility == Visibility.Visible && SkipCancel.IsHitTestVisible) _ = Motion.HideAsync(SkipCancel, 140);
        // A few seconds when its passage starts, then only with the controls (SkipPlanner.Shows): over the whole opening
        // it was in the way.
        SetSkipVisible(!card && !menu && _skips.Shows(_session.Position, _controlsVisible, _session.Paused));
        // Sits just above the bar's title and time while they are shown, near the bottom edge otherwise.
        var lift = _controlsVisible ? -Math.Max(0, PlayerControlsBody.ActualHeight + PlayerControlsBody.Margin.Bottom + 16 - SkipHost.Margin.Bottom) : 0;
        if (lift != _skipLift) { _skipLift = lift; Motion.Animate(SkipShift, TranslateTransform.YProperty, lift, 260, ease: Motion.EaseOut); }
    }
    private void SetSkipVisible(bool show, bool immediate = false)
    {
        if (show == _skipShown && !immediate) return;
        _skipShown = show;
        if (show) Motion.Reveal(SkipButton, 220, 10);
        else if (immediate) { foreach (var part in new FrameworkElement[] { SkipButton, SkipCancel, NextCard }) { part.BeginAnimation(OpacityProperty, null); part.Visibility = Visibility.Collapsed; } _nextCardFor = null; }
        else { _ = Motion.HideAsync(SkipButton, 160); if (SkipCancel.Visibility == Visibility.Visible) _ = Motion.HideAsync(SkipCancel, 160); }
    }
    /// <summary>The episode the card shows, filled once.</summary>
    private string? _nextCardFor;
    /// <summary>
    /// The next episode's card (maquette A, 2026-11-14): its picture (blurred while « Cacher les spoilers » hides it), the
    /// ring filled by the countdown around Play, its number, name and length, « Lire maintenant » and « Annuler ».
    /// </summary>
    private void UpdateNextCard(bool show)
    {
        if (show && _skips.Next is { } next)
        {
            if (_nextCardFor != next.Id) FillNextCard(next);
            if (_skips.CountdownText(_session.Position) is { } count && NextCardCount.Text != count) NextCardCount.Text = count;
            DrawNextRing(_skips.CountdownDone(_session.Position));
            if (NextCard.Visibility != Visibility.Visible || !NextCard.IsHitTestVisible) Motion.Reveal(NextCard, 220, 10);
        }
        else if (NextCard.Visibility == Visibility.Visible && NextCard.IsHitTestVisible) _ = Motion.HideAsync(NextCard, 160);
    }
    private void FillNextCard(MediaItem next)
    {
        _nextCardFor = next.Id;
        NextCardTitle.Inlines.Clear();
        if (next.IndexNumber is { } number) { NextCardTitle.Inlines.Add(new System.Windows.Documents.Run(number.ToString(Lang.Culture)) { Foreground = Palette.Get("Faint") }); NextCardTitle.Inlines.Add(new System.Windows.Documents.Run("  ")); }
        NextCardTitle.Inlines.Add(new System.Windows.Documents.Run(next.Name));
        NextCardDetails.Text = next.DurationLabel ?? "";
        AutomationProperties.SetName(NextCardPicture, L("Lire maintenant : ", "Play now: ") + PlayerText.Subtitle(next));
        NextCardImage.BeginAnimation(OpacityProperty, null); NextCardImage.Opacity = 1; NextCardImage.Source = null;
        if (Spoilers.Hides(next, _settings.HideSpoilers)) NextCardImage.Source = BlurHash.Of(next, Spoilers.Still(next)) is { } hash ? MediaCard.Blur(hash, 16 / 9d) : null;
        else if (_images is not null) _ = LoadEpisodeImageAsync(NextCardImage, next, 192);
    }
    /// <summary>The ring around Play: <paramref name="done"/> of the countdown, clockwise from the top.</summary>
    private void DrawNextRing(double done)
    {
        // Inside the 48 px circle, on its 2.5 px track.
        const double centre = 24, radius = 24 - 1.25;
        if (done <= .001) { NextCardRing.Data = null; return; }
        Geometry ring;
        if (done >= .999) ring = new EllipseGeometry(new Point(centre, centre), radius, radius);
        else
        {
            var angle = done * 2 * Math.PI;
            var figure = new PathFigure { StartPoint = new Point(centre, centre - radius) };
            figure.Segments.Add(new ArcSegment(new Point(centre + radius * Math.Sin(angle), centre - radius * Math.Cos(angle)), new Size(radius, radius), 0, done > .5, SweepDirection.Clockwise, true));
            ring = new PathGeometry { Figures = { figure } };
        }
        ring.Freeze(); NextCardRing.Data = ring;
    }
    private async void NextCardPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_skips.Next is not { } next || _mpv is null || !_loaded) return;
        await PlayAsync(next, _miniPlayer, completePrevious: true);
    }
    private async void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_skips.Active is not { } segment || _mpv is null || !_loaded) return;
        if (_skips.Film is { } film) { await FilmAfterTrailerAsync(film, _miniPlayer); return; }
        if (_skips.ToNext && _skips.Next is { } next) { await PlayAsync(next, _miniPlayer, completePrevious: true); return; }
        if (!TryCommand("seek", segment.End.ToString(CultureInfo.InvariantCulture), "absolute+exact")) return;
        HoldPosition(segment.End); SetSkipVisible(false); SeekedForGroup();
    }
}
