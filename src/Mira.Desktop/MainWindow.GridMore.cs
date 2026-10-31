using System.Windows;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// The grid goes on by itself: the next page of titles is asked for as its end comes within a screen of the window,
/// with placeholder cards while it comes and « Réessayer » if Jellyfin does not answer. A page that brings nothing new
/// (the library changed under the offsets) stops it until the list is asked for again, rather than asking in a loop.
/// </summary>
public partial class MainWindow
{
    private bool _loadingMore, _moreFailed, _moreExhausted, _moreScheduled;

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
        if (_loadingMore || _moreFailed || _moreExhausted || !GridHasMore || !PosterCards.IsVisible || LibraryScroll.ActualHeight <= 0) return;
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
