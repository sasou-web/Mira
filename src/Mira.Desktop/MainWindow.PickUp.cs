using System.Windows;
using System.Windows.Controls;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;
using static Mira.Core.Lang;

namespace Mira.Desktop;

/// <summary>
/// What sets Films and Séries apart from a plain grid: their genres as chips under the tools, and on top the title to
/// pick up (Catalog.PickUp), the film last started or the episode a show is at.
/// </summary>
public partial class MainWindow
{
    private MediaItem? _pickUp;
    private void RenderPickUp(string search)
    {
        var item = _catalog.Loading && _catalog.Items.Count == 0 ? null : _catalog.PickUp(search, CurrentQuery());
        PickUp.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (item is null || item.Id == _pickUp?.Id && item.UserData.PlaybackPositionTicks == _pickUp.UserData.PlaybackPositionTicks) { _pickUp = item; return; }
        var image = item.Id != _pickUp?.Id; _pickUp = item;
        PickUpLabel.Text = Catalog.PickUpLabel(item);
        PickUpTitle.Text = item.Type == "Episode" ? item.SeriesName ?? item.Name : item.Name;
        PickUpLine.Text = Catalog.PickUpLine(item);
        PickUpPlay.Content = ActionLabel("play", item.Progress > 0 ? L("Reprendre", "Resume") : L("Regarder", "Watch"));
        if (image) { PickUpImage.ImageSource = null; _ = LoadPickUpImageAsync(item); }
    }
    private async Task LoadPickUpImageAsync(MediaItem item)
    {
        if (_images is not { } cache) return;
        var image = await cache.GetAsync(item, true);
        if (_pickUp?.Id == item.Id && image is not null) { PickUpImage.ImageSource = image; Motion.Fade(PickUp, 1, 200, 0.85, Motion.Soft); }
    }
    private void PickUpBody_SizeChanged(object sender, SizeChangedEventArgs e) => PickUpBody.Clip = new System.Windows.Media.RectangleGeometry(new Rect(e.NewSize), 16, 16);
    private async void PickUpPlay_Click(object sender, RoutedEventArgs e) { if (_pickUp is { } item) await PlayAsync(item, chosen: true); }
    private async void PickUpDetails_Click(object sender, RoutedEventArgs e) { if (_pickUp is { } item) await ShowDetailsAsync(await PageItemAsync(item)); }
    /// <summary>The genres of Films and of Séries, grouped across languages, most frequent first (read once per connection).</summary>
    private readonly Dictionary<string, IReadOnlyList<GenreGroup>> _pageGenres = [];
    private readonly HashSet<string> _pageGenresAsked = [];
    /// <summary>Chips shown before « Plus de genres », which opens the filters with every genre.</summary>
    private const int ChipGenres = 8;
    private async Task LoadPageGenresAsync(string type)
    {
        if (!_pageGenresAsked.Add(type)) return;
        try
        {
            var titles = _demo || _client is null ? (_demoItems ?? DemoLibrary.Items()).Where(x => x.Type == type).Select(x => x.Genres).ToList()
                : await _client.TitleGenresAsync(type, _connectionLifetime.Token);
            _pageGenres[type] = GenreGroups.FromTitles(titles);
            RenderGenreChips();
        }
        catch (Exception ex) when (ExpectedErrors.Are(ex)) { _pageGenresAsked.Remove(type); }
    }
    /// <summary>« Tous », then the most frequent genres of the page, the one chosen lit like the rail's page.</summary>
    private void RenderGenreChips()
    {
        var shown = _catalog.View is "Movie" or "Series" && _catalog.Person is null && _catalog.Studio is null && _catalog.Saga is null;
        if (shown && !_pageGenres.ContainsKey(_catalog.View)) { _ = LoadPageGenresAsync(_catalog.View); shown = false; }
        shown = shown && _pageGenres[_catalog.View].Count > 1;
        GenreChipsScroll.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown) return;
        var chosen = Choice(GenreFilter);
        var all = _pageGenres[_catalog.View];
        var genres = all.Take(ChipGenres).Select(x => (Tag: x.Value, Name: x.Label)).ToList();
        // A genre chosen further down (filters, a title page) keeps a chip of its own.
        if (chosen.Length > 0 && genres.All(x => x.Tag != chosen) && all.FirstOrDefault(x => x.Value == chosen) is { } picked) genres.Add((picked.Value, picked.Label));
        genres.Insert(0, ("", L("Tous", "All")));
        if (all.Count > genres.Count - 1) genres.Add(("…", L("Plus de genres", "More genres")));
        if (!GenreChips.Children.OfType<Button>().Select(x => x.Tag as string).SequenceEqual(genres.Select(x => x.Tag)))
        {
            GenreChips.Children.Clear();
            foreach (var (tag, name) in genres)
            {
                var chip = new Button { Content = name, Tag = tag, Style = (Style)FindResource("Quiet"), Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0), FontSize = 13 };
                Motion.SetRadius(chip, new CornerRadius(16));
                if (tag == "…") { chip.Foreground = Palette.Get("TextSecondary"); chip.Click += (_, _) => OpenGenreFilter(); }
                else chip.Click += (_, _) => ChooseGenre(tag, name);
                GenreChips.Children.Add(chip);
            }
        }
        foreach (var chip in GenreChips.Children.OfType<Button>())
        {
            if ((chip.Tag as string) == "…") continue;
            var on = (chip.Tag as string) == chosen;
            if (on) { chip.Background = Palette.Get("Text"); chip.Foreground = Palette.Get("OnAccent"); } else { chip.ClearValue(BackgroundProperty); chip.ClearValue(ForegroundProperty); }
            System.Windows.Automation.AutomationProperties.SetName(chip, on ? L($"{chip.Content}, affiché", $"{chip.Content}, shown") : chip.Content as string);
        }
    }
    /// <summary>A genre group as the Genre filter's choice (added to it when Jellyfin's filters list it under another name).</summary>
    private void ChooseGenre(string value, string label)
    {
        if (GenreFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (x.Tag?.ToString() ?? "") == value) is not { } item)
        { _filterBusy = true; GenreFilter.Items.Add(item = new ComboBoxItem { Content = label, Tag = value }); _filterBusy = false; }
        GenreFilter.SelectedItem = item;
    }
    /// <summary>« Plus de genres »: the filters open, their Genre list dropped down.</summary>
    private void OpenGenreFilter()
    {
        if (!_filtersOpen) FiltersToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, FiltersToggle));
        Dispatcher.BeginInvoke(() => { GenreFilter.Focus(); GenreFilter.IsDropDownOpen = true; }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
