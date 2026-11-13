using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// Réglages and Aide, one window over Mira, as on Windows (choice of ogsas, 2026-11-12): on the left, Réglages | Aide, a
// search and the sections, what only the server's owner sees under « Ton serveur »; on the right, the section open, said
// in one line, the one last seen opened again. Behind it, a still and blurred picture of the app (MainWindow.Sheet.cs).
public sealed partial class SettingsPage
{
    private static IReadOnlyList<(string Key, string Title, string Icon, string About)> Tabs =>
    [
        ("playback", L("Lecture", "Playback"), "play", L("Ce qui compte pendant que tu regardes.", "What matters while you watch.")),
        ("audio", L("Audio & sous-titres", "Audio & subtitles"), "subtitles", L("Les bonnes langues dès le premier épisode, et des sous-titres lisibles.", "The right languages from the very first episode, and readable subtitles.")),
        ("appearance", L("Apparence", "Appearance"), "sparkle", L("La langue de Mira et ce que montre l’accueil.", "Mira’s language and what Home shows.")),
        ("server", L("Compte & serveur", "Account & server"), "server", L("Ton compte, ton serveur Jellyfin et le chemin jusqu’à lui.", "Your account, your Jellyfin server and the way to it.")),
        ("updates", L("Mira sur ce Mac", "Mira on this Mac"), "laptop", L("Mises à jour, titres gardés sur ce Mac et rapport pour signaler un problème.", "Updates, titles kept on this Mac and a report for a problem.")),
        ("privacy", L("Confidentialité", "Privacy"), "shield", L("Tout ce qui part sur le réseau, et comment l’arrêter.", "Everything that goes out on the network, and how to stop it.")),
        ("report", L("Ton bilan", "Your recap"), "clock", L("Ce que tu as regardé, sur tous tes appareils.", "What you’ve watched, on all your devices.")),
        ("sharing", L("Amis & invités", "Friends & guests"), "users", L("Un compte pour un ami, un lien pour un invité, un appareil à autoriser.", "An account for a friend, a link for a guest, a device to let in.")),
        ("care", L("Entretien", "Maintenance"), "sliders", L("Ce qui garde ta bibliothèque en forme : titres à corriger, sous-titres.", "What keeps your library in shape: titles to fix, subtitles.")),
    ];
    /// <summary>The sections only the server's owner has something in, listed under « Ton serveur ».</summary>
    private static readonly string[] ServerTools = ["sharing", "care"];
    /// <summary>Aide's topics: the shortcuts, shown in the window; the others open what they name.</summary>
    private static IReadOnlyList<(string Key, string Title, string Icon, bool Outside)> HelpTopics =>
    [
        ("keys", L("Raccourcis clavier", "Keyboard shortcuts"), "keyboard", false),
        ("whatsnew", L("Nouveautés de Mira", "What’s new in Mira"), "sparkle", false),
        ("online", L("Guide en ligne", "Online guide"), "globe", true),
        ("report", L("Exporter un rapport…", "Export a report…"), "warning", false),
    ];
    private static string _lastTab = "playback";
    private readonly Dictionary<string, StackPanel> _tabs = [];
    private readonly Dictionary<string, Button> _tabButtons = [];
    private readonly Dictionary<string, Button> _helpButtons = [];
    private readonly StackPanel _settingsList = new() { Spacing = 2 };
    private readonly StackPanel _helpList = new() { Spacing = 2 };
    private readonly ScrollViewer _tabScroll = new();
    private readonly ContentControl _tabBody = new();
    private readonly TextBlock _tabTitle = new() { FontSize = 26, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _tabAbout = new() { FontSize = 14, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _search = new() { FontSize = 14, Padding = new Thickness(12, 9) };
    private readonly Button _toSettings = new(), _toHelp = new();
    private readonly TextBlock _foot = new() { FontSize = 11, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap };
    private readonly Border _window = new();
    /// <summary>Aide's shortcuts, one line each, found by its search: the line, then its group's label and card.</summary>
    private readonly List<(Control Line, string Words, Control Group)> _keyLines = [];
    private readonly StackPanel _keys = new() { Spacing = 10 };

    /// <summary>The section shown: « playback », « audio », « appearance », « server », « updates », « privacy », « report », « sharing » or « care ».</summary>
    public string CurrentTab { get; private set; } = _lastTab;
    /// <summary>Aide's topic shown (« keys »), or null on Réglages.</summary>
    public string? HelpTopic { get; private set; }
    /// <summary>What the search holds (for the self-check).</summary>
    public string Search => _search.Text ?? "";
    /// <summary>The settings the search found, by section (for the self-check).</summary>
    public IReadOnlyDictionary<string, int> Found => _found;
    private readonly Dictionary<string, int> _found = [];

    private Control Layout(MainWindow shell, string? help)
    {
        // —— The list: Réglages | Aide, the search, the sections ——
        var serverLabel = new TextBlock { Text = L("Ton serveur", "Your server"), FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.Faint, Margin = new Thickness(14, 16, 0, 4) };
        Avalonia.Automation.AutomationProperties.SetHeadingLevel(serverLabel, 2);
        foreach (var (key, title, icon, _) in Tabs)
        {
            if (key == ServerTools[0]) _settingsList.Children.Add(serverLabel);
            var button = NavButton(title, icon, false);
            button.Click += (_, _) => { _search.Text = ""; _searched = ""; Show(key); };
            _tabButtons[key] = button; _settingsList.Children.Add(button);
        }
        var moreLabel = new TextBlock { Text = L("Plus", "More"), FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.Faint, Margin = new Thickness(14, 16, 0, 4) };
        foreach (var (key, title, icon, outside) in HelpTopics)
        {
            if (key == "whatsnew") _helpList.Children.Add(moreLabel);
            var button = NavButton(title, icon, outside);
            button.Click += (_, _) => OpenTopic(key);
            _helpButtons[key] = button; _helpList.Children.Add(button);
        }
        // « Amis & invités » and « Entretien » hold only what an administrator sees: listed while one of their sections
        // shows, and « Ton serveur » with them.
        void Listed()
        {
            foreach (var key in ServerTools) _tabButtons[key].IsVisible = _tabs[key].Children.Any(x => x.IsVisible);
            serverLabel.IsVisible = ServerTools.Any(x => _tabButtons[x].IsVisible);
        }
        foreach (var key in ServerTools)
            foreach (var section in _tabs[key].Children) section.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) Listed(); };
        Listed();

        foreach (var (button, words) in new[] { (_toSettings, L("Réglages", "Settings")), (_toHelp, L("Aide", "Help")) })
        {
            button.Content = words; button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.Padding = new Thickness(10, 7); button.FontSize = 13.5; button.FontWeight = FontWeight.SemiBold; button.CornerRadius = new CornerRadius(9);
            button.BorderThickness = new Thickness(1);
        }
        _toSettings.Click += (_, _) => ShowSettingsSide();
        _toHelp.Click += (_, _) => ShowHelp(HelpTopic ?? "keys");
        var turn = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Children = { _toSettings, _toHelp } };
        Grid.SetColumn(_toHelp, 1);
        var switcher = new Border { Background = Ui.Brush("#161616"), CornerRadius = new CornerRadius(12), Padding = new Thickness(3), Child = turn, Margin = new Thickness(0, 0, 0, 12) };

