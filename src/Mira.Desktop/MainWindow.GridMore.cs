using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// The grid goes on by itself: the next page of titles is asked for as its end comes within a screen of the window,
/// with placeholder cards while it comes and « Réessayer » if Jellyfin does not answer. A page that brings nothing new
/// (the library changed under the offsets) stops it until the list is asked for again, rather than asking in a loop.
/// The page that comes is added a few cards at each frame (<see cref="GrowGrid"/>), never all at once under a list that scrolls.
/// </summary>
public partial class MainWindow
{
    private bool _loadingMore, _moreFailed, _moreExhausted, _moreScheduled, _listRefreshing;
    /// <summary>The grid's next page being added, from <see cref="GridGrowth.Next"/> on; null when none is.</summary>
    private GridGrowth? _growth;
    private sealed class GridGrowth(List<MediaItem> items, int next, bool found)
    {
        public List<MediaItem> Items { get; } = items;
        public int Next { get; set; } = next;
        public bool Found { get; } = found;
        public int Made { get; set; }
    }
    /// <summary>The cards added at each frame: as many as are made in this time (at least one), a row at most, laid out in the same frame.</summary>
    private const double GrowBudgetMs = 1;

    /// <summary>The grid has more titles on Jellyfin than on screen, and nothing else is being asked for it.</summary>
    private bool GridHasMore => !_demo && _catalog.View != "offline" && !_catalog.Loading && PosterCards.Visibility == Visibility.Visible
        && _catalog.Items.Count > 0 && _catalog.Items.Count < _catalog.TotalCount;

    /// <summary>What sits under the grid: placeholders while the next page comes, the retry after a failure, else nothing.</summary>
    private void UpdateGridMore()
    {
        var more = GridHasMore;
        ShowMoreSkeleton(more && _loadingMore);
        MoreError.Visibility = more && _moreFailed && !_loadingMore ? Visibility.Visible : Visibility.Collapsed;
        ScheduleMoreCheck();
    }

    /// <summary>After layout: the grid may now be short of the window's bottom (a first page on a tall screen, a resize).</summary>
    private void ScheduleMoreCheck()
    {
        if (_moreScheduled) return; _moreScheduled = true;
        Dispatcher.BeginInvoke(() => { _moreScheduled = false; LoadMoreIfNear(); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void LoadMoreIfNear()
    {
        // A page still being added: the grid's end is not yet where it will be (asked again once it is, GrowGrid).
        if (_listRefreshing || _loadingMore || _growth is not null || _moreFailed || _moreExhausted || !GridHasMore || !PosterCards.IsVisible || LibraryScroll.ActualHeight <= 0) return;
        if (!PosterCards.IsDescendantOf(LibraryScroll)) return;
        var bottom = PosterCards.TransformToAncestor(LibraryScroll).Transform(new Point(0, PosterCards.ActualHeight)).Y;
        // Within a screen below the window: the next page is there before the reader reaches the end.
        if (bottom > LibraryScroll.ActualHeight * 2) return;
        _ = LoadMoreAsync();
    }

    private async Task LoadMoreAsync()
    {
        var before = _catalog.Items.Count;
        _loadingMore = true; _moreFailed = false; UpdateGridMore();
        var failed = await RefreshAsync(more: true);
        // Another list was asked for meanwhile: it set its own state.
        if (!_loadingMore) return;
        _loadingMore = false; _moreFailed = failed;
        if (!failed && _catalog.Items.Count == before) _moreExhausted = true;
        UpdateGridMore();
    }

    /// <summary>A new list (another page, filter, search, account): the grid may go on again.</summary>
    private void ResetGridMore() { _loadingMore = false; _moreFailed = false; _moreExhausted = false; }

    private void MoreRetry_Click(object sender, RoutedEventArgs e) { _moreFailed = false; _moreExhausted = false; _ = LoadMoreAsync(); }

    private void StartGrowingGrid(List<MediaItem> items, int next, bool found)
    {
        _growth = new GridGrowth(items, next, found);
        CompositionTarget.Rendering += GrowGrid;
    }
    private void StopGrowingGrid()
    {
        if (_growth is null) return;
        _growth = null; CompositionTarget.Rendering -= GrowGrid;
    }
    /// <summary>
    /// The grid's next page, added as the screen refreshes: at each frame, the cards made in about a millisecond (a row
    /// at most, which the same frame lays out). Made all at once, the 60 cards of a page held the interface's thread some
    /// 13 ms, and their layout about as long in the next frame: two stalls of three refreshes at 240 Hz at each page,
    /// while the list moved at full speed. The page is asked for with the grid's end a screen below the window: it is in
    /// place long before the reader gets there (some ten frames). Another list, or the same one drawn again, stops it (AddCards).
    /// </summary>
    private void GrowGrid(object? sender, EventArgs e)
    {
        if (_growth is not { } growth || !ReferenceEquals(growth.Items, _catalog.Items) || PosterCards.Children.Count != growth.Next) { StopGrowingGrid(); return; }
        var started = Stopwatch.GetTimestamp(); var made = 0;
        var available = PosterCards.ActualWidth;
        var (columns, width) = available >= 200 ? PosterLayout(available) : (8, double.NaN);
        for (var added = 0; growth.Next < growth.Items.Count && added < columns; added++)
        {
            var item = growth.Items[growth.Next];
            var card = CardFor(item, MediaCard.Key(item, _settings), false, growth.Found, growth.Next, ref made);
            if (!double.IsNaN(width)) card.Resize(width);
            PosterCards.Children.Add(card); growth.Next++;
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= GrowBudgetMs) break;
        }
        growth.Made += made;
        if (growth.Next >= growth.Items.Count)
        {
            StopGrowingGrid();
            if (growth.Made > 0) FrameProbe.Mark($"{growth.Made} new card{(growth.Made > 1 ? "s" : "")} in {PosterCards.Name}, over frames");
            // The grid's end is now where it stays: the next page, if it is near the window again.
            ScheduleMoreCheck();
        }
        RefreshHover(); ScheduleVisibleArtwork();
    }

    /// <summary>One row of placeholders shaped like the grid's cards, under its last row.</summary>
    private void ShowMoreSkeleton(bool visible)
    {
        if (!visible) { if (MoreSkeleton.Visibility == Visibility.Visible) { Motion.Pulse(MoreSkeleton, false); MoreSkeleton.Visibility = Visibility.Collapsed; } return; }
        if (MoreSkeleton.Visibility == Visibility.Visible) return;
        var (columns, width) = PosterLayout(PosterCards.ActualWidth < 200 ? Math.Max(600, LibraryRows.ActualWidth) : PosterCards.ActualWidth);
        MoreSkeleton.Children.Clear();
        for (var i = 0; i < columns; i++) MoreSkeleton.Children.Add(SkeletonCard(width));
        MoreSkeleton.Visibility = Visibility.Visible; Motion.Pulse(MoreSkeleton, true);
    }
}
