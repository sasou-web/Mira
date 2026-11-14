using System.Net;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// Téléchargements, for the administrators of Jellyfin. TorLink runs on the PC of Jellyfin only (Mira for Windows), where
/// its downloads land next to the library; this page sends it searches and downloads through the relay of the extension
/// (<c>Mira/torlink</c>), as Mira web's « Télécharger sur le PC serveur » and Mira for Windows away from that PC do: the
/// best release of each title or episode first (<see cref="ReleaseRanking"/>), what the PC has under way with its
/// progress and time left (<see cref="RemoteLines"/>), read every 3 s while the page shows, and only then.
/// « Chercher une meilleure version » of a film or an episode opens it for that title: its search made, its results
/// only those better than its file (<see cref="TitleUpgrade"/>), each one asked as its replacement.
/// </summary>
public sealed class DownloadsPage : Page
{
    public override string Rail => "downloads";
    public override void ShowAgain() { Glide.ToTop(_restScroll); Glide.ToTop(_resultsScroll); Glide.ToTop(_queueScroll); }
    private static readonly TimeSpan SearchWait = TimeSpan.FromSeconds(90);
    private static string PageLine => L("Chaque téléchargement terminé rejoint ta bibliothèque.", "Each finished download joins your library.");
    /// <summary>Below this width of the page, one column (« Régie » keeps both side by side above it).</summary>
    private const double WideFrom = 1150;
    // The Windows app's states and secondary ink (Themes/Cinema.xaml).
    private static readonly IBrush Success = Ui.Brush("#86C6A2"), Warning = Ui.Brush("#D5B787"), WarningText = Ui.Brush("#E9DCC3"), Danger = Ui.Brush("#F3AD99"),
        Secondary = Ui.Brush("#C5C5CA"), Line = Ui.Brush("#272727"), Surface = Ui.Brush("#121212"), Accent = Ui.Brush("#F5F5F7"), OnAccent = Ui.Brush("#0B0B0B"),
        Body = Ui.Brush("#E2E2E6"), Rule = Ui.Brush("#1C1C1C");
    private readonly Session? _session;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly TextBlock _subtitle = new() { Text = L("Lecture du PC serveur…", "Reading the server PC…"), FontSize = 14, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _pillText = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _pillDot = new() { Width = 8, Height = 8, Fill = Ui.Faint, VerticalAlignment = VerticalAlignment.Center };
    // Beside the pill: the PC that downloads, and what is left on its media disks (sent by its Mira, never elsewhere).
    private readonly StackPanel _place;
    private readonly TextBlock _placeName = new() { FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _freeRule = new() { Width = 1, Height = 14, Background = Line, Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _freeText = new() { FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.Faint, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _query = new() { Watermark = L("Un film, une série, un anime…", "A movie, a show, an anime…"), MaxLength = 200 };
    private readonly Button _search = new() { Content = L("Chercher", "Search"), Classes = { "primary" }, Padding = new Thickness(24, 12), Margin = new Thickness(12, 0, 0, 0), IsEnabled = false };
    private readonly Button _magnetButton;
    private readonly Border _magnetPanel;
    private readonly TextBox _magnet = new() { Watermark = "magnet:?xt=urn:btih:…", FontSize = 14, Padding = new Thickness(12, 9) };
    private readonly Button _magnetSend = new() { Content = L("Envoyer au PC serveur", "Send to the server PC"), Classes = { "primary" }, Padding = new Thickness(18, 9), Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock _magnetError = new() { FontSize = 12.5, Foreground = WarningText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
    private readonly TextBlock _searchStatus = new() { FontSize = 13.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 14) };
    private readonly StackPanel _results = new() { Margin = new Thickness(0, 0, 0, 40) };
    private readonly ScrollViewer _resultsScroll;
    // —— At rest: the queue in columns, the series followed, the titles placed ——
    private readonly ScrollViewer _restScroll;
    private readonly Control _resultsEmpty;
    private readonly TextBlock _summary = new() { FontSize = 13.5, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 2) };
    private readonly StackPanel _queue = new(), _follows = new();
    private readonly Control _queueEmpty;
    private readonly Grid _columns = new() { ColumnDefinitions = new ColumnDefinitions("*,40,404"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 32, 0, 0) };
    private readonly StackPanel _followsSection = new() { IsVisible = false }, _placedSection = new() { IsVisible = false };
    private readonly TextBlock _followsCount = Count18(), _placedCount = Count18();
    private readonly WrapPanel _placed = new() { Margin = new Thickness(0, 14, -16, 0) };
    private readonly Button _placedAll = new() { Classes = { "quiet" }, Padding = new Thickness(9, 5), FontSize = 13, Foreground = Ui.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
    // —— During a search: the queue tight on the right, the series and the titles folded under it ——
    private readonly Grid _searchView = new() { ColumnDefinitions = new ColumnDefinitions("*,36,428"), RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(0, 22, 0, 0), IsVisible = false };
    private readonly DockPanel _resultsColumn = new();
    private readonly ScrollViewer _queueScroll;
    private readonly TextBlock _sideSummary = new() { FontSize = 13.5, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _queueCompact = new(), _followsCompact = new();
    private readonly TextBlock _queueCompactEmpty = new() { Text = L("Rien en cours.", "Nothing in progress."), FontSize = 13, Foreground = Ui.Muted, Margin = new Thickness(13, 12) };
    private readonly Button _followsFold, _placedFold;
    private readonly TextBlock _followsFoldCount = FoldCount(), _followsFoldLine = FoldLine(), _placedFoldCount = FoldCount(), _placedFoldLine = FoldLine();
    private readonly Icon _followsChevron = Chevron(), _placedChevron = Chevron();
    private readonly Border _followsCompactCard;
    private readonly WrapPanel _placedCompact = new() { Margin = new Thickness(0, 12, -16, 0), IsVisible = false };
    private readonly Grid _searchRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
    private readonly Border _searchRule = new() { Width = 1, Height = 24, Background = Ui.Raised2, Margin = new Thickness(14, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _problem = new() { Background = Ui.Brush("#000000"), IsVisible = false };
    private readonly Icon _problemIcon = new("server", 40) { Foreground = Ui.Faint, Margin = new Thickness(0, 0, 0, 16) };
    private readonly TextBlock _problemTitle = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _problemHint = new() { FontSize = 14, Foreground = Ui.Muted, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
    /// <summary>Jellyfin answered once: the page shows the PC, and a hiccup afterwards keeps it on screen.</summary>
    private bool _filled, _ready, _loading, _again, _searching;
    private bool _narrow, _searchShown, _placedAllShown, _followsOpen, _placedOpen;
    private int _searchVersion;
    private RelayAgent? _agent;
    /// <summary>What the page last showed, drawn again when a picture arrives or the width changes.</summary>
    private RelayStatus? _status;
    /// <summary>The titles of Jellyfin whose poster the page shows, by id (null: Jellyfin has none, or no such title).</summary>
    private readonly Dictionary<string, MediaItem?> _posters = new(StringComparer.OrdinalIgnoreCase);
    private bool _postersAsking;
    /// <summary>Results sent from this page (search id / result id): « Envoyé » on their button.</summary>
    private readonly HashSet<string> _sent = [];
    /// <summary>Rows of the page by key, their texts changed in place every 3 s: a button under the pointer stays where it is.</summary>
    private readonly Dictionary<string, QueueRow> _rows = [];
    private readonly HashSet<string> _shownKeys = [];
    private sealed record QueueRow(string Shape, Control View, TextBlock[] Texts, Border? Bar, Control? Rule);
    private sealed record RowData(string Key, string Shape, string Name, string[] Texts, int? Progress, Func<QueueRow> Make);

    /// <summary>What the PC has under way and sent from here, as shown, and the titles it placed (the self-check).</summary>
    public int QueueCount => (_searchShown ? _queueCompact : _queue).Children.Count + (_status?.Placed.Count ?? 0);
    public int ResultGroups => _results.Children.Count;
    public bool Ready => _ready;
    /// <summary>The page at rest shows its series followed and its titles placed side by side (the self-check).</summary>
    public bool AtRest => !_searchShown && _followsSection.IsVisible && _placedSection.IsVisible;
    /// <summary>Types a search and runs it, as the field and Entrée do (the self-check).</summary>
    public Task SearchAsync(string text) { _query.Text = text; return SearchAsync(); }
    /// <summary>« Télécharger » on the best release of the first title found (the self-check).</summary>
    public Task SendBestAsync() => _firstBest is var (search, hit, button) ? SendHitAsync(search, hit, button) : Task.CompletedTask;
    private (string Search, RelayHit Hit, Button Button)? _firstBest;
    /// <summary>« Chercher une meilleure version »: the title asked for, then as Jellyfin has it (its file); whether a better version of it was asked.</summary>
    private readonly MediaItem? _upgradeOf;
    private MediaItem? _upgrade;
    private bool _upgradeStarted, _upgradeSent;
    private readonly Border _upgradeBanner;
    private readonly TextBlock _upgradeTitle = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _upgradeLine = new() { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    private static TextBlock Count18() => new() { FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Ui.Faint, Margin = new Thickness(12, 0, 0, 0) };
    private static TextBlock FoldCount() => new() { FontSize = 14.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.Faint, Margin = new Thickness(10, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock FoldLine() => new() { FontSize = 13, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private static Icon Chevron() => new("next", 14) { Foreground = Ui.Faint, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock Title18(string text) => new() { Text = text, FontSize = 18, FontWeight = FontWeight.SemiBold };
    private static TextBlock Caption(string text) => new() { Text = text, FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = Ui.Faint, VerticalAlignment = VerticalAlignment.Center };
    private static Border Card(Control child, double padding = 5) => new() { Background = Surface, CornerRadius = new CornerRadius(16), Padding = new Thickness(padding), Child = child };

    public DownloadsPage(MainWindow shell, MediaItem? upgrade = null) : base(shell)
    {
        _session = shell.Session;
        _upgradeOf = upgrade;
        // —— Header: the page, where the PC stands; the PC, its free space and its pill ——
        var pill = new Border
        {
            Background = Ui.Raised, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 6), VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Row(8, _pillDot, _pillText)
        };
        _place = Ui.Row(0, new Icon("server", 16) { Foreground = Ui.Faint, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center }, _placeName, _freeRule, _freeText);
        _place.Margin = new Thickness(0, 0, 12, 0); _place.VerticalAlignment = VerticalAlignment.Center; _place.IsVisible = false;
        var right = Ui.Row(0, _place, pill);
        right.VerticalAlignment = VerticalAlignment.Top; right.Margin = new Thickness(24, 10, 0, 0);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 26) };
        DockPanel.SetDock(right, Dock.Right); header.Children.Add(right);
        header.Children.Add(Ui.Column(0, Ui.Heading(L("Téléchargements", "Downloads"), 32, 1, FontWeight.SemiBold), _subtitle));

        // —— The search, a magnet link ——
        _query.InnerLeftContent = new Icon("search", 18) { Foreground = Ui.Muted, Margin = new Thickness(15, 0, 0, 0), IsHitTestVisible = false };
        var clear = Ui.IconButton("close", L("Effacer · Échap", "Clear · Esc"), () => { _query.Text = ""; _query.Focus(); }, "icon", 14);
        clear.Width = clear.Height = 30; clear.Margin = new Thickness(0, 0, 6, 0); clear.IsVisible = false;
        AutomationProperties.SetName(clear, L("Effacer la recherche", "Clear the search"));
        _query.InnerRightContent = clear;
        _query.TextChanged += (_, _) =>
        {
            clear.IsVisible = !string.IsNullOrEmpty(_query.Text);
            // The search emptied: back to the page at rest (the queue in full, the series and the titles).
            if (string.IsNullOrEmpty(_query.Text)) LeaveSearch();
        };
        _query.Padding = new Thickness(10, 13, 4, 13);
        AutomationProperties.SetName(_query, L("Que veux-tu télécharger ?", "What do you want to download?"));
        _query.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; _ = SearchAsync(); }
            // Échap first clears the search, then (a second press) goes back as usual.
            else if (e.Key == Key.Escape && !string.IsNullOrEmpty(_query.Text)) { e.Handled = true; _query.Text = ""; }
        };
        _search.Click += (_, _) => _ = SearchAsync();
        _magnetButton = SmallButton(L("Coller un lien magnet", "Paste a magnet link"), "link", OpenMagnet);
        _magnetButton.VerticalAlignment = VerticalAlignment.Center;
        _searchRow.Children.Add(_query);
        Grid.SetColumn(_search, 1); _searchRow.Children.Add(_search);
        Grid.SetColumn(_searchRule, 2); _searchRow.Children.Add(_searchRule);
        Grid.SetColumn(_magnetButton, 3); _searchRow.Children.Add(_magnetButton);

        AutomationProperties.SetName(_magnet, L("Lien magnet", "Magnet link"));
        _magnet.TextChanged += (_, _) => _magnetError.IsVisible = false;
        _magnet.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; _ = SendMagnetAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; CloseMagnet(); _magnetButton.Focus(); }
        };
        _magnetSend.Click += (_, _) => _ = SendMagnetAsync();
        var close = new Button { Content = L("Fermer", "Close"), Classes = { "quiet" }, Padding = new Thickness(14, 9), Margin = new Thickness(6, 0, 0, 0) };
        close.Click += (_, _) => { CloseMagnet(); _magnetButton.Focus(); };
        var magnetRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        magnetRow.Children.Add(_magnet);
        Grid.SetColumn(_magnetSend, 1); magnetRow.Children.Add(_magnetSend);
        Grid.SetColumn(close, 2); magnetRow.Children.Add(close);
        _magnetPanel = new Border
        {
            Background = Surface, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14), Margin = new Thickness(0, 12, 0, 0), IsVisible = false,
            Child = Ui.Column(0,
                new TextBlock { Text = L("Coller un lien magnet", "Paste a magnet link"), FontSize = 14, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = L("Le lien d’un site que tu connais : TorLink le télécharge sur le PC serveur, puis Mira le range dans ta bibliothèque.", "A link from a site you know: TorLink downloads it on the server PC, then Mira adds it to your library."), FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) },
                magnetRow, _magnetError)
        };
        // « Chercher une meilleure version »: the title replaced, what its file is now, what happens to it; closed, the page as it is.
        var endUpgrade = Ui.IconButton("close", L("Ne plus chercher de meilleure version", "Stop looking for a better version"), EndUpgrade, "icon", 14);
        endUpgrade.Width = endUpgrade.Height = 30; endUpgrade.VerticalAlignment = VerticalAlignment.Top;
        var upgradeHead = new DockPanel();
        DockPanel.SetDock(endUpgrade, Dock.Right); upgradeHead.Children.Add(endUpgrade);
        upgradeHead.Children.Add(Ui.Row(10, new Icon("sparkle", 18) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) }, Ui.Column(0, _upgradeTitle, _upgradeLine)));
        _upgradeBanner = new Border { Background = Surface, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 10, 14), Margin = new Thickness(0, 0, 0, 14), IsVisible = false, Child = upgradeHead };
        var top = Ui.Column(0, _upgradeBanner, _searchRow, _magnetPanel);

        // —— At rest: the queue, then the series followed and the titles placed side by side ——
        var queueHead = Ui.Row(0, Title18(L("Sur le PC serveur", "On the server PC")), _summary);
        queueHead.Margin = new Thickness(0, 32, 0, 0);
        _queueEmpty = Empty("download", L("Rien en cours", "Nothing in progress"), L("Ce que tu envoies apparaît ici avec sa progression et le temps restant, puis rejoint ta bibliothèque.", "What you send shows up here with its progress and time left, then joins your library."), null);
        _queueEmpty.Margin = new Thickness(12, 4, 12, 16);
        var queueCard = Card(new Panel { Children = { _queue, _queueEmpty } }, 0);
        queueCard.Padding = new Thickness(8, 4); queueCard.Margin = new Thickness(0, 14, 0, 0);

        var followsHead = new Grid { ColumnDefinitions = new ColumnDefinitions("50,*,146,104,74"), Height = 30, Margin = new Thickness(12, 0) };
        void Head(int column, string text) { var caption = Caption(text); Grid.SetColumn(caption, column); followsHead.Children.Add(caption); }
        Head(1, L("SÉRIE", "SERIES")); Head(2, L("PROCHAIN ÉPISODE", "NEXT EPISODE")); Head(3, L("PROCHAINE RECHERCHE", "NEXT SEARCH"));
        var followsTitle = Ui.Row(0, Title18(L("Séries suivies", "Followed series")), _followsCount);
        followsTitle.Height = 26;
        var followsCard = Card(Ui.Column(0, followsHead, _follows), 0);
        followsCard.Padding = new Thickness(8, 8, 8, 4); followsCard.Margin = new Thickness(0, 14, 0, 0);
        _followsSection.Children.Add(followsTitle); _followsSection.Children.Add(followsCard);

        _placedAll.Click += (_, _) => { _placedAllShown = !_placedAllShown; if (_status is { } status) RenderPlaced(status); };
        var placedHead = new DockPanel { Height = 26 };
        DockPanel.SetDock(_placedAll, Dock.Right); placedHead.Children.Add(_placedAll);
        placedHead.Children.Add(Ui.Row(0, Title18(L("Rangés dans ta bibliothèque", "Sorted into your library")), _placedCount));
        _placedSection.Children.Add(placedHead); _placedSection.Children.Add(_placed);
        _columns.Children.Add(_followsSection);
        Grid.SetColumn(_placedSection, 2); _columns.Children.Add(_placedSection);
        _resultsEmpty = Empty("search", L("Cherche un film, une série ou un anime", "Search for a movie, a show or an anime"),
            L("TorLink le cherche sur le PC serveur. Mira regroupe les versions par titre et par épisode, et met la meilleure en avant : la qualité de la source d’abord, le 1080p, puis MULTI ou VF.",
                "TorLink searches for it on the server PC. Mira groups the versions by title and by episode, and puts the best one first: the quality of the source first, then 1080p, then MULTI or French audio."),
            L("Ne télécharge que ce que tu as le droit d’obtenir.", "Only download what you have the right to get."));
        _resultsEmpty.Margin = new Thickness(2, 36, 0, 0); _resultsEmpty.MaxWidth = 540; _resultsEmpty.HorizontalAlignment = HorizontalAlignment.Left;
        var rest = Ui.Column(0, queueHead, queueCard, _columns, _resultsEmpty);
        rest.Margin = new Thickness(0, 0, 0, 40);
        _restScroll = new ScrollViewer { Content = rest };

        // —— During a search: the results on the left, the queue tight on the right ——
        _resultsScroll = new ScrollViewer { Content = _results };
        DockPanel.SetDock(_searchStatus, Dock.Top); _resultsColumn.Children.Add(_searchStatus); _resultsColumn.Children.Add(_resultsScroll);
        Grid.SetRowSpan(_resultsColumn, 2); _searchView.Children.Add(_resultsColumn);
        var sideHead = Ui.Row(0, Title18(L("Sur le PC serveur", "On the server PC")), _sideSummary);
        var compactCard = Card(new Panel { Children = { _queueCompact, _queueCompactEmpty } });
        compactCard.Margin = new Thickness(0, 10, 0, 0);
        _followsFold = Fold("calendar", L("Séries suivies", "Followed series"), _followsFoldCount, _followsFoldLine, _followsChevron, () => { _followsOpen = !_followsOpen; Redraw(); });
        _followsCompactCard = Card(_followsCompact);
        _followsCompactCard.Margin = new Thickness(0, 6, 0, 0); _followsCompactCard.IsVisible = false;
        _placedFold = Fold("library", L("Rangés dans ta bibliothèque", "Sorted into your library"), _placedFoldCount, _placedFoldLine, _placedChevron, () => { _placedOpen = !_placedOpen; Redraw(); });
        var side = Ui.Column(0, sideHead, compactCard, _followsFold, _followsCompactCard, _placedFold, _placedCompact);
        side.Margin = new Thickness(0, 0, 0, 40);
        _queueScroll = new ScrollViewer { Content = side };
        Grid.SetColumn(_queueScroll, 2); Grid.SetRowSpan(_queueScroll, 2); _searchView.Children.Add(_queueScroll);

        // —— Why the PC cannot be reached, over the whole page ——
        var retry = new Button { Content = L("Réessayer", "Try again"), Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0), Padding = new Thickness(20, 11) };
        retry.Click += async (_, _) => { _problemHint.Text = L("Lecture du PC serveur…", "Reading the server PC…"); await LoadStatusAsync(); };
        _problemIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _problem.Children.Add(new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 480, Margin = new Thickness(24, 24, 24, 80), Children = { _problemIcon, _problemTitle, _problemHint, retry } });

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Children.Add(top);
        Grid.SetRow(_restScroll, 1); body.Children.Add(_restScroll);
        Grid.SetRow(_searchView, 1); body.Children.Add(_searchView);
        Grid.SetRowSpan(_problem, 2); body.Children.Add(_problem);
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(56, 44 + shell.TitleBarInset, 56, 0) };
        page.Children.Add(header);
        Grid.SetRow(body, 1); page.Children.Add(body);
        body.SizeChanged += (_, e) => Fit(e.NewSize.Width);
        Content = page;

