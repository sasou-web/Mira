using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// A collection's page, « L'étagère » as on Windows (MainWindow.Shelf.cs there; CollectionShelf of Core): its picture,
/// name and facts, « Reprendre » across the whole collection, Favori, Vu, « ⋯ », a bar with one notch per title, then its
/// titles numbered in their watch order (or by release date), « À suivre » on the one it resumes at; each one opens its page.
/// </summary>
public sealed class ShelfPage : Page
{
    private MediaItem _box;
    private List<MediaItem> _titles;
    private IReadOnlyList<string>? _order;
    private bool _byRelease, _editing, _loaded;
    /// <summary>What « Reprendre » plays: an episode of the series under way, or a film.</summary>
    private MediaItem? _target;
    private int _version;

    private readonly Image _backdrop = new() { Stretch = Stretch.UniformToFill, Opacity = 0 };
    private readonly Grid _header = new() { ClipToBounds = true };
    private readonly StackPanel _top = new() { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(56 + MainWindow.RailWidth, 0, 56, 40) };
    private readonly TextBlock _title = new() { FontSize = 44, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _facts = new() { FontSize = 15, Foreground = Ui.Muted, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly ActionLine _actions = new() { Margin = new Thickness(0, 22, 0, 0) };
    private readonly Button _play = new() { Classes = { "primary" }, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, CornerRadius = ActionLine.Corners, Height = ActionLine.Round, Padding = new Thickness(25, 0) };
    private readonly Button _favorite, _watched, _more;
    /// <summary>One notch per title, in the watch order.</summary>
    private readonly Grid _steps = new() { Height = 4, Margin = new Thickness(0, 22, 0, 0), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _body = new() { Spacing = 18, Margin = new Thickness(56 + MainWindow.RailWidth, 12, 56, 64) };
    private readonly TextBlock _heading = new() { FontSize = 22, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _watchOrder = new(), _releaseOrder = new();
    private readonly Segmented _orderChoice;
    private readonly Border _orderBox;
    private readonly Button _done;
    private readonly DockPanel _head = new();
    private readonly CardGrid _grid = new() { MinItemWidth = 168 };
    private readonly StackPanel _rows = new() { Spacing = 6, IsVisible = false };
    private readonly EmptyState _state = new() { Margin = new Thickness(0, 40, 0, 40) };
    private double _viewport;

    public override string Rail => "";
    public MediaItem Box => _box;
    /// <summary>For the self-check: the titles shown, and the state said instead of them.</summary>
    public int TitlesShown => _grid.Children.Count;
    public EmptyState State => _state;

    public ShelfPage(MainWindow shell, MediaItem box) : base(shell, underRail: true)
    {
        _box = box; _titles = box.Shelf?.ToList() ?? [];
        _backdrop.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(380) }];
        _header.Children.Add(new Border { Background = Ui.Brush("#0F0F0F") });
        _header.Children.Add(_backdrop);
        // Shaded from the left and the bottom, where the text and the buttons sit, as a title's page.
        _header.Children.Add(new Border { Background = Gradient(0, 0.5, 1, 0.5, ("#F0000000", 0), ("#C4000000", 0.3), ("#40000000", 0.62), ("#00000000", 0.86)) });
        _header.Children.Add(new Border { Background = Gradient(0.5, 0, 0.5, 1, ("#66000000", 0), ("#00000000", 0.2), ("#00000000", 0.5), ("#A6000000", 0.8), ("#FF000000", 1)) });
        var back = Ui.IconButton("back", L("Retour · Échap", "Back · Esc"), () => Shell.Back(), "icon", 24);
        back.HorizontalAlignment = HorizontalAlignment.Left; back.VerticalAlignment = VerticalAlignment.Top; back.Margin = new Thickness(44 + MainWindow.RailWidth, 30 + shell.TitleBarInset, 0, 0);
        back.Width = back.Height = 46;

        _top.Children.Add(new TextBlock { Text = L("COLLECTION", "COLLECTION"), FontSize = 12.5, FontWeight = FontWeight.Bold, Foreground = Ui.Muted, LetterSpacing = 1.2, Margin = new Thickness(0, 0, 0, 8) });
        _top.Children.Add(_title); _top.Children.Add(_facts);
        _play.Click += (_, _) => _ = PlayAsync();
        _actions.Children.Add(_play);
        _favorite = StateButton("favorite", () => _ = FlipFavoriteAsync()); _actions.Children.Add(_favorite);
        _watched = StateButton("watched", () => _ = FlipWatchedAsync()); _actions.Children.Add(_watched);
        Button? more = null;
        more = _more = Ui.IconButton("more", L("Plus d’actions sur cette collection", "More actions for this collection"), () => MoreMenu(more!), "state", 18, L("Plus d’actions sur cette collection", "More actions for this collection"));
        _more.VerticalAlignment = VerticalAlignment.Center;
        _actions.Children.Add(_more);
        _top.Children.Add(_actions); _top.Children.Add(_steps);
        _header.Children.Add(_top); _header.Children.Add(back);

        // Ordre de visionnage | Date de sortie, as on Windows; « Terminé » while the order changes.
        foreach (var (button, words) in new[] { (_watchOrder, L("Ordre de visionnage", "Watch order")), (_releaseOrder, L("Date de sortie", "Release date")) })
        {
            button.Content = words; button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.Padding = new Thickness(14, 7); button.FontSize = 13.5; button.FontWeight = FontWeight.SemiBold; button.CornerRadius = new CornerRadius(9);
            button.BorderThickness = new Thickness(1);
        }
        _orderChoice = new Segmented(_watchOrder, _releaseOrder);
        _watchOrder.Click += (_, _) => ShowOrder(byRelease: false);
        _releaseOrder.Click += (_, _) => ShowOrder(byRelease: true);
        _orderBox = new Border { Background = Ui.Brush("#161616"), CornerRadius = new CornerRadius(12), Padding = new Thickness(3), Child = _orderChoice, VerticalAlignment = VerticalAlignment.Center };
        _done = Ui.Action(L("Terminé", "Done"), "check", "primary", () => _ = FinishEditingAsync());
        _done.IsVisible = false; _done.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_orderBox, Dock.Right); DockPanel.SetDock(_done, Dock.Right);
        _head.Children.Add(_done); _head.Children.Add(_orderBox); _head.Children.Add(_heading);
        _body.Children.Add(_head); _body.Children.Add(_state); _body.Children.Add(_grid); _body.Children.Add(_rows);

        var page = new StackPanel();
        page.Children.Add(_header); page.Children.Add(_body);
        Content = new ScrollViewer { Content = page };
        SizeChanged += (_, e) => { _viewport = e.NewSize.Height; SizeHero(); };
        _top.SizeChanged += (_, e) => { if (e.HeightChanged) SizeHero(); };
        _title.Text = box.DisplayTitle;
        ShowFavorite(box.UserData.IsFavorite);
        Render(loading: _titles.Count == 0);
    }
    private static LinearGradientBrush Gradient(double x1, double y1, double x2, double y2, params (string Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new RelativePoint(x1, y1, RelativeUnit.Relative), EndPoint = new RelativePoint(x2, y2, RelativeUnit.Relative) };
        foreach (var (color, offset) in stops) brush.GradientStops.Add(new GradientStop(Color.Parse(color), offset));
        return brush;
    }
    /// <summary>The picture over half the window, never shorter than what sits on it (a 960×600 window keeps the titles in view).</summary>
    private void SizeHero()
    {
        var view = _viewport > 0 ? _viewport : 760;
        _header.Height = Math.Max(Math.Max(360, view * .6), _top.Bounds.Height + _top.Margin.Bottom + 100 + Shell.TitleBarInset);
    }
    private static Button StateButton(string kind, Action click)
    {
        var button = new Button { Content = new StateIcon(kind) { VerticalAlignment = VerticalAlignment.Center }, Classes = { "state" }, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => click();
        return button;
    }
    private static void ShowState(Button button, string kind, bool on, string name, string tip)
    {
        if (button.Content is StateIcon glyph) glyph.Show(kind, on);
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, name);
        Avalonia.Automation.AutomationProperties.SetHelpText(button, tip != name ? tip : null);
    }

    public override async Task LoadAsync() => await LoadTitlesAsync(++_version);
    /// <summary>After a playback or a change in Mira: the titles' marks read again, the page staying where it is.</summary>
    public override async Task RefreshAsync() { if (!_editing) await LoadTitlesAsync(++_version); }
    /// <summary>The collection's titles from Jellyfin (nested collections flattened), this account's order, then the page drawn again.</summary>
    private async Task LoadTitlesAsync(int version)
    {
        if (Shell.Session is not { } session) { Render(loading: false); return; }
        try
        {
            var titles = session.Client.CollectionTitlesAsync(_box.Id, session.Lifetime);
            var choices = session.Client.ShelfChoicesAsync(session.Lifetime);
            var read = await titles;
            await Task.Run(() => Errors.TryCache(() => session.Store.ApplyLocalProgress(read)));
            var chosen = await choices;
            if (version != _version) return;
            _titles = read; _order = chosen.GetValueOrDefault(_box.Id)?.Order; _loaded = true;
            Render(loading: false);
            await LoadTargetAsync(session, version);
        }
        catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (Errors.Expected(ex))
        {
            if (version != _version) return;
            Shell.NotAnswered(ex);
            // The titles of the card, if it had them, still show: only their order and progress may be older.
            if (_titles.Count > 0) { Render(loading: false); Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
            else ShowProblem(Errors.Friendly(ex));
        }
    }
    /// <summary>The titles in the watch order: this account's, the titles added since after it by release date.</summary>
    private List<MediaItem> Ordered() => CollectionShelf.InOrder(_titles, _order);
    private void Render(bool loading)
    {
        var ordered = Ordered();
        var shown = _byRelease && !_editing ? CollectionShelf.ByRelease(_titles) : ordered;
        var up = CollectionShelf.UpNext(ordered) is int at ? ordered[at] : null;
        _facts.Text = _titles.Count > 0 ? CollectionShelf.Facts(_titles) : loading ? L("Chargement…", "Loading…") : "";
        _heading.Text = CollectionShelf.TitlesHeading(_titles.Count);
        _head.IsVisible = _titles.Count > 0;
        _orderBox.IsVisible = !_editing && _titles.Count > 1;
        _done.IsVisible = _editing;
        _actions.IsVisible = true;
        ShowWatched(_titles);
        RenderSteps(ordered);
        ShowPlayLabel(up, ordered);
        _grid.Children.Clear(); _rows.Children.Clear();
        _grid.IsVisible = !_editing; _rows.IsVisible = _editing;
        if (loading) { _state.Hide(); _grid.Skeleton(1); return; }
        if (_titles.Count == 0)
        {
            _state.Show("library", L("Rien dans cette collection", "Nothing in this collection yet"),
                L("Ajoute des films ou des séries depuis leur menu : « Ajouter à une collection… ».", "Add movies or shows from their menu: “Add to collection…”."));
            return;
        }
        _state.Hide();
        if (_editing) { RenderEditing(ordered); return; }
        var images = Shell.Session?.Images;
        foreach (var title in shown)
        {
            // Numbered in the watch order, whichever order the page shows.
            var place = ordered.IndexOf(title);
            _grid.Children.Add(Cards.Poster(title, images, Shell.Open, Shell.Menu, CardGrid.Pixels, CollectionShelf.Number(place), CollectionShelf.TitleLine(title), upNext: ReferenceEquals(title, up)));
        }
    }
    private void ShowProblem(string why)
    {
        _grid.Children.Clear(); _rows.Children.Clear(); _head.IsVisible = false; _steps.IsVisible = false;
        _facts.Text = ""; _actions.IsVisible = false;
        _state.Show("warning", L("Jellyfin ne répond pas", "Jellyfin isn’t answering"), why, L("Réessayer", "Try again"), () => { var version = ++_version; Render(loading: true); _ = LoadTitlesAsync(version); });
    }
    /// <summary>One notch per title in the watch order: full once watched, partly while under way.</summary>
    private void RenderSteps(List<MediaItem> ordered)
    {
        _steps.Children.Clear(); _steps.ColumnDefinitions.Clear();
        _steps.IsVisible = ordered.Count > 1;
        for (var i = 0; i < ordered.Count; i++)
        {
            var title = ordered[i]; var share = CollectionShelf.Progress(title);
            _steps.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var notch = new Grid { Margin = new Thickness(0, 0, i < ordered.Count - 1 ? 3 : 0, 0) };
            ToolTip.SetTip(notch, title.DisplayTitle);
            notch.Children.Add(new Border { Background = Ui.LineStrong, CornerRadius = new CornerRadius(2) });
            if (share > 0)
            {
                var fill = new Border { Background = Ui.Ink, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
                notch.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * share;
                notch.Children.Add(fill);
            }
            Grid.SetColumn(notch, i);
            _steps.Children.Add(notch);
        }
        Avalonia.Automation.AutomationProperties.SetName(_steps, L($"{ordered.Count(CollectionShelf.Watched)} sur {ordered.Count} vus", $"{ordered.Count(CollectionShelf.Watched)} of {ordered.Count} watched"));
    }
    /// <summary>« Reprendre · Nisemonogatari · S01 E04 », « Regarder · … », or « Revoir » from the first title once all are watched.</summary>
    private void ShowPlayLabel(MediaItem? up, List<MediaItem> ordered)
    {
        _play.IsEnabled = ordered.Count > 0;
        string label; string? detail = null;
        if (ordered.Count == 0) label = L("Regarder", "Watch");
        else
        {
            var title = up ?? ordered[0];
            var episode = _target is { Type: "Episode" } next && next.SeriesId == title.Id ? next : null;
            var playing = episode ?? (title.Type == "Movie" ? title : null);
            var resume = playing is not null && playing.Resumable && Shell.Settings.RememberPosition;
            var verb = up is null ? L("Revoir", "Watch again") : resume ? L("Reprendre", "Resume") : L("Regarder", "Watch");
            var code = episode is not null ? $"  ·  S{episode.ParentIndexNumber ?? 1:00} E{episode.IndexNumber ?? 1:00}" : "";
            label = $"{verb}  ·  {title.DisplayTitle}{code}";
            if (resume) detail = MediaQuality.Left(playing!);
        }
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 460 };
        if (detail is not null) text.Inlines!.Add(new Avalonia.Controls.Documents.Run("  ·  " + detail) { FontWeight = FontWeight.Normal, FontSize = 12.5 });
        _play.Content = Ui.Row(9, new Icon("play", 18) { VerticalAlignment = VerticalAlignment.Center }, text);
        Avalonia.Automation.AutomationProperties.SetName(_play, detail is null ? label : $"{label}, {detail}");
    }
    /// <summary>The episode a series of the collection picks up at (Jellyfin's next up), and the picture of the title to resume at the top.</summary>
    private async Task LoadTargetAsync(Session session, int version)
    {
        var ordered = Ordered();
        var title = CollectionShelf.UpNext(ordered) is int at ? ordered[at] : ordered.FirstOrDefault();
        if (title is null) return;
        try
        {
            var picture = await session.Images.BackdropAsync(title, 1920, ownPage: true);
            if (version != _version) return;
            if (picture is not null) { _backdrop.Source = picture; _backdrop.Opacity = 1; }
            if (title.Type != "Series") { _target = title; return; }
            var next = await session.NextEpisodeAsync(title.Id);
            if (version != _version) return;
            _target = next;
            ShowPlayLabel(CollectionShelf.UpNext(ordered) is int i ? ordered[i] : null, ordered);
        }
        // Only the picture and the episode named on « Reprendre »: without them, the page is complete.
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }
    private async Task PlayAsync()
    {
        var ordered = Ordered();
        if (ordered.Count == 0) return;
        var up = CollectionShelf.UpNext(ordered) is int at ? ordered[at] : null;
        var title = up ?? ordered[0];
        if (title.Type == "Movie") { await Shell.PlayAsync(title, fromStart: up is null); return; }
        // A series: the episode it picks up at; none known (all watched, or Jellyfin silent), its page, where Lecture says what it plays.
        if (up is not null && _target is { Type: "Episode" } episode && episode.SeriesId == title.Id) { await Shell.PlayAsync(episode); return; }
        Shell.Open(title);
    }

    private void ShowFavorite(bool on) => ShowState(_favorite, "favorite", on, on ? L("Favori", "Favorite") : L("Ajouter aux favoris", "Add to favorites"),
        on ? L("Dans tes favoris. Clique pour la retirer", "In your favorites. Click to remove it") : L("Ajouter aux favoris", "Add to favorites"));
    private void ShowWatched(List<MediaItem> titles)
    {
        var all = titles.Count > 0 && titles.All(CollectionShelf.Watched);
        ShowState(_watched, "watched", all, all ? L("Vue", "Watched") : L("Marquer la collection comme vue", "Mark collection as watched"),
            all ? L("Toute la collection est vue. Clique pour la marquer comme non vue", "The whole collection is watched. Click to mark it as unwatched") : L("Marquer chaque titre comme vu", "Mark every title as watched"));
    }
    private bool _favoriteBusy, _watchedBusy;
    private async Task FlipFavoriteAsync()
    {
        if (_favoriteBusy) return; _favoriteBusy = true;
        var want = !_box.UserData.IsFavorite;
        // Its glyph at the click, as a title's page; a refusal puts it back.
        try { ShowFavorite(want); await Shell.SetFavoriteAsync(_box, want); ShowFavorite(_box.UserData.IsFavorite); }
        finally { _favoriteBusy = false; }
    }
    private async Task FlipWatchedAsync()
    {
        if (_watchedBusy || _titles.Count == 0) return; _watchedBusy = true;
        var played = !_titles.All(CollectionShelf.Watched);
        // One request for all its titles: Jellyfin marks each one of a collection (UserPlayedItems on a BoxSet, checked on 12.1).
        try { ShowState(_watched, "watched", played, "", ""); await Shell.SetPlayedAsync(_box, played); ShowWatched(_titles); }
        finally { _watchedBusy = false; }
    }
    private void MoreMenu(Button button)
    {
        var menu = new ContextMenu();
        Shell.AddShelfToggle(menu, _box);
        if (Shell.Session is not null && _titles.Count > 1)
            menu.Items.Add(Ui.Entry(L("Changer l’ordre de visionnage…", "Change the watch order…"), "sliders", StartEditing));
        if (_order is not null && Shell.Session is not null)
            menu.Items.Add(Ui.Entry(L("Revenir à l’ordre de sortie", "Back to release order"), "refresh", () => _ = SaveOrderAsync(null)));
        if (Shell.CollectionRights.Manage)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            if (Shell.CollectionRights.Rename) menu.Items.Add(Ui.Entry(L("Renommer…", "Rename…"), "pencil", () => _ = Shell.RenameCollectionAsync(_box)));
            menu.Items.Add(Ui.Entry(L("Supprimer la collection…", "Delete collection…"), "trash", () => Shell.AskDeleteCollection(_box)));
        }
        if (menu.Items.Count == 0) return;
        Ui.OpenUnder(menu, button);
    }
    /// <summary>Its new name, after « Renommer… », without leaving the page.</summary>
    public void Renamed(string name) { _box = _box with { Name = name }; _title.Text = _box.DisplayTitle; }

    // —— The watch order: chosen once, by this account, kept on Jellyfin beside its other choices ——
    private void ShowOrder(bool byRelease)
    {
        if (_byRelease == byRelease) return;
        _byRelease = byRelease; _orderChoice.Select(byRelease ? 1 : 0);
        Render(loading: false);
    }
    private void StartEditing()
    {
        _editing = true; _byRelease = false; _orderChoice.Select(0, animate: false);
        Render(loading: false);
        _head.BringIntoView();
    }
    private async Task FinishEditingAsync()
    {
        _editing = false;
        await SaveOrderAsync(Ordered().Select(x => x.Id).ToList());
    }
    /// <summary>The order kept for this account (null: by release date again), then the page and the library drawn again.</summary>
    private async Task SaveOrderAsync(IReadOnlyList<string>? order)
    {
        if (Shell.Session is not { } session) return;
        var was = _order; _order = order; _editing = false;
        Render(loading: false);
        try { await session.Client.SetShelfChoiceAsync(_box.Id, x => x with { Order = order }, session.Lifetime); }
        catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested) { return; }
        catch (Exception ex) when (Errors.Expected(ex)) { _order = was; Render(loading: false); Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); return; }
        Shell.ForgetGrouping();
        await LoadTargetAsync(session, _version);
    }
    /// <summary>The titles as rows while their order changes: number, name, year, and a step up or down.</summary>
    private void RenderEditing(List<MediaItem> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            var place = i; var title = ordered[i];
            var row = new DockPanel();
            var down = Step("down", L($"Descendre « {title.DisplayTitle} »", $"Move “{title.DisplayTitle}” down"), place < ordered.Count - 1, () => Move(place, 1));
            var up = Step("up", L($"Monter « {title.DisplayTitle} »", $"Move “{title.DisplayTitle}” up"), place > 0, () => Move(place, -1));
            DockPanel.SetDock(down, Dock.Right); DockPanel.SetDock(up, Dock.Right);
            row.Children.Add(down); row.Children.Add(up);
            var text = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = CollectionShelf.Number(place), FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Ui.Muted, Width = 40 });
            text.Children.Add(new TextBlock { Text = title.DisplayTitle, FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = "   " + CollectionShelf.TitleLine(title), FontSize = 13, Foreground = Ui.Muted });
            row.Children.Add(new Border { Background = Ui.Surface, BorderBrush = Ui.MenuLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 10, 10, 10), Child = text });
            _rows.Children.Add(row);
        }
    }
    private static Button Step(string icon, string name, bool enabled, Action move)
    {
        var button = Ui.IconButton(icon, name, move, "icon", 16, name);
        button.IsEnabled = enabled; button.Margin = new Thickness(8, 0, 0, 0); button.Tag = icon; button.VerticalAlignment = VerticalAlignment.Center;
        return button;
    }
    private void Move(int index, int step)
    {
        _order = CollectionShelf.Move(Ordered(), index, step);
        Render(loading: false);
        // The keyboard follows the title it moved.
        var at = Math.Clamp(index + step, 0, _rows.Children.Count - 1);
        if (_rows.Children[at] is DockPanel row && row.Children.OfType<Button>().FirstOrDefault(x => x.IsEnabled && (string?)x.Tag == (step < 0 ? "up" : "down")) is { } again) again.Focus();
    }
    /// <summary>For the self-check: loaded from Jellyfin, at least once.</summary>
    public bool TitlesRead => _loaded;
}
