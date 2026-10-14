using System.Windows;
using System.Windows.Controls;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// « Accueil à ta main » (<see cref="HomeLayout"/>, as on Mac and in Mira web): the home page's rows in the order chosen
/// in Réglages → Apparence, each shown or not. Continuer à regarder, Tes favoris and « Nouveau dans … » (a library's
/// latest titles) are rows; « Ajoutés récemment » is the grid that goes on with « Afficher plus », so it stays below them.
/// </summary>
public partial class MainWindow
{
    /// <summary>The account's libraries, for their « Nouveau dans … ».</summary>
    private List<LibrarySection> _homeLibraries = [];
    private readonly Dictionary<string, (StackPanel Section, TextBlock Title, ScrollViewer Scroll, StackPanel Cards, FrameworkElement Arrows)> _shelves = [];

    private List<HomeLayout.Row> HomeRows() => HomeLayout.Rows(_settings.HomeRows, _homeLibraries);
    private bool HomeShows(string key) => HomeRows().FirstOrDefault(x => x.Key == key)?.Shown != false;

    /// <summary>The rows Jellyfin fills beside Continuer à regarder: the favourites, a library's latest titles.</summary>
    private async Task<Dictionary<string, List<MediaItem>>> LoadShelvesAsync(JellyfinClient client, CancellationToken ct)
    {
        if (_homeLibraries.Count == 0)
        {
            try { _homeLibraries = await client.LibrariesAsync(ct); }
            catch (Exception ex) when (IsExpected(ex) && !ct.IsCancellationRequested) { }
        }
        var rows = HomeRows().Where(x => x.Shown && (x.Key == HomeLayout.Favorites || x.LibraryId is not null)).ToList();
        var lists = await Task.WhenAll(rows.Select(async row =>
        {
            try { return (await (row.LibraryId is { } library ? client.BrowseAsync(parentId: library, limit: 30, ct: ct) : client.BrowseAsync(favorites: true, limit: 30, ct: ct))).Items; }
            // A row that does not come leaves the others.
            catch (Exception ex) when (IsExpected(ex) && !ct.IsCancellationRequested) { return null; }
        }));
        var shelves = new Dictionary<string, List<MediaItem>>();
        for (var i = 0; i < rows.Count; i++) if (lists[i] is { } items) shelves[rows[i].Key] = items;
        return shelves;
    }