        DescribeAgent(DateTimeOffset.UtcNow);
        _timer.Tick += async (_, _) => await TickAsync();
        // Jellyfin is asked only while the page shows: not from the history, behind a title opened by « Voir ».
        Showing += shown =>
        {
            if (!shown) { _timer.Stop(); return; }
            _timer.Start(); Dispatcher.UIThread.Post(() => { if (IsShowing) _query.Focus(); }, DispatcherPriority.Input);
        };
    }

    /// <summary>A folded section beside a search: its icon, its name and count, what is in it in a line, its chevron.</summary>
    private static Button Fold(string icon, string title, TextBlock count, TextBlock line, Icon chevron, Action click)
    {
        var dock = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Right); dock.Children.Add(chevron);
        var start = Ui.Row(0, new Icon(icon, 18) { Foreground = Ui.Faint, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, count);
        DockPanel.SetDock(start, Dock.Left); dock.Children.Add(start);
        dock.Children.Add(line);
        var button = new Button
        {
            Classes = { "quiet" }, Background = Surface, CornerRadius = new CornerRadius(16), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(18, 0, 16, 0), Height = 52, Margin = new Thickness(0, 10, 0, 0), IsVisible = false, Content = dock
        };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Side by side above <see cref="WideFrom"/>, one under the other below it.</summary>
    private void Fit(double width)
    {
        var narrow = width < WideFrom;
        if (narrow == _narrow && _columns.Tag is not null) { if (_status is { } same && !_placedAllShown) RenderPlaced(same); return; }
        _narrow = narrow; _columns.Tag = true;
        _columns.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,0,0" : "*,40,404");
        Grid.SetColumn(_placedSection, narrow ? 0 : 2); Grid.SetRow(_placedSection, narrow ? 1 : 0);
        _searchView.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,0,0" : "*,36,428");
        Grid.SetColumn(_queueScroll, narrow ? 0 : 2); Grid.SetRowSpan(_queueScroll, narrow ? 1 : 2);
        _queueScroll.MaxHeight = narrow ? 320 : double.PositiveInfinity; _queueScroll.Margin = new Thickness(0, 0, 0, narrow ? 18 : 0);
        Grid.SetRow(_resultsColumn, narrow ? 1 : 0); Grid.SetRowSpan(_resultsColumn, narrow ? 1 : 2);
        _searchRule.IsVisible = !narrow;
        Grid.SetColumn(_magnetButton, narrow ? 0 : 3); Grid.SetRow(_magnetButton, narrow ? 1 : 0); Grid.SetColumnSpan(_magnetButton, narrow ? 4 : 1);
        _magnetButton.HorizontalAlignment = HorizontalAlignment.Left; _magnetButton.Margin = new Thickness(narrow ? -10 : 0, narrow ? 10 : 0, 0, 0);
        // The queue's columns change: its rows are made again.
        foreach (var key in _rows.Keys.Where(x => x.StartsWith("w:", StringComparison.Ordinal)).ToList()) _rows.Remove(key);
        Redraw();
    }

    private void Redraw() { if (_status is { } status) Menus.WhenClosed(() => Render(status, DateTimeOffset.UtcNow)); }

    /// <summary>A search shown (its results on the left, the queue tight on the right), or the page at rest.</summary>
    private void ShowSearch(bool on)
    {
        if (_searchShown == on && (on ? _searchView.IsVisible : _restScroll.IsVisible)) return;
        _searchShown = on;
        _restScroll.IsVisible = !on; _searchView.IsVisible = on;
        Redraw();
    }

    /// <summary>Back to the page at rest: the search is let go (one still running comes to nothing).</summary>
    private void LeaveSearch()
    {
        if (!_searchShown) return;
        if (_searching) { _searchVersion++; _searching = false; _search.Content = L("Chercher", "Search"); _search.IsEnabled = _ready; }
        _results.Children.Clear(); _results.Opacity = 1; _searchStatus.Text = ""; _firstBest = null;
        ShowSearch(false);
    }

    public override async Task LoadAsync()
    {
        await LoadStatusAsync();
        if (_upgradeOf is { } wanted && !_upgradeStarted) { _upgradeStarted = true; await StartUpgradeAsync(wanted); }
    }

    /// <summary>« Chercher une meilleure version »: the title as Jellyfin has it now (its file, its picture), then its search.</summary>
    private async Task StartUpgradeAsync(MediaItem wanted)
    {
        if (_session is not { } session) return;
        MediaItem item;
        try { item = await session.Client.ItemAsync(wanted.Id, session.Lifetime); }
        catch (Exception ex) when (Errors.Expected(ex)) { if (!session.Lifetime.IsCancellationRequested) Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); return; }
        _upgrade = item; _upgradeSent = false;
        _upgradeTitle.Text = L($"Meilleure version de « {TitleUpgrade.Title(item)} »", $"Better version of “{TitleUpgrade.Title(item)}”");
        _upgradeLine.Text = L($"Actuellement : {TitleUpgrade.Line(TitleUpgrade.Current(item))}. Seules les versions meilleures sont montrées. Une fois téléchargée et vérifiée (même durée, lisible par Jellyfin), la nouvelle prend la place de l’ancienne, qui part à la Corbeille du PC serveur.",
            $"Currently: {TitleUpgrade.Line(TitleUpgrade.Current(item))}. Only better versions are shown. Once downloaded and checked (same length, readable by Jellyfin), the new one takes the old one’s place, which goes to the server PC’s Recycle Bin.");
        _upgradeBanner.IsVisible = true;
        AutomationProperties.SetName(_upgradeBanner, _upgradeTitle.Text);
        _query.Text = TitleUpgrade.Query(item);
        await SearchAsync();
    }

    /// <summary>Back to the page as it is: every result, downloaded as a new title.</summary>
    private void EndUpgrade()
    {
        _upgrade = null; _upgradeSent = false; _upgradeBanner.IsVisible = false;
        _query.Text = ""; LeaveSearch(); _query.Focus();
    }
    public override Task RefreshAsync() => LoadStatusAsync();

    private async Task TickAsync()
    {
        // Not under the player, nor while the window is in the Dock or minimized (the timer stops then too: Page.IsShowing).
        if (Shell.Watching || Shell.Away) return;
        await LoadStatusAsync();
    }

    private static EmptyState Empty(string icon, string title, string text, string? note)
    {
        var empty = new EmptyState(centered: false) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 18, 0, 0) };
        empty.Show(icon, title, text, note: note);
        return empty;
    }

    /// <summary>A small quiet button of a row or a tool: its icon, its label, and the name VoiceOver reads.</summary>
    private static Button SmallButton(string label, string icon, Action click, string? name = null)
    {
        var button = new Button
        {
            Classes = { "quiet" }, Padding = new Thickness(10, 6), FontSize = 13,
            Content = Ui.Row(7, new Icon(icon, 15) { VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center })
        };
        button.Click += (_, _) => click();
        AutomationProperties.SetName(button, name ?? label);
        return button;
    }

    // —— What the PC has under way ——

    private async Task LoadStatusAsync()
    {
        if (_session is not { } session) return;
        if (_loading) { _again = true; return; }
        _loading = true; _again = false;
        try
        {
            var status = await session.Client.TorLinkStatusAsync(session.Lifetime);
            _problem.IsVisible = false;
            Render(status, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (Errors.Expected(ex))
        {
            if (session.Lifetime.IsCancellationRequested) return;
            if (ex is RelayRefusedException { StatusCode: HttpStatusCode.Forbidden }) ShowProblem("users", L("Réservé aux administrateurs de Jellyfin", "For Jellyfin administrators only"), ex.Message);
            else if (ex is RelayRefusedException { StatusCode: HttpStatusCode.NotFound }) ShowProblem("refresh", L("L’extension Mira est à mettre à jour", "The Mira extension needs an update"), ex.Message);
            // A page already filled keeps what it shows (a hiccup on Tailscale); an empty one says why.
            else if (_filled) _subtitle.Text = Errors.Friendly(ex);
            else ShowProblem("server", L("Le PC serveur ne répond pas", "The server PC isn’t answering"), Errors.Friendly(ex));
        }
        finally
        {
            _loading = false;
            if (_again) _ = LoadStatusAsync();
        }
    }

    private void ShowProblem(string icon, string title, string hint)
    {
        _problemIcon.Kind = icon; _problemTitle.Text = title; _problemHint.Text = hint;
        _pillText.Text = L("PC serveur · Indisponible", "Server PC · Unavailable"); _pillDot.Fill = Warning;
        _subtitle.Text = PageLine;
        _problem.IsVisible = true;
    }

    /// <summary>The header: where the PC stands, in Mira web's words, its name and free space, and its pill.</summary>
    private void DescribeAgent(DateTimeOffset now)
    {
        var (text, ready) = _filled ? RemoteLines.Agent(_agent, now) : (L("Lecture du PC serveur…", "Reading the server PC…"), false);
        _ready = ready;
        _subtitle.Text = text;
        var (status, dot) = !_filled ? (L("Lecture…", "Reading…"), Ui.Faint) : _agent is null ? (L("Jamais ouvert", "Never opened"), Ui.Faint) : !_agent.Online ? (L("Mira fermé", "Mira closed"), Ui.Faint)
            : !string.IsNullOrEmpty(_agent.Problem) ? (L("Occupé", "Busy"), Warning) : (L("Prêt", "Ready"), Success);
        _pillText.Text = L("PC serveur · ", "Server PC · ") + status; _pillDot.Fill = dot;
        _search.IsEnabled = ready && !_searching;
        _place.IsVisible = !string.IsNullOrEmpty(_agent?.Name);
        _placeName.Text = _agent?.Name ?? "";
        _freeText.Text = _agent?.Free is { } bytes ? RemoteBoard.Free(bytes) : "";
        _freeRule.IsVisible = _freeText.IsVisible = _agent?.Free is not null;
    }

    // —— The whole page from what the PC says ——

    private void Render(RelayStatus status, DateTimeOffset now)
    {
        _filled = true; _agent = status.Agent; _status = status;
        DescribeAgent(now);
        var queue = RemoteBoard.Queue(status);
        var follows = RemoteBoard.Follows(status, now);
        var placed = status.Placed;
        _summary.Text = _sideSummary.Text = RemoteBoard.Summary(queue);
        // The rows of the view shown are kept up to date; those of the other are made when it shows.
        if (_searchShown)
        {
            UpdateRows(_queueCompact, [.. queue.Select(x => CompactLine(x, now))]);
            _queueCompactEmpty.IsVisible = queue.Count == 0;
            _followsFold.IsVisible = follows.Count > 0;
            _followsFoldCount.Text = follows.Count.ToString(Culture); _followsFoldLine.Text = _followsOpen ? "" : RemoteBoard.Coming(status.Follows, now);
            _followsChevron.Kind = _followsOpen ? "down" : "next";
            _followsCompactCard.IsVisible = _followsOpen && follows.Count > 0;
            UpdateRows(_followsCompact, _followsOpen ? [.. follows.Select(x => FollowLine(x, compact: true))] : []);
            _placedFold.IsVisible = placed.Count > 0;
            _placedFoldCount.Text = placed.Count.ToString(Culture); _placedFoldLine.Text = _placedOpen ? "" : RemoteBoard.Latest(placed);
            _placedChevron.Kind = _placedOpen ? "down" : "next";
            _placedCompact.IsVisible = _placedOpen && placed.Count > 0;
            if (_placedOpen) FillPosters(_placedCompact, placed, placed.Count);
        }
        else
        {
            UpdateRows(_queue, [.. queue.Select(x => WideLine(x, now))]);
            _queueEmpty.IsVisible = queue.Count == 0;
            _followsSection.IsVisible = follows.Count > 0;
            _followsCount.Text = follows.Count.ToString(Culture);
            UpdateRows(_follows, [.. follows.Select(x => FollowLine(x, compact: false))]);
            RenderPlaced(status);
            // One under the other: a gap between them only when both show.
            _placedSection.Margin = new Thickness(0, _narrow && follows.Count > 0 ? 32 : 0, 0, 0);
            var any = follows.Count > 0 || placed.Count > 0;
            _columns.IsVisible = any;
            _resultsEmpty.IsVisible = !any;
        }
        // Rows no longer shown are let go (those of the view hidden too: made again when it shows).
        foreach (var key in _rows.Keys.Except(_shownKeys).ToList()) _rows.Remove(key);
        _shownKeys.Clear();
        _ = FetchPostersAsync(queue.Select(x => x.PosterId).Concat(follows.Select(x => x.Follow.SeriesId)).Concat(placed.Select(x => x.ItemId)));
    }

    private void RenderPlaced(RelayStatus status)
    {
        var placed = status.Placed;
        _placedSection.IsVisible = placed.Count > 0;
        _placedCount.Text = placed.Count.ToString(Culture);
        // As many as one row holds; « View all » the rest.
        var width = _narrow ? Math.Max(0, _restScroll.Bounds.Width - 8) : 404;
        var fit = Math.Max(1, (int)((width + 16) / 105));
        _placedAll.IsVisible = placed.Count > fit;
        _placedAll.Content = _placedAllShown ? L("Moins", "Show less") : L("Tout voir", "View all");
        AutomationProperties.SetName(_placedAll, _placedAllShown ? L("Montrer moins de titres rangés", "Show fewer titles sorted") : L("Voir tous les titres rangés", "View all the titles sorted"));
        FillPosters(_placed, placed, _placedAllShown ? placed.Count : fit);
    }

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static double Share(int progress) => Math.Clamp(progress, 0, 100) / 100.0;

    // —— Rows kept between reads ——

    private void UpdateRows(Panel panel, List<RowData> rows)
    {
        var next = new List<QueueRow>();
        foreach (var data in rows)
        {
            _shownKeys.Add(data.Key);
            if (!_rows.TryGetValue(data.Key, out var row) || row.Shape != data.Shape)
                _rows[data.Key] = row = data.Make() with { Shape = data.Shape };
            for (var i = 0; i < Math.Min(row.Texts.Length, data.Texts.Length); i++)
            {
                if (row.Texts[i].Text != data.Texts[i]) { row.Texts[i].Text = data.Texts[i]; if (row.Texts[i].Tag is "keep") ToolTip.SetTip(row.Texts[i], data.Texts[i].Length > 0 ? data.Texts[i] : null); }
                // An empty cell takes no room (a time left unknown, a tight row's second line).
                row.Texts[i].IsVisible = data.Texts[i].Length > 0 || row.Texts[i].Tag is "keep";
            }
            if (row.Bar is not null && data.Progress is { } progress) row.Bar.RenderTransform = new ScaleTransform(Share(progress), 1);
            AutomationProperties.SetName(row.View, string.Join(", ", new[] { data.Name }.Concat(data.Texts).Where(x => x.Length > 0)));
            next.Add(row);
        }
        // A hairline between rows, never above the first.
        for (var i = 0; i < next.Count; i++) if (next[i].Rule is { } rule) rule.IsVisible = i > 0;
        if (next.Count == panel.Children.Count && next.Select((x, i) => ReferenceEquals(x.View, panel.Children[i])).All(x => x)) return;
        panel.Children.Clear();
        foreach (var row in next) { if (row.View.Parent is Panel other) other.Children.Remove(row.View); panel.Children.Add(row.View); }
    }

    /// <summary>A row's frame: its content over a hairline at its top, shown from the second row on.</summary>
    private static (Grid Frame, Border Rule) RowFrame(Control content, double height, Thickness padding)
    {
        var frame = new Grid { Background = Brushes.Transparent };
        var rule = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top, Background = Ui.Raised, Margin = new Thickness(12, 0), IsHitTestVisible = false };
        var body = new Border { Padding = padding, Child = content };
        if (height > 0) body.MinHeight = height;
        frame.Children.Add(body); frame.Children.Add(rule);
        return (frame, rule);
    }

    // —— The queue ——

    private static TextBlock Cell(string text, double size = 13.5, IBrush? color = null, bool right = false, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = text, FontSize = size, Foreground = color ?? Body, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(18, 0, 0, 0), IsVisible = text.Length > 0
    };

    /// <summary>« Suzume (2022) », « Blue Eye Samurai · S01E04 », and « Followed » after it.</summary>
    private static Control LineName(BoardLine line, double size)
    {
        var name = new TextBlock { FontSize = size, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(name, line.Full ?? line.Title);
        name.Inlines!.Add(new Run(line.Title));
        if (line.Year is { } year) name.Inlines.Add(new Run($" ({year})") { Foreground = Ui.Muted, FontWeight = FontWeight.SemiBold });
        if (line.Episode.Length > 0) { name.Inlines.Add(new Run(" · ") { Foreground = Ui.Faint }); name.Inlines.Add(new Run(line.Episode)); }
        if (!line.Followed) return name;
        var row = new DockPanel { HorizontalAlignment = HorizontalAlignment.Left };
        var tag = FollowedTag(L("Suivie", "Followed"));
        DockPanel.SetDock(tag, Dock.Right); row.Children.Add(tag); row.Children.Add(name);
        return row;
    }
    private static Border FollowedTag(string text) => new()
    {
        CornerRadius = new CornerRadius(6), Background = Ui.Raised, Padding = new Thickness(7, 2), Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Ui.TextSecondary }
    };

    /// <summary>A picture Jellyfin has (a poster), else a plain tile with the kind's icon.</summary>
    private Control Thumb(string? id, bool episodic, double width, double height, double radius = 6)
    {
        var tile = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(radius), Background = Ui.Raised, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
        if (id is not null && _posters.GetValueOrDefault(id) is { } item && _session is { } session)
        {
            var image = new Image { Stretch = Stretch.UniformToFill };
            tile.Child = image;
            _ = SetAsync();
            async Task SetAsync() { if (await session.Images.PosterAsync(item, (int)Math.Ceiling(width * 2)) is { } bitmap) image.Source = bitmap; }
        }
        else
        {
            tile.BorderBrush = Ui.Raised2; tile.BorderThickness = new Thickness(1); tile.Background = Surface;
            var size = Math.Min(18, width / 2);
            tile.Child = new Icon(episodic ? "tv" : "film", size) { Foreground = Ui.Faint, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        return tile;
    }
    private string PosterShape(string? id) => id is not null && _posters.GetValueOrDefault(id) is not null ? "img" : "tile";

    private static Button IconAction(string icon, string name, Func<Task> action)
    {
        var button = new Button
        {
            Classes = { "quiet" }, Width = 34, Height = 34, Padding = new Thickness(0), CornerRadius = new CornerRadius(10), Foreground = Ui.TextSecondary,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Content = new Icon(icon, 16)
        };
        ToolTip.SetTip(button, name);
        AutomationProperties.SetName(button, name);
        button.Click += async (_, _) => await action();
        return button;
    }

    /// <summary>What a line can do from here: pause or resume, cancel (asked once more), remove a refused one.</summary>
    private List<Button> LineActions(BoardLine line, RelayStatus? status)
    {
        var name = line.Episode.Length > 0 ? $"{line.Title} · {line.Episode}" : line.Title;
        var buttons = new List<Button>();
        if (line.Download is { } download)
        {
            var pending = status?.Controls.Any(x => x.Target == download.Id) == true;
            if (pending || !RemoteLines.Controllable(download)) return buttons;
            if (download.Status != "failed")
                buttons.Add(RemoteLines.Paused(download) ? IconAction("play", L($"Reprendre « {name} »", $"Resume “{name}”"), () => ControlAsync(download, "resume"))
                    : IconAction("pause", L($"Mettre en pause « {name} »", $"Pause “{name}”"), () => ControlAsync(download, "pause")));
            buttons.Add(IconAction("close", L($"Annuler « {name} »", $"Cancel “{name}”"), () => ConfirmCancelAsync(download, name)));
        }
        else if (line.Request is { State: not "claimed" } request)
        {
            var label = request.State == "waiting" ? L("Annuler", "Cancel") : L("Retirer", "Remove");
            buttons.Add(IconAction("close", L($"{label} « {name} »", $"{label} “{name}”"), () => CancelRequestAsync(request)));
        }
        else if (line.Pick is { State: "waiting" or "failed" } pick)
        {
            var label = pick.State == "waiting" ? L("Annuler", "Cancel") : L("Retirer", "Remove");
            buttons.Add(IconAction("close", L($"{label} « {name} »", $"{label} “{name}”"), () => CancelPickAsync(pick)));
        }
        return buttons;
    }

    /// <summary>« Annuler » deletes what TorLink already has: asked once more.</summary>
    private async Task ConfirmCancelAsync(RelayDownload download, string name)
    {
        if (await Shell.ConfirmAsync(L($"Annuler « {name} » ?", $"Cancel “{name}”?"),
            L("TorLink l’arrête et supprime ce qu’il en a déjà reçu sur le PC serveur.", "TorLink stops it and deletes what it has already received on the server PC."),
            L("Annuler le téléchargement", "Cancel the download"), L("Garder", "Keep")))
            await ControlAsync(download, "delete");
    }

    private static string LineShape(BoardLine line, RelayStatus? status) => line.Download is { } d ? $"d{d.Status}{status?.Controls.Any(x => x.Target == d.Id)}" : line.Request is { } r ? $"r{r.State}" : $"p{line.Pick?.State}";

    /// <summary>The wide row at rest: picture, name and quality, then the bar and the columns (or what it waits for), then its buttons.</summary>
    private RowData WideLine(BoardLine line, DateTimeOffset now)
    {
        var status = _status;
        var download = line.Download;
        var waiting = RemoteBoard.Waiting(line, now) ?? (download is not null && status?.Controls.FirstOrDefault(x => x.Target == download.Id) is { } pending ? RemoteLines.ActionLine(pending.Action) : null);
        var columns = download is not null && waiting is null ? RemoteBoard.Columns(download) : (Amount: "", Rate: "", Left: "", Peers: "");
        string[] texts = waiting is not null ? [waiting] : [RemoteBoard.Percent(download!), columns.Amount, columns.Rate, columns.Left, columns.Peers];
        var shape = $"{LineShape(line, status)}|{waiting is null}|{PosterShape(line.PosterId)}|{_narrow}";
        return new("w:" + line.Key, shape, line.Episode.Length > 0 ? $"{line.Title} · {line.Episode}" : line.Title, texts, download?.Progress, () => MakeWideLine(line, waiting is not null, status));
    }

    private QueueRow MakeWideLine(BoardLine line, bool waits, RelayStatus? status)
    {
        var narrow = _narrow;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(36, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MaxWidth = 290 + 18 });
        // Bar, share, size, speed, time left, peers; the size, time left and peers left out when narrow.
        foreach (var width in narrow ? new double[] { -1, 64, 0, 120, 0, 0 } : [-1, 64, 168, 102, 102, 86])
            grid.ColumnDefinitions.Add(width < 0 ? new ColumnDefinition(1, GridUnitType.Star) : new ColumnDefinition(width, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(90, GridUnitType.Pixel));
        grid.Children.Add(Thumb(line.PosterId, line.Episodic, 36, 54));
        var text = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(LineName(line, 15));
        var quality = new TextBlock { Text = line.Quality.Length > 0 ? line.Quality : line.Full ?? "", FontSize = 12.5, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        if (line.Full is not null) ToolTip.SetTip(quality, line.Full);
        text.Children.Add(quality);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        TextBlock[] texts; Border? bar = null;
        if (waits)
        {
            var failed = line.Download?.Status == "failed" || line.Request?.State == "failed" || line.Pick?.State == "failed";
            var done = line.Download?.Status is "completed" or "seeding";
            var said = new TextBlock { FontSize = 13.5, Foreground = failed ? WarningText : Ui.TextSecondary, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Tag = "keep" };
            var row = new DockPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var icon = new Icon(failed ? "warning" : done ? "check" : "clock", 16) { Foreground = failed ? Warning : done ? Success : Ui.Faint, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon); row.Children.Add(said);
            Grid.SetColumn(row, 2); Grid.SetColumnSpan(row, 6); grid.Children.Add(row);
            texts = [said];
        }
        else
        {
            var paused = line.Download!.Status != "downloading";
            bar = new Border { Background = paused ? Ui.Faint : Accent, CornerRadius = new CornerRadius(2), RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative), RenderTransform = new ScaleTransform(Share(line.Download.Progress), 1) };
            var track = new Border { Height = 4, Margin = new Thickness(18, 0, 0, 0), Background = Line, CornerRadius = new CornerRadius(2), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, Child = bar };
            Grid.SetColumn(track, 2); grid.Children.Add(track);
            var cells = new[] { Cell("", 15, Ui.Ink, right: true, FontWeight.Bold), Cell(""), Cell("", right: true), Cell("", right: true), Cell("", right: true) };
            cells[0].Tag = "keep";
            for (var i = 0; i < cells.Length; i++) { Grid.SetColumn(cells[i], 3 + i); grid.Children.Add(cells[i]); }
            if (paused) cells[2].Foreground = Ui.Muted;
            // Narrow: the size, the time left and the peers have no column (their words are in its name for VoiceOver).
            texts = narrow ? [cells[0], new TextBlock(), cells[2], new TextBlock(), new TextBlock()] : cells;
            if (narrow) cells[1].IsVisible = cells[3].IsVisible = cells[4].IsVisible = false;
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in LineActions(line, status)) actions.Children.Add(button);
        Grid.SetColumn(actions, 8); grid.Children.Add(actions);
        var (frame, rule) = RowFrame(grid, 70, new Thickness(12, 8, 8, 8));
        return new("", frame, texts, bar, rule);
    }

    /// <summary>The tight row beside a search: picture, name, bar and one line of state.</summary>
    private RowData CompactLine(BoardLine line, DateTimeOffset now)
    {
        var status = _status;
        var download = line.Download;
        var said = RemoteBoard.Waiting(line, now) ?? (download is not null && status?.Controls.FirstOrDefault(x => x.Target == download.Id) is { } pending ? RemoteLines.ActionLine(pending.Action) : null) ?? RemoteLines.Download(download!);
        var withBar = download is not null && download.Status is "downloading" or "queued" or "paused" or "stopped";
        var shape = $"{LineShape(line, status)}|{withBar}|{PosterShape(line.PosterId)}";
        return new("c:" + line.Key, shape, line.Episode.Length > 0 ? $"{line.Title} · {line.Episode}" : line.Title, [said], withBar ? download!.Progress : null, () => MakeCompactLine(line, withBar, status));
    }

    private QueueRow MakeCompactLine(BoardLine line, bool withBar, RelayStatus? status)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("36,*,Auto") };
        grid.Children.Add(Thumb(line.PosterId, line.Episodic, 36, 54));
        var text = new StackPanel { Margin = new Thickness(13, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(LineName(line, 14));
        Border? bar = null;
        if (withBar)
        {
            bar = new Border { Background = line.Download!.Status == "downloading" ? Accent : Ui.Faint, CornerRadius = new CornerRadius(2), RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative), RenderTransform = new ScaleTransform(Share(line.Download.Progress), 1) };
            text.Children.Add(new Border { Height = 4, Margin = new Thickness(0, 8, 0, 6), Background = Line, CornerRadius = new CornerRadius(2), ClipToBounds = true, Child = bar });
        }
        var failed = line.Download?.Status == "failed" || line.Request?.State == "failed" || line.Pick?.State == "failed";
        var said = new TextBlock { FontSize = 12, Foreground = failed ? WarningText : Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, withBar ? 0 : 3, 0, 0), Tag = "keep" };
        text.Children.Add(said);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in LineActions(line, status)) actions.Children.Add(button);
        Grid.SetColumn(actions, 2); grid.Children.Add(actions);
        var (frame, rule) = RowFrame(grid, 0, new Thickness(12, 11, 8, 11));
        return new("", frame, [said], bar, rule);
    }

    // —— The series followed ——

    private RowData FollowLine(BoardFollow row, bool compact)
    {
        var follow = row.Follow;
        string[] texts = compact ? [row.Line, string.Join(" · ", new[] { row.NextEpisode, row.Airs, L($"prochaine recherche {row.NextSearch}", $"next search {row.NextSearch}") }.Where(x => x.Length > 0))]
            : [row.Line, row.NextEpisode, row.Airs, row.NextSearch];
        var shape = $"{follow.Query}|{row.Live}|{row.Warning}|{PosterShape(follow.SeriesId)}|{compact}";
        return new((compact ? "c:f:" : "f:") + follow.Id, shape, follow.Name, texts, null, () => MakeFollowLine(row, compact));
    }

    private QueueRow MakeFollowLine(BoardFollow row, bool compact)
    {
        var follow = row.Follow;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(compact ? "45,*,74" : "50,*,146,104,74") };
        var thumb = Thumb(follow.SeriesId, true, 32, 48); thumb.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(thumb);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var name = new TextBlock { FontSize = 14.5, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Inlines!.Add(new Run(follow.Name));
        if (follow.Query is { Length: > 0 } query) name.Inlines.Add(new Run(L($"   cherchée sous « {query} »", $"   searched as “{query}”")) { FontSize = 12, FontWeight = FontWeight.Normal, Foreground = Ui.Faint });
        ToolTip.SetTip(name, follow.Name);
        text.Children.Add(name);
        var line = new TextBlock { FontSize = 12.5, Foreground = row.Warning ? WarningText : row.Live ? Body : Ui.Muted, FontWeight = row.Live ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0), Tag = "keep" };
        text.Children.Add(line);
        TextBlock[] texts;
        if (compact)
        {
            var next = new TextBlock { FontSize = 12, Foreground = Ui.Faint, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0), Tag = "keep" };
            text.Children.Add(next);
            texts = [line, next];
        }
        else
        {
            var number = new TextBlock { FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = Body };
            var airs = new TextBlock { FontSize = 12, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "keep" };
            var episode = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { number, airs } };
            Grid.SetColumn(episode, 2); grid.Children.Add(episode);
            var search = new TextBlock { FontSize = 13.5, Foreground = Body, VerticalAlignment = VerticalAlignment.Center, Tag = "keep" };
            Grid.SetColumn(search, 3); grid.Children.Add(search);
            texts = [line, number, airs, search];
        }
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(IconAction("refresh", L($"Chercher maintenant les nouveaux épisodes de « {follow.Name} »", $"Search now for new episodes of “{follow.Name}”"), () => CheckNowAsync(follow)));
        var edit = new StackPanel { IsVisible = false, Margin = new Thickness(12, 0, 12, 12) };
        Button? more = null;
        more = IconAction("more", L($"Plus d’actions pour « {follow.Name} »", $"More for “{follow.Name}”"), () => { FollowMenu(more!, follow, edit); return Task.CompletedTask; });
        actions.Children.Add(more);
        Grid.SetColumn(actions, compact ? 2 : 4); grid.Children.Add(actions);
        var outer = new StackPanel { Children = { grid, edit } };
        var (frame, rule) = RowFrame(outer, compact ? 0 : 60, compact ? new Thickness(12, 10, 8, 10) : new Thickness(12, 6, 8, 6));
        return new("", frame, texts, null, rule);
    }

    private Task CheckNowAsync(RelayFollow follow) => FollowActionAsync(c => c.TorLinkCheckFollowAsync(follow.Id, _session!.Lifetime),
        L($"Le PC serveur cherche les nouveaux épisodes de « {follow.Name} ».", $"The server PC is looking for new episodes of “{follow.Name}”."));

    /// <summary>The ⋯ of a series followed, in the « Écrin » menu: search now, the name searched, its page, stop following.</summary>
    private void FollowMenu(Button anchor, RelayFollow follow, StackPanel edit)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Entry(L("Chercher maintenant", "Search now"), "refresh", () => _ = CheckNowAsync(follow)));
        menu.Items.Add(Ui.Entry(L("Nom cherché…", "Search name…"), "pencil", () => EditQuery(edit, follow)));
        menu.Items.Add(Ui.Entry(L("Ouvrir la série", "Open the series"), "tv", () => _ = OpenAsync(follow.SeriesId)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Entry(L("Ne plus suivre…", "Stop following…"), "close", () => _ = UnfollowAsync(follow)));
        Ui.OpenUnder(menu, anchor);
    }

    private async Task UnfollowAsync(RelayFollow follow)
    {
        if (await Shell.ConfirmAsync(L($"Ne plus suivre « {follow.Name} » ?", $"Stop following “{follow.Name}”?"),
            L("Le PC serveur ne cherchera plus ses nouveaux épisodes. Ceux déjà dans ta bibliothèque restent.", "The server PC will stop looking for its new episodes. Those already in your library stay."),
            L("Ne plus suivre", "Stop following"), L("Continuer à suivre", "Keep following")))
            await FollowActionAsync(c => c.TorLinkUnfollowAsync(follow.Id, _session!.Lifetime), null);
    }

    /// <summary>The name TorLink searches, changed under the row: empty for the series' own.</summary>
    private void EditQuery(StackPanel edit, RelayFollow follow)
    {
        edit.Children.Clear();
        var box = new TextBox { Text = follow.Query ?? follow.Name, Margin = new Thickness(0, 8, 0, 0), MaxLength = RemoteDownloads.MaxQuery };
        AutomationProperties.SetName(box, L("Nom cherché", "Search name"));
        edit.Children.Add(new TextBlock { Text = L("Le nom sous lequel ses épisodes sortent, par exemple son nom japonais. Vide : son nom dans Jellyfin.", "The name its episodes come out under, its Japanese name for example. Empty: its name in Jellyfin."), FontSize = 12.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        edit.Children.Add(box);
        void Save()
        {
            edit.IsVisible = false;
            var query = SeriesFollowing.ChosenQuery(box.Text, follow.Name);
            _ = FollowActionAsync(c => c.TorLinkFollowAsync(follow.SeriesId, follow.Name, query, _session!.Lifetime), L("Nom cherché changé : le PC serveur cherche de nouveau.", "Search name changed: the server PC is searching again."));
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Save(); }
            else if (e.Key == Key.Escape) { e.Handled = true; edit.IsVisible = false; }
        };
        var buttons = new WrapPanel { Margin = new Thickness(-10, 6, 0, 0) };
        buttons.Children.Add(SmallButton(L("Enregistrer", "Save"), "check", Save, L("Enregistrer le nom cherché", "Save the search name")));
        buttons.Children.Add(SmallButton(L("Annuler", "Cancel"), "close", () => edit.IsVisible = false, L("Garder le nom cherché", "Keep the search name")));
        edit.Children.Add(buttons);
        edit.IsVisible = true;
        Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
    }

    // —— The titles placed ——

    /// <summary>The latest titles placed as posters: a click opens the title Jellyfin has; the one Jellyfin is still adding faded.</summary>
    private void FillPosters(WrapPanel panel, IReadOnlyList<RelayPlaced> placed, int count)
    {
        // Drawn again only when something changed: every 3 s would flicker the pictures and lose the pointer.
        var drawn = string.Join("\n", placed.Take(count).Select(x => $"{x.Id}|{x.Title}|{x.Status}|{x.ItemId}|{x.Ok}|{PosterShape(x.ItemId)}"));
        if (panel.Tag is string before && before == drawn && panel.Children.Count > 0) return;
        panel.Tag = drawn;
        panel.Children.Clear();
        foreach (var title in placed.Take(count))
        {
            var (name, sub) = title.Title.Split(" · ", 2) is [var first, var rest] ? (first, rest) : (title.Title, "");
            var status = RemoteLines.Placed(title.Status);
            var adding = title.ItemId is null && title.Ok;
            if (!title.Ok || adding) sub = Sentence(status);
            var content = new StackPanel { Width = 89 };
            var poster = Thumb(title.ItemId, sub.Length > 0 && title.Ok && !adding, 89, 133, 9);
            if (adding || !title.Ok) poster.Opacity = 0.45;
            content.Children.Add(poster);
            content.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 9, 0, 0) });
            if (sub.Length > 0) content.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = title.Ok ? Ui.Muted : WarningText, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            var button = new Button
            {
                Classes = { "quiet" }, Padding = new Thickness(0), Margin = new Thickness(0, 0, 16, 16), Content = content, VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(9), Background = Brushes.Transparent, Focusable = title.ItemId is not null, IsHitTestVisible = title.ItemId is not null
            };
            AutomationProperties.SetName(button, string.Join(", ", new[] { title.Title, status }.Where(x => x.Length > 0)));
            if (title.ItemId is { } id) button.Click += async (_, _) => await OpenAsync(id);
            panel.Children.Add(button);
        }
    }

    // —— Pictures from Jellyfin ——

    /// <summary>The titles of Jellyfin the page shows a poster of, asked once each (in one request), then the page drawn again.</summary>
    private async Task FetchPostersAsync(IEnumerable<string?> ids)
    {
        if (_postersAsking || _session is not { } session) return;
        var asked = ids.OfType<string>().Where(RemoteDownloads.IsItemId).Distinct(StringComparer.OrdinalIgnoreCase).Where(x => !_posters.ContainsKey(x)).Take(100).ToList();
        if (asked.Count == 0) return;
        _postersAsking = true;
        try
        {
            var found = await session.Client.PostersAsync(asked, session.Lifetime);
            foreach (var id in asked) _posters[id] = found.FirstOrDefault(x => string.Equals(x.Id.Replace("-", ""), id.Replace("-", ""), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (Errors.Expected(ex)) { foreach (var id in asked) _posters[id] = null; }
        finally { _postersAsking = false; }
        if (IsShowing && _status is { } status) Render(status, DateTimeOffset.UtcNow);
    }

    /// <param name="done">Said when the list cannot show it (a search started); null when it does (a series no longer followed leaves it).</param>
    private async Task FollowActionAsync(Func<JellyfinClient, Task> action, string? done)
    {
        if (_session is not { } session) return;
        try { await action(session.Client); if (done is not null) Shell.Notice(done); }
        catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
        await LoadStatusAsync();
    }
    private async Task CancelPickAsync(RelayPick pick)
    {
        if (_session is not { } session) return;
        try
        {
            // Gone from the list, it needs no notice; what the list cannot show is said.
            var said = await session.Client.TorLinkCancelAsync(pick.Id, session.Lifetime) switch
            {
                true => pick.State == "waiting" ? L($"{pick.Episode} annulé : ce fichier ne sera plus pris, un autre de l’épisode pourra l’être.", $"{pick.Episode} canceled: this file won’t be taken again, another one of the episode may be.") : null,
                false => L("Le PC serveur l’a déjà pris : annule-le dans sa liste dès qu’il y apparaît.", "The server PC already took it: cancel it in its list as soon as it shows up there."),
                null => L("Cet épisode n’est plus en attente.", "This episode is no longer waiting.")
            };
            if (said is not null) Shell.Notice(said);
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
        await LoadStatusAsync();
    }

    /// <summary>« Voir »: the title's page, as from the library.</summary>
    private async Task OpenAsync(string id)
    {
        if (_session is not { } session) return;
        try { Shell.Open(await session.ItemAsync(id)); }
        catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
    }

    private async Task CancelRequestAsync(RelayRequest request)
    {
        if (_session is not { } session) return;
        try
        {
            // Gone from the list, it needs no notice; what the list cannot show is said.
            var said = await session.Client.TorLinkCancelAsync(request.Id, session.Lifetime) switch
            {
                true => null,
                false => L("Le PC serveur l’a déjà pris : annule-le dans sa liste dès qu’il y apparaît.", "The server PC already took it: cancel it in its list as soon as it shows up there."),
                null => L("Cette demande n’est plus là.", "This request is no longer there.")
            };
            if (said is not null) Shell.Notice(said);
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
        await LoadStatusAsync();
    }

    private async Task ControlAsync(RelayDownload download, string action)
    {
        if (_session is not { } session) return;
        try
        {
            await session.Client.TorLinkControlAsync(download.Id, action, session.Lifetime);
            if (RemoteLines.ActionLine(action) is { } said) Shell.Notice(said);
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
        await LoadStatusAsync();
    }

    // —— Searching ——

    /// <summary>
    /// A search TorLink runs on the PC of Jellyfin, in films, shows and anime at once, followed every second until it
    /// answers (90 s at most).
    /// </summary>
    private async Task SearchAsync()
    {
        if (_session is not { } session) return;
        if (RemoteDownloads.CleanQuery(_query.Text) is not { } query) { _query.Focus(); return; }
        ShowSearch(true);
        if (!_ready) { _searchStatus.Text = RemoteLines.Agent(_agent, DateTimeOffset.UtcNow).Text; return; }
        var mine = ++_searchVersion; var ct = session.Lifetime;
        _searching = true; _search.IsEnabled = false; _search.Content = L("Recherche…", "Searching…");
        _searchStatus.Text = L($"Recherche de « {query} » dans les films, les séries et les animes sur le PC serveur…", $"Searching for “{query}” in movies, shows and anime on the server PC…");
        _results.Opacity = 0.45;
        void End(string text) { _searchStatus.Text = text; _results.Children.Clear(); }
        try
        {
            var id = await session.Client.TorLinkSearchAsync(query, RemoteDownloads.All, ct);
            var started = DateTime.UtcNow;
            while (true)
            {
                await Task.Delay(1000, ct);
                if (mine != _searchVersion) return;
                if (DateTime.UtcNow - started > SearchWait) { End(L("Le PC serveur n’a pas répondu à temps. Vérifie que Mira y est ouvert, puis relance la recherche.", "The server PC didn’t answer in time. Check that Mira is open there, then search again.")); return; }
                RelaySearch? state;
                // A hiccup on the way: asked again a second later.
                try { state = await session.Client.TorLinkSearchStateAsync(id, ct); }
                catch (Exception ex) when (Errors.Expected(ex) && !ct.IsCancellationRequested && ex is not RelayRefusedException) { continue; }
                if (mine != _searchVersion) return;
                // Forgotten (Jellyfin restarted meanwhile): it will not come back.
                if (state is null) { End(L("Cette recherche a été perdue : relance-la.", "This search was lost: run it again.")); return; }
                if (state.State == "failed") { End(string.IsNullOrEmpty(state.Error) ? L("La recherche n’a pas abouti.", "The search didn’t work.") : state.Error); return; }
                if (state.State == "done") { ShowHits(id, query, state.Results ?? []); return; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (Errors.Expected(ex)) { if (mine == _searchVersion) End(Errors.Friendly(ex)); }
        finally
        {
            if (mine == _searchVersion) { _searching = false; _search.Content = L("Chercher", "Search"); _search.IsEnabled = _ready; _results.Opacity = 1; }
        }
    }

    /// <summary>
    /// The results by title and episode (or season, or film), the best release of each first: its quality and source,
    /// its size and sources, its full name; the other releases folded under it.
    /// </summary>
    private void ShowHits(string searchId, string query, IReadOnlyList<RelayHit> hits)
    {
        ShowSearch(true); _results.Children.Clear(); _resultsScroll.Offset = default; _firstBest = null;
        // « Chercher une meilleure version »: only this title, in a better picture than its file.
        if (_upgrade is { } upgrade)
        {
            var better = TitleUpgrade.Candidates(upgrade, hits);
            var now = TitleUpgrade.Line(TitleUpgrade.Current(upgrade));
            var count = Count(better.Count, "version meilleure", "versions meilleures", "better version", "better versions");
            _searchStatus.Text = better.Count == 0
                ? L($"Aucune version meilleure que l’actuelle ({now}) parmi les {hits.Count} résultats pour « {query} ». Réessaie plus tard, ou avec un autre nom.",
                    $"No version better than the current one ({now}) among the {hits.Count} results for “{query}”. Try again later, or with another name.")
                : L($"{count} que l’actuelle, la meilleure en premier.", $"{count} than the current one, the best first.");
            foreach (var group in ReleaseRanking.Group(better)) _results.Children.Add(GroupView(searchId, group));
            return;
        }
        if (hits.Count == 0) { _searchStatus.Text = L($"Aucun résultat pour « {query} » dans les films, les séries et les animes. Essaie un autre titre.", $"No results for “{query}” in movies, shows and anime. Try another title."); return; }
        var groups = ReleaseRanking.Group(hits);
        var versions = Count(hits.Count, "version", "versions", "version", "versions"); var titles = Count(groups.Count, "titre", "titres", "title", "titles");
        _searchStatus.Text = L($"{versions} pour « {query} », regroupées en {titles}. La meilleure de chacun en premier.", $"{versions} for “{query}”, grouped into {titles}. The best of each first.");
        var views = groups.Select(group => (Group: group, View: GroupView(searchId, group))).ToList();
        foreach (var (_, view) in views) _results.Children.Add(view);
        _ = MarkOwnedAsync(views);
    }

    /// <summary>
    /// « Déjà dans ta bibliothèque » under a title Jellyfin has (OwnedTitles), so that it is not downloaded twice: Jellyfin
    /// asked once per title found, the episodes of a series once, after the results show.
    /// </summary>
    private async Task MarkOwnedAsync(List<(ReleaseGroup Group, Border View)> views)
    {
        if (_session is not { } session) return;
        await Task.WhenAll(OwnedTitles.Lookups(views.Select(x => x.Group)).Select(async lookup =>
        {
            var mine = views.Where(x => OwnedTitles.LookupKey(x.Group) == lookup.Key).ToList();
            List<MediaItem> items, episodes = [];
            try
            {
                items = await OwnedTitles.FindAsync(lookup.Title, [.. mine.Select(x => x.Group)], async text => (await session.Client.OwnedAsync(text)).Items);
                if (!lookup.Film && OwnedTitles.Line(mine[0].Group, items)?.ItemId is { } series && mine.Any(x => !x.Group.Release.Absolute))
                    episodes = (await session.Client.OwnedEpisodesAsync(series)).Items;
            }
            catch (Exception ex) when (Errors.Expected(ex)) { return; } // Nothing said rather than a guess.
            foreach (var (group, view) in mine)
            {
                if (!_results.Children.Contains(view) || view.Child is not StackPanel panel || OwnedTitles.Line(group, items, episodes) is not { } line) continue;
                var button = new Button
                {
                    Classes = { "quiet" }, Padding = new Thickness(10, 5), Margin = new Thickness(-10, -6, 0, 8), HorizontalAlignment = HorizontalAlignment.Left,
                    Foreground = line.Owned ? Warning : Secondary,
                    Content = Ui.Row(7, new Icon(line.Owned ? "check" : "info", 14) { VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = line.Text, FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }),
                };
                AutomationProperties.SetName(button, L($"{line.Text} : ouvrir « {group.Title} »", $"{line.Text}: open “{group.Title}”"));
                var id = line.ItemId;
                button.Click += (_, _) => _ = OpenAsync(id);
                panel.Children.Insert(1, button);
            }
        }));
    }

    private Border GroupView(string searchId, ReleaseGroup group)
    {
        var panel = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        if (group.Label.Length > 0)
        {
            var episode = new Border
            {
                CornerRadius = new CornerRadius(6), Background = Ui.Raised, Padding = new Thickness(9, 4), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = group.Label, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Secondary }
            };
            DockPanel.SetDock(episode, Dock.Right); head.Children.Add(episode);
        }
        if (RemoteLines.KindLabel(group.Category) is { Length: > 0 } kind)
        {
            var tag = new TextBlock { Text = kind, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Secondary, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(tag, Dock.Right); head.Children.Add(tag);
        }
        var title = new TextBlock { Text = group.Title, FontSize = 17, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(title, group.Title);
        head.Children.Add(title);
        panel.Children.Add(head);
        panel.Children.Add(ReleaseView(searchId, group.Best, best: !ReleaseRanking.Dead(group.Best)));
        if (group.Others.Count > 0)
        {
            var others = new StackPanel { IsVisible = false };
            foreach (var entry in group.Others) others.Children.Add(ReleaseView(searchId, entry, best: false));
            var more = Count(group.Others.Count, "autre version", "autres versions", "other version", "other versions");
            var text = new TextBlock { Text = more, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var chevron = new Icon("down", 14) { VerticalAlignment = VerticalAlignment.Center };
            var toggle = new Button { Classes = { "quiet" }, Padding = new Thickness(10, 6), Margin = new Thickness(-10, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Foreground = Secondary, Content = Ui.Row(6, text, chevron) };
            AutomationProperties.SetName(toggle, L($"{more} de {group.Title}", $"{more} of {group.Title}"));
            toggle.Click += (_, _) =>
            {
                others.IsVisible = !others.IsVisible;
                text.Text = others.IsVisible ? L("Masquer les autres versions", "Hide the other versions") : more; chevron.Kind = others.IsVisible ? "up" : "down";
                AutomationProperties.SetName(toggle, others.IsVisible ? L("Masquer les autres versions", "Hide the other versions") : L($"{more} de {group.Title}", $"{more} of {group.Title}"));
            };
            panel.Children.Add(toggle); panel.Children.Add(others);
        }
        var card = new Border { Background = Surface, CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 16, 20, 14), Margin = new Thickness(0, 0, 0, 12), Child = panel };
        AutomationProperties.SetName(card, string.Join(" · ", new[] { group.Title, group.Label, RemoteLines.KindLabel(group.Category) }.Where(x => x.Length > 0)));
        return card;
    }

    private Control ReleaseView(string searchId, RankedRelease entry, bool best)
    {
        var hit = entry.Hit;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        var quality = new WrapPanel();
        quality.Children.Add(new TextBlock { Text = ReleaseRanking.QualityLine(entry.Release) is { Length: > 0 } line ? line : L("Qualité non précisée", "Quality not given"), FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (best)
            quality.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(5), Background = Accent, Padding = new Thickness(7, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = L("Meilleur choix", "Best pick"), FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = OnAccent }
            });
        text.Children.Add(quality);
        text.Children.Add(new TextBlock { Text = RemoteLines.Hit(hit), FontSize = 12.5, Foreground = Secondary, Margin = new Thickness(0, 4, 0, 0) });
        var name = new TextBlock { Text = hit.Name, FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
        ToolTip.SetTip(name, hit.Name);
        text.Children.Add(name);
        if (ReleaseRanking.Warning(entry) is { Length: > 0 } caution)
            text.Children.Add(new TextBlock { Text = caution, FontSize = 12.5, Foreground = WarningText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        grid.Children.Add(text);
        var key = searchId + "/" + hit.Id;
        var button = new Button { Padding = new Thickness(16, 9), VerticalAlignment = VerticalAlignment.Center };
        if (best) _firstBest ??= (searchId, hit, button);
        if (best) button.Classes.Add("primary");
        ShowSendState(button, _sent.Contains(key));
        AutomationProperties.SetName(button, _upgrade is null ? L($"Télécharger {hit.Name}", $"Download {hit.Name}") : L($"Remplacer par {hit.Name}", $"Replace with {hit.Name}"));
        button.Click += async (_, _) => await SendHitAsync(searchId, hit, button);
        Grid.SetColumn(button, 1); grid.Children.Add(button);
        if (best) return grid;
        // The other versions, each under a hairline.
        return new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0), Margin = new Thickness(0, 12, 0, 0), Child = grid };
    }

    private void ShowSendState(Button button, bool sent)
    {
        var replace = _upgrade is not null;
        button.Content = Ui.Row(7, new Icon(sent ? "check" : replace ? "sparkle" : "download", 15) { VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = sent ? replace ? L("Demandé", "Requested") : L("Envoyé", "Sent") : replace ? L("Remplacer", "Replace") : L("Télécharger", "Download"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        button.IsEnabled = !sent;
    }

    private async Task SendHitAsync(string searchId, RelayHit hit, Button button)
    {
        if (_session is not { } session) return;
        var replace = _upgrade?.Id;
        // One better version at a time: the second would only replace the first. A Mira of 0.8.5 on the PC would keep both.
        if (replace is not null && _upgradeSent) { Shell.Notice(L("Une meilleure version de ce titre est déjà demandée : elle s’affiche à droite.", "A better version of this title is already requested: it shows on the right.")); return; }
        if (replace is not null && _agent is { Replaces: false }) { Shell.Notice(L("Mira sur le PC serveur ne sait pas encore remplacer un titre : mets-le à jour.", "Mira on the server PC can’t replace a title yet: update it.")); return; }
        button.IsEnabled = false;
        try
        {
            await session.Client.TorLinkRequestAsync(searchId, hit, session.Lifetime, replace);
            _sent.Add(searchId + "/" + hit.Id); ShowSendState(button, sent: true);
            if (replace is not null) _upgradeSent = true;
            await SentAsync(replace is not null);
        }
        catch (Exception ex) when (Errors.Expected(ex)) { button.IsEnabled = true; Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
    }

    /// <summary>
    /// Its row, with its progress, is at the top of the right column, where the list goes back to: that says it is on its
    /// way. A notice only for what the row cannot show: a better version replacing the old one, a request kept until Mira
    /// opens on the server PC.
    /// </summary>
    private async Task SentAsync(bool replacing = false)
    {
        if (replacing) Shell.Notice(L("Demandé au PC serveur : la nouvelle version prendra la place de l’ancienne une fois téléchargée et vérifiée.", "Requested from the server PC: the new version will take the old one’s place once downloaded and checked."));
        else if (!_ready) Shell.Notice(L("Demande gardée : le PC serveur la fera dès que Mira y sera ouvert.", "Request kept: the server PC will do it as soon as Mira is open there."));
        _queueScroll.Offset = default;
        await LoadStatusAsync();
    }

    // —— A magnet link ——

    private void OpenMagnet()
    {
        _magnetPanel.IsVisible = true; _magnetButton.IsVisible = _magnetError.IsVisible = false;
        Dispatcher.UIThread.Post(() => _magnet.Focus(), DispatcherPriority.Input);
    }
    private void CloseMagnet()
    {
        _magnetPanel.IsVisible = _magnetError.IsVisible = false; _magnetButton.IsVisible = true;
        _magnet.Text = ""; _magnetSend.IsEnabled = true;
    }

    private async Task SendMagnetAsync()
    {
        if (_session is not { } session || !_magnetSend.IsEnabled) return;
        var magnet = (_magnet.Text ?? "").Trim();
        if (RemoteDownloads.InfoHash(magnet) is null) { ShowMagnetError(L("Ce n’est pas un lien magnet (« magnet:?xt=urn:btih:… »).", "That’s not a magnet link (“magnet:?xt=urn:btih:…”).")); _magnet.Focus(); return; }
        _magnetSend.IsEnabled = false;
        try
        {
            await session.Client.TorLinkRequestAsync(magnet, session.Lifetime);
            CloseMagnet(); _magnetButton.Focus();
            await SentAsync();
        }
        catch (Exception ex) when (Errors.Expected(ex)) { ShowMagnetError(Errors.Friendly(ex)); }
        finally { _magnetSend.IsEnabled = true; }
    }
    private void ShowMagnetError(string text) { _magnetError.Text = text; _magnetError.IsVisible = true; }
}
