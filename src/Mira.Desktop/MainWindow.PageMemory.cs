using System.Windows;
using System.Windows.Controls;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>The rail's pages kept as they were left (<see cref="PageMemory"/>): scroll, filters, search and titles.</summary>
public partial class MainWindow
{
    private readonly PageMemory _pages = new();
    /// <summary>The library under nothing else: no title page, Réglages, Téléchargements or Guide over it.</summary>
    private bool LibraryOnScreen => !new UIElement[] { DetailOverlay, SettingsOverlay, TorLinkOverlay, GuideOverlay }.Any(x => x.Visibility == Visibility.Visible && x.IsHitTestVisible);
    /// <summary>
    /// A page of the rail (<paramref name="view"/>, one library for « library » with <paramref name="parentId"/>): the one
    /// on screen kept first, then the chosen one as it was left, or afresh when its button is clicked again while shown.
    /// </summary>
    private async Task GoToPageAsync(string view, string? parentId = null, bool keepSearch = false)
    {
        var from = PageMemory.KeyOf(_catalog); var onScreen = LibraryOnScreen;
        KeepPage();
        if (!keepSearch) { ResetFilters(); _filtersOpen = false; }
        _catalog.View = view;
        if (view == "library")
        {
            _filterBusy = true; _catalog.ParentId = parentId;
            LibraryFilter.SelectedItem = LibraryFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Tag?.ToString() == (parentId ?? "")); _filterBusy = false;
        }
        var key = PageMemory.KeyOf(_catalog);
        var recalled = _pages.Recall(key, again: onScreen && key == from);
        if (recalled is not null) Restore(recalled);
        await NavigateLibraryAsync(recalled);
    }
    /// <summary>The page on screen as it is, before another one replaces it.</summary>
    private void KeepPage()
    {
        if (LoginOverlay.Visibility == Visibility.Visible) return;
        var genre = GenreFilter.SelectedItem as ComboBoxItem;
        _pages.Keep(PageMemory.KeyOf(_catalog), new(Choice(GenreFilter), genre?.Content?.ToString() ?? "", Choice(YearFilter), Choice(WatchedFilter), Choice(SortFilter), Choice(LibraryFilter),
            _catalog.Favorites, _filtersOpen, SearchBox.Text, _catalog.Items, _catalog.TotalCount, LibraryScroll.VerticalOffset), _catalog.Loading);
    }
    private void Restore(PageMemory.State state)
    {
        _filterBusy = true;
        // A genre chosen from a title page may not be one of the list's: put back as it was then.
        if (state.Genre.Length > 0 && !GenreFilter.Items.OfType<ComboBoxItem>().Any(x => x.Tag?.ToString() == state.Genre))
            GenreFilter.Items.Add(new ComboBoxItem { Content = state.GenreLabel, Tag = state.Genre });
        Pick(GenreFilter, state.Genre); Pick(YearFilter, state.Year); Pick(WatchedFilter, state.Watched); Pick(SortFilter, state.Sort); Pick(LibraryFilter, state.Library);
        _catalog.ParentId = state.Library.Length > 0 ? state.Library : null;
        _catalog.Favorites = state.Favorites; _filtersOpen = state.FiltersOpen; SearchBox.Text = state.Search;
        _filterBusy = false; ShowFavoritesFilter();
        static void Pick(ComboBox box, string tag) => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Tag?.ToString() == tag) ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }
    /// <summary>Back where the list was scrolled to, once its cards are laid out.</summary>
    private void ScrollBack(double offset)
    {
        LibraryScroll.UpdateLayout();
        SmoothScroll.Jump(LibraryScroll, Math.Min(offset, LibraryScroll.ScrollableHeight));
    }
}