    /// <summary>The rows above the grid, in their order: Continuer à regarder among them.</summary>
    private void RenderShelves(bool home)
    {
        var order = new List<UIElement>();
        var shown = home ? HomeRows().Where(x => x.Shown && x.Key != HomeLayout.Latest).ToList() : [];
        foreach (var row in shown)
        {
            if (row.Key == HomeLayout.Continue) { order.Add(ResumeSection); continue; }
            var items = _catalog.Shelves.GetValueOrDefault(row.Key) ?? [];
            if (!_shelves.TryGetValue(row.Key, out var shelf)) _shelves[row.Key] = shelf = Shelf();
            shelf.Title.Text = row.Title;
            shelf.Section.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AddCards(shelf.Cards, items, false);
            var width = _settings.PosterDensity == "Compact" ? 161 : 192;
            foreach (var card in shelf.Cards.Children.OfType<MediaCard>()) card.Resize(width);
            order.Add(shelf.Section);
        }
        if (!order.Contains(ResumeSection)) { ResumeSection.Visibility = Visibility.Collapsed; order.Insert(0, ResumeSection); }
        if (HomeShelves.Children.Cast<UIElement>().SequenceEqual(order)) return;
        HomeShelves.Children.Clear();
        foreach (var part in order) HomeShelves.Children.Add(part);
        foreach (var key in _shelves.Keys.Where(key => !order.Contains(_shelves[key].Section)).ToList()) _shelves.Remove(key);
    }
    /// <summary>A row of posters as Continuer à regarder is drawn: its title, its arrows when it goes past the window.</summary>
    private (StackPanel Section, TextBlock Title, ScrollViewer Scroll, StackPanel Cards, FrameworkElement Arrows) Shelf()
    {
        var cards = new StackPanel { Orientation = Orientation.Horizontal };
        Accessible.SetArrowKeys(cards, true);
        var scroll = new ScrollViewer { Style = (Style)FindResource(typeof(ScrollViewer)), HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = cards };
        var title = new TextBlock { FontSize = 24, FontWeight = FontWeights.Normal, Foreground = Palette.Get("TextTertiary"), VerticalAlignment = VerticalAlignment.Center };
        Button Arrow(string kind, string tip, double by)
        {
            var button = new Button { Style = (Style)FindResource("IconButton"), ToolTip = tip, Content = new Icon { Kind = kind, Width = 17, Height = 17 } };
            System.Windows.Automation.AutomationProperties.SetName(button, tip);
            button.Click += (_, _) => SmoothScroll.By(scroll, by, true);
            return button;
        }
        var previous = Arrow("back", "Titres précédents", -624); var next = Arrow("next", "Titres suivants", 624);
        var arrows = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        arrows.Children.Add(previous); arrows.Children.Add(next);
        DockPanel.SetDock(arrows, Dock.Right);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(arrows); header.Children.Add(title);
        scroll.ScrollChanged += (_, _) =>
        {
            arrows.Visibility = scroll.ScrollableWidth > 1 ? Visibility.Visible : Visibility.Collapsed;
            previous.IsEnabled = scroll.HorizontalOffset > 1; next.IsEnabled = scroll.HorizontalOffset < scroll.ScrollableWidth - 1;
        };
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 30) };
        section.Children.Add(header); section.Children.Add(scroll);
        return (section, title, scroll, cards, arrows);
    }

    // —— Réglages → Apparence ——
    /// <summary>
    /// Each row with its box and two arrows, applied at once. « Ajoutés récemment » is listed last, as the page puts it:
    /// a move never goes past it, so that each press shows.
    /// </summary>
    private void ShowHomeRowsSetting(string? focus = null)
    {
        var rows = HomeRows();
        var latest = rows.First(x => x.Key == HomeLayout.Latest);
        rows = [.. rows.Where(x => x.Key != HomeLayout.Latest), latest];
        HomeRowsList.Children.Clear();
        UIElement? focused = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var grid = row.Key == HomeLayout.Latest;
            var check = new CheckBox { IsChecked = row.Shown, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(check, "Afficher « " + RowTitle(row) + " »");
            check.Click += (_, _) => ApplyHomeRows(HomeLayout.Show(Ordered(rows), _homeLibraries, row.Key, check.IsChecked == true), row.Key + "check");
            var line = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var tools = new StackPanel { Orientation = Orientation.Horizontal };
            if (!grid)
            {
                var index = i;
                Button Arrow(string kind, string verb, int step)
                {
                    var button = new Button { Style = (Style)FindResource("IconButton"), Width = 34, Height = 34, ToolTip = $"{verb} « {row.Title} »", Content = new Icon { Kind = kind, Width = 15, Height = 15 } };
                    System.Windows.Automation.AutomationProperties.SetName(button, $"{verb} « {row.Title} »");
                    // The arrow pressed keeps the focus, or the other one once the row reaches an end.
                    var lands = step < 0 ? (index == 1 ? "down" : "up") : (index == rows.Count - 3 ? "up" : "down");
                    button.Click += (_, _) => ApplyHomeRows(HomeLayout.Move(Ordered(rows), _homeLibraries, row.Key, step), row.Key + lands);
                    button.IsEnabled = step < 0 ? index > 0 : index < rows.Count - 2;
                    if (focus == row.Key + kind) focused = button;
                    return button;
                }
                tools.Children.Add(Arrow("up", "Monter", -1)); tools.Children.Add(Arrow("down", "Descendre", 1));
            }
            tools.Children.Add(check);
            if (focus == row.Key + "check") focused = check;
            DockPanel.SetDock(tools, Dock.Right); line.Children.Add(tools);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = RowTitle(row) });
            text.Children.Add(new TextBlock { Text = HomeLayout.Detail(row), Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 2, 0, 0) });
            line.Children.Add(text);
            HomeRowsList.Children.Add(line);
        }
        focused?.Focus();
        static string RowTitle(HomeLayout.Row row) => row.Key == HomeLayout.Latest ? "Ajoutés récemment" : row.Title;
        static string Ordered(List<HomeLayout.Row> rows) => HomeLayout.Save(rows);
    }
    private void ApplyHomeRows(string value, string? focus = null)
    {
        _settings.HomeRows = value;
        if (!TrySaveSettings()) SetNotice(SettingsNotSaved);
        ShowHomeRowsSetting(focus);
        // A row newly shown comes from Jellyfin; the others move at once.
        RenderLibrary();
        if (_catalog.View == "home" && !_demo) _ = RefreshAsync(quiet: true);
    }
    private void HomeRowsReset_Click(object sender, RoutedEventArgs e) => ApplyHomeRows("");
}
