using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>Chapters, skippable passages (opening, ending, recap…) and the next-episode shortcut.</summary>
public partial class MainWindow
{
    private List<SkipSegment> _serverSegments = [], _segments = [];
    private List<ChapterMark> _fileChapters = [], _serverChapters = [];
    private SkipSegment? _activeSkip;
    private DateTimeOffset _skipSince;
    private bool _skipShown, _skipToNext;
    private double _skipLift = double.NaN;
    private MediaItem? _nextItem;
    // The pill stays while the controls are shown; otherwise it lingers a few seconds, as in Jellyfin.
    private static readonly TimeSpan SkipLinger = TimeSpan.FromSeconds(8);
    private const double NextEpisodeLead = 20;
    private bool SkipAvailable => _skipShown && _activeSkip is not null;

    // With "Enchaîner les épisodes", the next-episode button counts down over this much playback, as on streaming services.
    private const double CountdownLength = 10;
    private double? _countdownEnd;
    private bool _autoNextCancelled;
    private void ResetMarkers()
    {
        _serverSegments = []; _segments = []; _fileChapters = []; _serverChapters = [];
        _activeSkip = null; _nextItem = null; _skipToNext = false; _countdownEnd = null; _autoNextCancelled = false;
        SeekBar.Reset(); SetSkipVisible(false, immediate: true);
    }
    private void SkipCancel_Click(object sender, RoutedEventArgs e)
    {
        // Stay on this episode: no countdown, and no automatic jump when it ends.
        _countdownEnd = null; _autoNextCancelled = true;
        SkipText.Text = PlaybackMarkers.Label(SkipKind.Outro, true, true);
        _ = Motion.HideAsync(SkipCancel, 140);
    }
    private async Task LoadServerMarkersAsync(MediaItem item, int generation)
    {
        if (_demo || _client is not { } client) return;
        var segments = client.SegmentsAsync(item.Id);
        var chapters = client.ChaptersAsync(item.Id);
        var next = item.Type == "Episode" ? GetNextAsync(item, quiet: true) : Task.FromResult<MediaItem?>(null);
        // Older servers, or servers without a segment provider, simply have no markers.
        try { var result = await segments; if (generation == _playGeneration) _serverSegments = PlaybackMarkers.FromJellyfin(result); }
        catch (Exception ex) when (IsExpected(ex)) { }
        try { var result = await chapters; if (generation == _playGeneration) _serverChapters = PlaybackMarkers.FromJellyfin(result); }
        catch (Exception ex) when (IsExpected(ex)) { }
        var following = await next;
        if (generation != _playGeneration) return;
        _nextItem = following;
        UpdateWindowsPlayback();
        if (item.Type == "Episode")
        {
            NextButton.IsEnabled = following is not null;
            NextButton.ToolTip = following is null ? "Dernier épisode disponible" : "Épisode suivant · " + following.Subtitle;
        }
        RebuildSegments();
    }
    private void LoadFileChapters()
    {
        if (_mpv is null) return;
        _fileChapters = _mpv.Chapters(); RebuildSegments();
    }
    /// <summary>File chapters first (the source), Jellyfin's chapter list otherwise; typed server segments win.</summary>
    private void RebuildSegments()
    {
        var chapters = _fileChapters.Count > 0 ? _fileChapters : _serverChapters;
        SeekBar.SetChapters(chapters);
        _segments = PlaybackMarkers.Merge(_serverSegments, PlaybackMarkers.FromChapters(chapters, _duration));
        _activeSkip = null; UpdateSkip();
    }
    private void UpdateSkip()
    {
        if (SkipHost is null) return;
        SkipSegment? active = null;
        if (_playing && _loaded && !_miniPlayer)
        {
            active = PlaybackMarkers.Active(_segments, _position);
            // Without a credits marker, offer the next episode during the last seconds.
            if (active is null && _nextItem is not null && _duration > 120 && _position > 0 && _duration - _position <= NextEpisodeLead)
                active = new SkipSegment(SkipKind.Outro, _duration - NextEpisodeLead, _duration);
        }
        if (active != _activeSkip)
        {
            _activeSkip = active; _skipSince = DateTimeOffset.UtcNow; _countdownEnd = null;
            if (active is { } segment)
            {
                _skipToNext = _nextItem is not null && PlaybackMarkers.EndsPlayback(_segments, segment, _duration);
                SkipText.Text = PlaybackMarkers.Label(segment.Kind, _playingItem?.Type == "Episode", _skipToNext);
                AutomationProperties.SetName(SkipButton, SkipText.Text);
                SkipButton.ToolTip = SkipText.Text + " · S";
                if (_skipToNext && _settings.AutoNext && !_autoNextCancelled) _countdownEnd = Math.Max(_position, segment.Start) + CountdownLength;
            }
        }
        // Playback time drives the countdown, so pausing pauses it too.
        var counting = _countdownEnd is not null && _activeSkip is not null;
        if (counting)
        {
            var left = (int)Math.Ceiling(_countdownEnd!.Value - _position);
            if (left <= 0 && _nextItem is { } next) { _countdownEnd = null; _ = PlayAsync(next, _miniPlayer, completePrevious: true); return; }
            var label = $"Épisode suivant dans {Math.Max(1, left)} s";
            if (SkipText.Text != label) SkipText.Text = label;
        }
        if (counting && SkipCancel.Visibility != Visibility.Visible) Motion.Reveal(SkipCancel, 200, 8);
        else if (!counting && SkipCancel.Visibility == Visibility.Visible && SkipCancel.IsHitTestVisible) _ = Motion.HideAsync(SkipCancel, 140);
        SetSkipVisible(_activeSkip is not null && (_controlsVisible || counting || (!_paused && DateTimeOffset.UtcNow - _skipSince < SkipLinger)));
        // Sits just above the control bar while it is shown, near the bottom edge otherwise.
        var lift = _controlsVisible ? -(PlayerControls.Height - 14) : 0;
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
        if (_activeSkip is not { } segment || _mpv is null || !_loaded) return;
        if (_skipToNext && _nextItem is { } next) { await PlayAsync(next, _miniPlayer, completePrevious: true); return; }
        if (!TryCommand("seek", segment.End.ToString(CultureInfo.InvariantCulture), "absolute+exact")) return;
        HoldPosition(segment.End); SetSkipVisible(false);
    }
}
