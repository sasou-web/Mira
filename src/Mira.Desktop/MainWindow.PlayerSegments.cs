using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>Chapters, skippable passages (opening, ending, recap…) and the next-episode shortcut.</summary>
public partial class MainWindow
{
    private readonly SkipPlanner _skips = new();
    private readonly SleepTimer _sleep = new();
    private DateTimeOffset _skipSince;
    private bool _skipShown;
    private double _skipLift = double.NaN;
    // The pill stays while the controls are shown; otherwise it lingers a few seconds, as in Jellyfin.
    private static readonly TimeSpan SkipLinger = TimeSpan.FromSeconds(8);
    private bool SkipAvailable => _skipShown && _skips.Active is not null;
    private void ResetMarkers()
    {
        _skips.Reset(); _trickplay?.Dispose(); _trickplay = null;
        SeekBar.Reset(); SetSkipVisible(false, immediate: true); UpdateChapterText(0);
    }
    private void SkipCancel_Click(object sender, RoutedEventArgs e)
    {
        // Stay on this episode: no countdown, and no automatic jump when it ends.
        SkipText.Text = _skips.Cancel();
        _ = Motion.HideAsync(SkipCancel, 140);
    }
    private async Task LoadServerMarkersAsync(MediaItem item, int generation)
    {
        if (_demo || _client is not { } client) return;
        var segments = client.SegmentsAsync(item.Id);
        var timeline = client.TimelineAsync(item.Id, _session.Source?.Id);
        var next = item.Type == "Episode" ? EpisodeContextAsync(item, quiet: true) : Task.FromResult<(MediaItem? Next, List<MediaItem> Episodes)>((null, []));
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
        _skips.Next = following;
        UpdateWindowsPlayback();
        if (item.Type == "Episode")
        {
            NextButton.IsEnabled = following is not null;
            NextButton.ToolTip = following is null ? L("Dernier épisode disponible", "Last available episode") : L("Épisode suivant · ", "Next episode · ") + PlayerText.Subtitle(following, episodes);
            SetPlayerEpisodes(item, episodes);
            if (episodes.Count > 0) PlayingSubtitle.Text = PlayerText.Subtitle(item, episodes);
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
        try
        {
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = new MemoryStream(bytes); image.DecodePixelWidth = pixelWidth; image.EndInit();
            image.Freeze(); return image;
        }
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
        if (_skips.Offer(_session.Position, _session.Duration, offering: _playing && _loaded && !_miniPlayer, _settings.AutoNext && !_sleep.AtEnd))
        {
            _skipSince = DateTimeOffset.UtcNow;
            if (_skips.Active is not null)
            {
                SkipText.Text = _skips.Label(_session.Item?.Type == "Episode");
                AutomationProperties.SetName(SkipButton, SkipText.Text);
                SkipButton.ToolTip = SkipText.Text + " · S";
            }
        }
        // Playback time drives the countdown, so pausing pauses it too.
        if (_skips.Due(_session.Position) is { } next) { _ = PlayAsync(next, _miniPlayer, completePrevious: true); return; }
        var counting = _skips.Counting;
        if (_skips.CountdownText(_session.Position) is { } label && SkipText.Text != label) SkipText.Text = label;
        if (counting && SkipCancel.Visibility != Visibility.Visible) Motion.Reveal(SkipCancel, 200, 8);
        else if (!counting && SkipCancel.Visibility == Visibility.Visible && SkipCancel.IsHitTestVisible) _ = Motion.HideAsync(SkipCancel, 140);
        SetSkipVisible(_skips.Active is not null && (_controlsVisible || counting || (!_session.Paused && DateTimeOffset.UtcNow - _skipSince < SkipLinger)));
        // Sits just above the bar's title and time while they are shown, near the bottom edge otherwise.
        var lift = _controlsVisible ? -Math.Max(0, PlayerControlsBody.ActualHeight + PlayerControlsBody.Margin.Bottom + 16 - SkipHost.Margin.Bottom) : 0;
        if (lift != _skipLift) { _skipLift = lift; Motion.Animate(SkipShift, TranslateTransform.YProperty, lift, 260, ease: Motion.EaseOut); }
    }
    private void SetSkipVisible(bool show, bool immediate = false)
    {
        if (show == _skipShown && !immediate) return;
        _skipShown = show;
        if (show) Motion.Reveal(SkipButton, 220, 10);
        else if (immediate) { foreach (var button in new[] { SkipButton, SkipCancel }) { button.BeginAnimation(OpacityProperty, null); button.Visibility = Visibility.Collapsed; } }
        else { _ = Motion.HideAsync(SkipButton, 160); if (SkipCancel.Visibility == Visibility.Visible) _ = Motion.HideAsync(SkipCancel, 160); }
    }
    private async void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_skips.Active is not { } segment || _mpv is null || !_loaded) return;
        if (_skips.ToNext && _skips.Next is { } next) { await PlayAsync(next, _miniPlayer, completePrevious: true); return; }
        if (!TryCommand("seek", segment.End.ToString(CultureInfo.InvariantCulture), "absolute+exact")) return;
        HoldPosition(segment.End); SetSkipVisible(false);
    }
}
