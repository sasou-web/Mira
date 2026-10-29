using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Identifier… » (the page of a film or series, for an administrator), as Windows and Mira web do it
/// (<see cref="TitleIdentity"/>): Jellyfin searches its metadata sources (it speaks to TMDb, this Mac never does), the
/// posters of its proposals come through Mira's extension, and the one chosen, asked once more, is given to the title.
/// </summary>
public sealed class IdentifyWindow : Window
{
    private readonly Session _session;
    private readonly MediaItem _item;
    private readonly TextBox _name = new() { Watermark = L("Titre, ou lien TMDb ou IMDb", "Title, or a TMDb or IMDb link") };
    private readonly TextBox _year = new() { Width = 80, MaxLength = 4, Watermark = L("Année", "Year") };
    private readonly TextBlock _status = new() { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _results = new() { Spacing = 4 };
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _confirm = new() { Spacing = 12, IsVisible = false };
    private int _version;
    private bool _applying;

    /// <summary>True once Jellyfin gave the title the proposal chosen (its page is drawn again).</summary>
    public bool Applied { get; private set; }
    /// <summary>What to say once the window closes.</summary>
    public string? Said { get; private set; }

    public IdentifyWindow(Session session, MediaItem item)
    {
        _session = session; _item = item;
        Title = L($"Identifier « {TitleIdentity.Named(item)} »", $"Identify “{TitleIdentity.Named(item)}”"); Width = 620; Height = 660; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var (name, year) = TitleIdentity.Prefill(item);
        _name.Text = name; _year.Text = year?.ToString(Culture) ?? "";
        Avalonia.Automation.AutomationProperties.SetName(_name, L("Titre à chercher, ou lien TMDb ou IMDb", "Title to search for, or a TMDb or IMDb link"));
        Avalonia.Automation.AutomationProperties.SetName(_year, L("Année", "Year"));
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_status, Avalonia.Automation.AutomationLiveSetting.Polite);
        var search = Ui.Action(L("Chercher", "Search"), "search", "primary", () => _ = SearchAsync());
        var line = new DockPanel();
        DockPanel.SetDock(search, Dock.Right); DockPanel.SetDock(_year, Dock.Right);
        _year.Margin = new Thickness(10, 0); line.Children.Add(search); line.Children.Add(_year); line.Children.Add(_name);
        _scroll = new ScrollViewer { Content = _results, MaxHeight = 400 };
        var close = Ui.Action(L("Fermer", "Close"), null, "quiet", Close);
        close.HorizontalAlignment = HorizontalAlignment.Left;
        Content = new Border
        {
            Padding = new Thickness(32, 28),
            Child = Ui.Column(14,
                Ui.Text(TitleIdentity.Named(item), 24, FontWeight.Bold),
                new TextBlock { Text = L("C’est Jellyfin qui cherche, auprès de ses sources (TMDb…) : ce Mac ne leur parle pas. Un lien vers la page TMDb ou IMDb du titre tombe juste.", "Jellyfin does the searching, with its sources (TMDb…): this Mac never talks to them. A link to the title’s TMDb or IMDb page finds exactly that one."), FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap },
                line, _status, _scroll, _confirm, close),
        };
        foreach (var box in new[] { _name, _year }) box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = SearchAsync(); } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_applying) { Close(); e.Handled = true; } };
        Opened += (_, _) => { _name.Focus(); _name.SelectAll(); _ = SearchAsync(); };
    }

    private async Task SearchAsync()
    {
        if (_applying) return;
        var query = TitleIdentity.Search(_name.Text, _year.Text);
        if (query.Name.Length == 0 && query.ProviderIds.Count == 0) { _status.Text = L("Écris le titre à chercher.", "Type the title to search for."); _name.Focus(); return; }
        var version = ++_version;
        _confirm.IsVisible = false; _scroll.IsVisible = true; _results.Children.Clear();
        _status.Text = L("Jellyfin cherche…", "Jellyfin is searching…");
        try
        {
            var found = await _session.Client.FindMatchesAsync(_item, query, _session.Lifetime);
            if (version != _version) return;
            _status.Text = found.Count > 0 ? "" : L("Rien trouvé. Essaie le titre d’origine, sans l’année, ou colle le lien de sa page TMDb.", "Nothing found. Try the original title, without the year, or paste the link to its TMDb page.");
            foreach (var match in found.Take(12)) _results.Children.Add(Row(match, version));
        }
        catch (OperationCanceledException) when (_session.Lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (Errors.Expected(ex)) { if (version == _version) _status.Text = Errors.Friendly(ex); }
    }

    private Button Row(RemoteMatch match, int version)
    {
        var poster = new Image { Width = 56, Height = 84, Stretch = Stretch.UniformToFill };
        var frame = new Border { Width = 56, Height = 84, CornerRadius = new CornerRadius(6), Background = Ui.Raised, ClipToBounds = true, Child = poster, VerticalAlignment = VerticalAlignment.Top };
        var text = new StackPanel { Spacing = 3, Margin = new Thickness(14, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = match.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (TitleIdentity.Line(match) is { Length: > 0 } line) text.Children.Add(Ui.Text(line, 12.5, color: Ui.Muted));
        if (match.Overview is { } overview) text.Children.Add(new TextBlock { Text = OverviewText.Plain(overview, flat: true), FontSize = 12.5, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis });
        var content = new DockPanel();
        DockPanel.SetDock(frame, Dock.Left); content.Children.Add(frame); content.Children.Add(text);
        var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 8) };
        button.Classes.Add("quiet");
        Avalonia.Automation.AutomationProperties.SetName(button, string.Join(", ", new[] { match.Name, TitleIdentity.Line(match) }.Where(x => x.Length > 0)));
        button.Click += (_, _) => Confirm(match);
        if (match.ImageUrl is { } url) _ = PosterAsync(url, poster, version);
        return button;
    }
    private async Task PosterAsync(string url, Image target, int version)
    {
        try
        {
            if (await _session.Client.MatchImageAsync(url, _session.Lifetime) is not { } bytes || version != _version) return;
            using var stream = new MemoryStream(bytes);
            target.Source = Bitmap.DecodeToWidth(stream, 112);
        }
        // Only a picture: the proposal is there without it.
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException || Errors.Expected(ex)) { }
    }

    private static string Chosen(RemoteMatch match) => match.Year is { } year ? $"{match.Name} ({year})" : match.Name;

    private void Confirm(RemoteMatch match)
    {
        if (_applying) return;
        _status.Text = "";
        var apply = Ui.Action(L("Remplacer", "Replace"), null, "primary", () => { });
        var back = Ui.Action(L("Retour", "Back"), null, "quiet", () => { _confirm.IsVisible = false; _scroll.IsVisible = true; _status.Text = ""; });
        apply.Click += async (_, _) => await ApplyAsync(match, apply);
        _confirm.Children.Clear();
        _confirm.Children.Add(new TextBlock { Text = L($"« {TitleIdentity.Named(_item)} » devient « {Chosen(match)} » ?", $"“{TitleIdentity.Named(_item)}” becomes “{Chosen(match)}”?"), FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        _confirm.Children.Add(new TextBlock { Text = L("Jellyfin remplace son titre, son résumé, ses affiches et sa distribution par ceux de ce titre, sur chaque appareil. Le fichier ne bouge pas.", "Jellyfin replaces its title, summary, posters and cast with this title’s, on every device. The file stays where it is."), FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap });
        _confirm.Children.Add(Ui.Row(10, apply, back));
        _scroll.IsVisible = false; _confirm.IsVisible = true;
        apply.Focus();
    }

    private async Task ApplyAsync(RemoteMatch match, Button apply)
    {
        if (_applying) return;
        _applying = true; apply.IsEnabled = false;
        _status.Text = L($"Jellyfin prend les informations de « {Chosen(match)} »…", $"Jellyfin is taking the details of “{Chosen(match)}”…");
        try
        {
            var done = await _session.Client.ApplyMatchAsync(_item.Id, match, _session.Lifetime);
            Applied = true;
            Said = done ? L($"« {Chosen(match)} » : Jellyfin a mis à jour sa fiche.", $"“{Chosen(match)}”: Jellyfin updated its page.")
                : L($"Jellyfin prend « {Chosen(match)} » : il lui faut un peu plus de temps, la fiche se met à jour dans un moment.", $"Jellyfin is taking “{Chosen(match)}”: it needs a little longer, the page updates in a moment.");
            _applying = false;
            Close();
        }
        catch (OperationCanceledException) when (_session.Lifetime.IsCancellationRequested) { _applying = false; Close(); }
        catch (Exception ex) when (Errors.Expected(ex)) { _status.Text = Errors.Friendly(ex); apply.IsEnabled = true; _applying = false; }
    }
}