        _search.TextChanged += (_, _) => Searched();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; OpenFirstFound(); }
            else if (e.Key == Key.Down && HelpTopic is null) { e.Handled = true; FirstFocusable(_tabBody)?.Focus(NavigationMethod.Directional); }
        };
        var keycap = Keycap("⌘F");
        keycap.HorizontalAlignment = HorizontalAlignment.Right; keycap.Margin = new Thickness(0, 0, 9, 0); keycap.IsHitTestVisible = false;
        _search.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty || e.Property == IsFocusedProperty) keycap.IsVisible = string.IsNullOrEmpty(_search.Text) && !_search.IsFocused; };
        var searchBox = new Grid { Margin = new Thickness(0, 0, 0, 12), Children = { _search, keycap } };

        var foot = Ui.Column(4,
            new TextBlock { Text = L("Chaque changement s’applique et s’enregistre tout de suite.", "Each change applies and is saved at once."), FontSize = 11, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap },
            _foot);
        foot.Margin = new Thickness(6, 12, 0, 0);
        var lists = new Panel { Children = { _settingsList, _helpList } };
        var side = new DockPanel { Margin = new Thickness(14, 18, 14, 16) };
        DockPanel.SetDock(switcher, Dock.Top); side.Children.Add(switcher);
        DockPanel.SetDock(searchBox, Dock.Top); side.Children.Add(searchBox);
        DockPanel.SetDock(foot, Dock.Bottom); side.Children.Add(foot);
        side.Children.Add(new ScrollViewer { Content = lists, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var sideBack = new Border { Background = Ui.Brush("#0C0C0C"), BorderBrush = Ui.Brush("#1F1F1F"), BorderThickness = new Thickness(0, 0, 1, 0), Child = side };
        // Its own left corners, those of the window inside its edge: otherwise its fill may cover the window's edge there.
        sideBack.CornerRadius = new CornerRadius(19, 0, 0, 19);

        // —— The section open: its title and line, Échap and the cross, then its settings ——
        Avalonia.Automation.AutomationProperties.SetHeadingLevel(_tabTitle, 1);
        var close = Ui.IconButton("close", L("Fermer · Échap", "Close · Esc"), shell.CloseSettingsWindow, size: 17, name: L("Fermer", "Close"));
        close.Width = close.Height = 36; close.CornerRadius = new CornerRadius(18); close.Background = Ui.Raised;
        var corner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Top, Children = { Keycap(L("Échap", "Esc")), close } };
        corner.Children[0].VerticalAlignment = VerticalAlignment.Center;
        var head = new DockPanel { Margin = new Thickness(38, 30, 22, 0) };
        DockPanel.SetDock(corner, Dock.Right); head.Children.Add(corner);
        head.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 20, 0), Children = { _tabTitle, _tabAbout } });
        // Its lines at most 940 wide, as on Windows: a description across a whole screen is hard to follow.
        var page = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 14, 26), Children = { _tabBody } };
        _tabScroll.Content = page;
        _tabScroll.Margin = new Thickness(38, 22, 8, 0);
        _tabScroll.SizeChanged += (_, e) => page.Width = Math.Max(0, Math.Min(940, e.NewSize.Width - 14));
        var body = new DockPanel();
        DockPanel.SetDock(head, Dock.Top); body.Children.Add(head);
        body.Children.Add(_tabScroll);

        var inside = new Grid { ColumnDefinitions = new ColumnDefinitions("262,*") };
        inside.Children.Add(sideBack);
        Grid.SetColumn(body, 1); inside.Children.Add(body);
        _window.Child = inside;
        _window.Background = Ui.Brush("#111111"); _window.BorderBrush = Ui.Brush("#2A2A2A"); _window.BorderThickness = new Thickness(1);
        _window.CornerRadius = new CornerRadius(20); _window.ClipToBounds = true;
        _window.MaxWidth = 1180; _window.MaxHeight = 840;
        _window.Margin = new Thickness(44, Math.Max(52, shell.TitleBarInset + 22), 44, 36);

        // —— Behind it: the app as it was, blurred and dimmed; a click there closes the window ——
        var scrim = new Border { Background = Ui.Brush(shell.SheetBackdrop is null ? "#F2000000" : "#80000000") };
        var pressed = false;
        scrim.PointerPressed += (_, e) => { pressed = true; e.Handled = true; };
        scrim.PointerReleased += (_, e) => { if (!pressed) return; pressed = false; e.Handled = true; shell.CloseSettingsWindow(); };
        var root = new Grid();
        if (shell.SheetBackdrop is { } picture) root.Children.Add(new Image { Source = picture, Stretch = Stretch.Fill, IsHitTestVisible = false });
        root.Children.Add(scrim);
        root.Children.Add(_window);
        KeyboardNavigation.SetTabNavigation(_window, KeyboardNavigationMode.Cycle);

        BuildKeys();
        if (help is not null) ShowHelp(help);
        else ShowSettingsSide();
        return root;
    }

    /// <summary>A button of the window's list: its icon, its name, room on the right for what the search found there.</summary>
    private static Button NavButton(string title, string icon, bool outside)
    {
        var row = new DockPanel();
        var count = new Border { Background = Ui.Brush("#242424"), CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 2), VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Child = new TextBlock { FontSize = 11.5, FontWeight = FontWeight.Bold, Foreground = Ui.TextTertiary } };
        DockPanel.SetDock(count, Dock.Right); row.Children.Add(count);
        if (outside) { var arrow = new Icon("external", 14) { VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(arrow, Dock.Right); row.Children.Add(arrow); }
        var glyph = new Icon(icon, 18) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(glyph, Dock.Left); row.Children.Add(glyph);
        row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var button = new Button
        {
            Content = row, Classes = { "quiet" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 10), FontWeight = FontWeight.Normal, FontSize = 14, Tag = title,
        };
        Avalonia.Automation.AutomationProperties.SetName(button, title);
        return button;
    }
    private static void ShowCount(Button button, int count)
    {
        if (button.Content is not DockPanel { Children: [Border { Child: TextBlock text } badge, ..] }) return;
        badge.IsVisible = count > 0; text.Text = count.ToString(Lang.Culture);
    }
    /// <summary>The list: the page shown lit (as the rail's), the others quiet; during a search, those with nothing found dimmer.</summary>
    private static void Light(Dictionary<string, Button> buttons, string? shown, Func<string, bool>? found = null)
    {
        foreach (var (name, button) in buttons)
        {
            var on = name == shown;
            button.Classes.Set("on", on); button.Classes.Set("quiet", !on);
            button.Opacity = on || found is null || found(name) ? 1 : .55;
            var words = (string)button.Tag!;
            Avalonia.Automation.AutomationProperties.SetName(button, on ? L($"{words}, affiché", $"{words}, shown") : words);
        }
    }
    private static Border Keycap(string text) => new()
    {
        MinWidth = 26, Height = 24, Padding = new Thickness(7, 0), CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center,
        Background = Ui.Raised, BorderBrush = Ui.Brush("#363636"), BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.Faint, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };
    /// <summary>Réglages | Aide: the side shown, its switch lit.</summary>
    private void Side(bool help)
    {
        _settingsList.IsVisible = !help; _helpList.IsVisible = help;
        foreach (var (button, on) in new[] { (_toSettings, !help), (_toHelp, help) })
        {
            button.Background = on ? Ui.Brush("#242424") : Brushes.Transparent;
            button.BorderBrush = on ? Ui.Brush("#3F3F3F") : Brushes.Transparent;
            button.Foreground = on ? Ui.Ink : Ui.Muted;
            Avalonia.Automation.AutomationProperties.SetName(button, on ? L($"{button.Content}, affiché", $"{button.Content}, shown") : (string)button.Content!);
        }
        _search.Watermark = help ? L("Rechercher dans l’aide", "Search help") : L("Rechercher un réglage", "Search settings");
        Avalonia.Automation.AutomationProperties.SetName(_search, _search.Watermark);
        _foot.Text = help ? L($"Mira {Updates.Current} · l’aide s’ouvre aussi par ⌘/.", $"Mira {Updates.Current} · help also opens with ⌘/.") : $"Mira {Updates.Current} · Jellyfin + mpv";
    }

    /// <summary>Réglages, on the section last seen (or the one a notice asked for, <see cref="OpenAt"/>).</summary>
    public void ShowSettingsSide()
    {
        var was = HelpTopic is not null;
        HelpTopic = null;
        if (was) _search.Text = ""; _searched = "";
        Side(false);
        Show(_tabs.ContainsKey(_lastTab) && _tabButtons[_lastTab].IsVisible ? _lastTab : "playback");
    }
    /// <summary>The section open before the page was made again, or on its top again.</summary>
    public override void ShowAgain() { if (HelpTopic is not null || _lastTab != CurrentTab) ShowSettingsSide(); else GlideUp.Start(_tabScroll); }
    /// <summary>The section the next Réglages opens on (a notice that names a setting).</summary>
    public static void OpenAt(string key) { if (Tabs.Any(x => x.Key == key)) _lastTab = key; }
    public void Show(string key)
    {
        if (!_tabs.TryGetValue(key, out var tab)) return;
        if (HelpTopic is not null) { HelpTopic = null; _search.Text = ""; _searched = ""; Side(false); }
        EndSearch();
        CurrentTab = _lastTab = key;
        var (_, title, _, about) = Tabs.First(x => x.Key == key);
        _tabTitle.Text = title; _tabAbout.Text = about;
        Light(_tabButtons, key);
        // Drawn each time it opens: a switch changed in another section shows as it is (SettingsPage.Privacy.cs).
        if (key == "privacy") DrawPrivacy();
        _tabBody.Content = tab;
        _tabScroll.Offset = default;
    }

    /// <summary>The window comes in over the picture of the app: the picture fades in, the window rises a little (MainWindow.OpenSheet).</summary>
    public void Enter()
    {
        Opacity = 0;
        _window.RenderTransform = TransformOperations.Parse("translateY(14px)");
        Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) }];
        _window.Transitions = [new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(300), Easing = new Avalonia.Animation.Easings.CubicEaseOut() }];
        Dispatcher.UIThread.Post(() => { Opacity = 1; _window.RenderTransform = TransformOperations.Parse("translateY(0px)"); }, DispatcherPriority.Loaded);
    }

    // —— Aide ——
    /// <summary>Aide on <paramref name="key"/>: the shortcuts in the window; the others open what they name, the window staying.</summary>
    public void ShowHelp(string key)
    {
        if (HelpTopic is null) { EndSearch(); _search.Text = ""; _searched = ""; Side(true); }
        if (key != "keys") { HelpTopic ??= "keys"; ShowKeys(); OpenTopic(key); return; }
        ShowKeys();
    }
    private void ShowKeys()
    {
        HelpTopic = "keys";
        Light(_helpButtons, "keys");
        _tabTitle.Text = L("Raccourcis clavier", "Keyboard shortcuts");
        _tabAbout.Text = L("Les touches que Mira lit sur un Mac, partout puis pendant la lecture.", "The keys Mira reads on a Mac, everywhere and then while playing.");
        _tabBody.Content = _keys;
        _tabScroll.Offset = default;
    }
    private void OpenTopic(string key)
    {
        switch (key)
        {
            case "keys": ShowKeys(); break;
            case "whatsnew": Shell.ShowWhatsNew(WhatsNew.Recent(Updates.Current)); break;
            case "online": Updates.Open(MainWindow.Guide); break;
            case "report": _ = Shell.ExportReportAsync(); break;
        }
    }
    /// <summary>The shortcuts by group, each on its card, two columns of keycaps; each line found by the search.</summary>
    private void BuildKeys()
    {
        foreach (var (group, lines) in MainWindow.Shortcuts)
        {
            var label = new TextBlock { Text = group, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.Faint, Margin = new Thickness(4, 6, 0, 0) };
            Avalonia.Automation.AutomationProperties.SetHeadingLevel(label, 2);
            var grid = new UniformGrid { Columns = 2 };
            var card = Card(grid);
            var holder = Ui.Column(10, label, card);
            foreach (var (keys, text) in lines)
            {
                var line = MainWindow.ShortcutLine(keys, text);
                grid.Children.Add(line);
                _keyLines.Add((line, string.Join(" ", keys) + " " + text + " " + group, holder));
            }
            _keys.Children.Add(holder);
        }
    }

    // —— The search: every section's settings at once, those found kept where they are (their switches and lists work
    // there), the rest put away. Sections Mira shows or hides by itself (an administrator's) keep doing so: only the
    // cards are hidden, never the sections ——
    private readonly Dictionary<Control, bool> _hidden = [];
    private readonly List<(Action<bool> Fold, bool Open)> _searchOpened = [];
    private readonly StackPanel _results = new() { Spacing = 10 };
    private bool _searching;

    /// <summary>For the self-check: <paramref name="text"/> typed in the search.</summary>
    public void SearchFor(string text) { _search.Text = text; Searched(); }
    /// <summary>For the self-check: the shortcuts Aide shows.</summary>
    public int KeysShown => _keyLines.Count(x => x.Line.IsVisible && x.Group.IsVisible);
    /// <summary>⌘F in the window: its own search, not the library's.</summary>
    public void FocusSearch() { _search.Focus(); _search.SelectAll(); }
    /// <summary>Échap with something typed in the search: the search goes first, then the window.</summary>
    public bool ClearSearch()
    {
        if (string.IsNullOrEmpty(_search.Text)) return false;
        _search.Text = ""; _search.Focus();
        // Avalonia says TextChanged later: the window is as it should be now, for the Échap that may follow at once.
        Searched();
        return true;
    }
    private string? _searched;
    private void Searched()
    {
        var query = (_search.Text ?? "").Trim();
        // Said again by TextChanged after a change already shown.
        if (query == _searched) return;
        _searched = query;
        if (HelpTopic is not null) { SearchKeys(query); return; }
        if (query.Length == 0) { EndSearch(); Show(CurrentTab); return; }
        if (!_searching)
        {
            _searching = true;
            foreach (var (fold, inside, _) in _folds) { _searchOpened.Add((fold, inside.IsVisible)); fold(true); }
        }
        foreach (var (element, shown) in _hidden) element.IsVisible = shown;
        _hidden.Clear(); _found.Clear();
        _tabBody.Content = null; _results.Children.Clear();
        var total = 0;
        foreach (var (key, title, _, _) in Tabs)
        {
            var tab = _tabs[key];
            var count = 0;
            if (_tabButtons[key].IsVisible || !ServerTools.Contains(key))
            {
                foreach (var card in tab.GetLogicalDescendants().OfType<Border>().Where(x => x.Classes.Contains("setting")).ToList())
                {
                    if (!ShownIn(card, tab)) continue;
                    var found = SheetWindow.Matches(Words(card), query);
                    if (!found) Hide(card); else count++;
                }
                // A section's label and « Réglages avancés » go with their cards.
                foreach (var label in tab.GetLogicalDescendants().OfType<TextBlock>().Where(x => x.Classes.Contains("label")).ToList())
                    if (label.Parent is Panel list && !list.Children.OfType<Border>().Any(x => x.IsVisible)) Hide(label);
                foreach (var (_, inside, header) in _folds)
                    if (tab.IsLogicalAncestorOf(inside) && inside is Panel cards && !cards.Children.Any(x => x.IsVisible)) Hide(header);
            }
            _found[key] = count;
            ShowCount(_tabButtons[key], count);
            if (count == 0) continue;
            total += count;
            var crumb = new TextBlock { Text = title.ToUpper(Lang.Culture), FontSize = 11.5, FontWeight = FontWeight.Bold, LetterSpacing = 0.6, Foreground = Ui.Faint, Margin = new Thickness(4, _results.Children.Count == 0 ? 0 : 18, 0, 0) };
            _results.Children.Add(crumb);
            _results.Children.Add(tab);
        }
        Light(_tabButtons, null, key => _found.GetValueOrDefault(key) > 0);
        _tabTitle.Text = total == 0 ? L("Aucun résultat", "No results") : Lang.Count(total, "résultat", "résultats", "result", "results");
        _tabAbout.Text = total == 0
            ? L($"Aucun réglage ne correspond à « {query} ».", $"No setting matches “{query}”.")
            : L($"Pour « {query} », dans toutes les sections. Change-les ici même ; Entrée ouvre le premier dans sa section.", $"For “{query}”, in every section. Change them right here; Enter opens the first one in its section.");
        _tabBody.Content = _results;
        _tabScroll.Offset = default;
    }
    /// <summary>Back to the section open before the search, everything as it was.</summary>
    private void EndSearch()
    {
        if (!_searching) return;
        _searching = false;
        foreach (var (element, shown) in _hidden) element.IsVisible = shown;
        _hidden.Clear(); _found.Clear();
        foreach (var (fold, open) in _searchOpened) fold(open);
        _searchOpened.Clear();
        _tabBody.Content = null; _results.Children.Clear();
        foreach (var button in _tabButtons.Values) ShowCount(button, 0);
    }
    private void Hide(Control element) { if (!_hidden.ContainsKey(element)) _hidden[element] = element.IsVisible; element.IsVisible = false; }
    /// <summary>Shown in its section, the search aside: none of the parents between it and the section is hidden.</summary>
    private static bool ShownIn(Control control, Control tab)
    {
        for (var parent = control.Parent as Control; parent is not null && parent != tab; parent = parent.Parent as Control)
            if (!parent.IsVisible) return false;
        return control.IsVisible;
    }
    /// <summary>Everything a card says: its titles, its lines, what its lists show.</summary>
    private static string Words(Control root)
    {
        var words = new List<string>();
        foreach (var part in root.GetLogicalDescendants())
        {
            if (part is TextBlock { Text: { Length: > 0 } text }) words.Add(text);
            else if (part is Control control && Avalonia.Automation.AutomationProperties.GetName(control) is { Length: > 0 } name) words.Add(name);
        }
        return string.Join(" ", words);
    }
    /// <summary>Entrée: the first setting found, in its own section.</summary>
    private void OpenFirstFound()
    {
        if (HelpTopic is not null) return;
        var first = Tabs.FirstOrDefault(x => _found.GetValueOrDefault(x.Key) > 0).Key;
        if (first is null) return;
        var card = _tabs[first].GetLogicalDescendants().OfType<Border>().FirstOrDefault(x => x.Classes.Contains("setting") && x.IsVisible && ShownIn(x, _tabs[first]));
        _search.Text = ""; _searched = "";
        Show(first);
        if (card is not null) Dispatcher.UIThread.Post(() => { card.BringIntoView(); FirstFocusable(card)?.Focus(NavigationMethod.Directional); }, DispatcherPriority.Loaded);
    }
    private static Control? FirstFocusable(Control root) => root.GetLogicalDescendants().OfType<Control>().FirstOrDefault(x => x.Focusable && x.IsEffectivelyVisible && x.IsEffectivelyEnabled && x is not TextBlock);
    /// <summary>Aide's search: the shortcuts that say what is typed, their groups with them.</summary>
    private void SearchKeys(string query)
    {
        var groups = new Dictionary<Control, int>();
        foreach (var (line, words, group) in _keyLines)
        {
            var found = SheetWindow.Matches(words, query);
            line.IsVisible = found;
            groups[group] = groups.GetValueOrDefault(group) + (found ? 1 : 0);
        }
        foreach (var (group, count) in groups) group.IsVisible = count > 0;
        var total = query.Length == 0 ? 0 : groups.Values.Sum();
        ShowCount(_helpButtons["keys"], total);
        if (HelpTopic != "keys") ShowKeys();
        _tabTitle.Text = query.Length == 0 ? L("Raccourcis clavier", "Keyboard shortcuts") : total == 0 ? L("Aucun résultat", "No results") : Lang.Count(total, "raccourci", "raccourcis", "shortcut", "shortcuts");
        _tabAbout.Text = query.Length == 0 ? L("Les touches que Mira lit sur un Mac, partout puis pendant la lecture.", "The keys Mira reads on a Mac, everywhere and then while playing.")
            : total == 0 ? L($"Aucun raccourci ne correspond à « {query} ».", $"No shortcut matches “{query}”.") : L($"Pour « {query} ».", $"For “{query}”.");
    }
}
