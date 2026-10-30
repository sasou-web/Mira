using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// Titles Jellyfin recognized badly (TitleIdentity), for an administrator, as Mira web and Mac do it: « Identifier… »
// and « Actualiser les métadonnées » in the « … » of a film's or series' page; « Titres à corriger » in Réglages →
// Jellyfin. Jellyfin searches its metadata sources (it speaks to TMDb, this PC never does); the posters of its
// proposals come through Mira's extension.
public partial class MainWindow
{
    /// <summary>Whether the account of a client administers Jellyfin, once Jellyfin said.</summary>
    private (JellyfinClient Client, bool Admin)? _adminOf;
    private MediaItem? _identifying;
    private RemoteMatch? _identifyChoice;
    private int _identifyVersion;
    private bool _identifyApplying;
    private List<TitleIdentity.Entry> _fixEntries = [];
    private bool _fixAll, _fixBusy;
    private const int FixShown = 8;

    private async Task<bool> IsAdministratorAsync(JellyfinClient client)
    {
        if (_adminOf is { } known && ReferenceEquals(known.Client, client)) return known.Admin;
        if (await client.IsAdministratorAsync(_connectionLifetime.Token) is not { } admin) return false;
        if (ReferenceEquals(client, _client)) _adminOf = (client, admin);
        return admin;
    }

    /// <summary>The « … » menu's entries of an administrator, above the files'.</summary>
    private void AddIdentifyActions(ContextMenu menu, MediaItem item)
    {
        menu.Items.Add(MenuAction(L("Identifier…", "Identify…"), "search", () => { ShowIdentify(item); return Task.CompletedTask; }));
        menu.Items.Add(MenuAction(L("Actualiser les métadonnées", "Refresh metadata"), "refresh", () => RefreshTitleAsync(item)));
    }

    private async Task RefreshTitleAsync(MediaItem item)
    {
        if (_client is not { } client) return;
        try
        {
            await client.RefreshMetadataAsync(item.Id, _connectionLifetime.Token);
            SetNotice(L($"Jellyfin relit « {item.Name} » : la fiche se met à jour dans un moment.", $"Jellyfin is reading “{item.Name}” again: the page updates in a moment."));
            // Queued by Jellyfin: a film takes a few seconds, a series longer.
            foreach (var wait in new[] { 6, 14 })
            {
                await Task.Delay(TimeSpan.FromSeconds(wait), _connectionLifetime.Token);
                if (_detail?.Id == item.Id && DetailOverlay.Visibility == Visibility.Visible) await RefreshDetailAsync();
            }
        }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); }
    }

    // —— « Identifier… » ——
    private void ShowIdentify(MediaItem item)
    {
        _identifying = item; _identifyChoice = null; ++_identifyVersion;
        var (name, year) = TitleIdentity.Prefill(item);
        IdentifyTitle.Text = TitleIdentity.Named(item);
        IdentifyName.Text = name; IdentifyYear.Text = year?.ToString(Lang.Culture) ?? "";
        IdentifySearch.Content = ActionLabel("search", L("Chercher", "Search"));
        IdentifyResults.Children.Clear(); IdentifyStatus.Text = "";
        IdentifyConfirm.Visibility = Visibility.Collapsed; IdentifyScroll.Visibility = Visibility.Visible;
        Motion.Reveal(IdentifyOverlay, 260, 0); Motion.Reveal(IdentifyCard, 360, 16);
        _ = Dispatcher.BeginInvoke(() => { IdentifyName.Focus(); IdentifyName.SelectAll(); }, DispatcherPriority.Input);
        _ = SearchMatchesAsync();
    }
    private void IdentifyBox_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; _ = SearchMatchesAsync(); } }
    private void IdentifySearch_Click(object sender, RoutedEventArgs e) => _ = SearchMatchesAsync();

    private async Task SearchMatchesAsync()
    {
        if (_client is not { } client || _identifying is not { } item || _identifyApplying) return;
        var query = TitleIdentity.Search(IdentifyName.Text, IdentifyYear.Text);
        if (query.Name.Length == 0 && query.ProviderIds.Count == 0) { IdentifyStatus.Text = L("Écris le titre à chercher.", "Type the title to search for."); IdentifyName.Focus(); return; }
        var version = ++_identifyVersion;
        IdentifyConfirm.Visibility = Visibility.Collapsed; IdentifyScroll.Visibility = Visibility.Visible;
        IdentifyResults.Children.Clear(); IdentifyStatus.Text = L("Jellyfin cherche…", "Jellyfin is searching…"); IdentifySearch.IsEnabled = false;
        try
        {
            var found = await client.FindMatchesAsync(item, query, _connectionLifetime.Token);
            if (version != _identifyVersion) return;
            IdentifyStatus.Text = found.Count > 0 ? "" : L("Rien trouvé. Essaie le titre d’origine, sans l’année, ou colle le lien de sa page TMDb.", "Nothing found. Try the original title, without the year, or paste the link to its TMDb page.");
            foreach (var match in found.Take(12)) IdentifyResults.Children.Add(MatchRow(client, match, version));
        }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { if (version == _identifyVersion) IdentifyStatus.Text = Friendly(ex); }
        finally { if (version == _identifyVersion) IdentifySearch.IsEnabled = true; }
    }

    /// <summary>A proposal: its poster (through the extension), its name, year and source, the start of its summary.</summary>
    private Button MatchRow(JellyfinClient client, RemoteMatch match, int version)
    {
        var poster = new Image { Width = 56, Height = 84, Stretch = Stretch.UniformToFill };
        var frame = new Border { Width = 56, Height = 84, CornerRadius = new CornerRadius(6), Background = Palette.Get("Raised2"), ClipToBounds = true, Child = poster, VerticalAlignment = VerticalAlignment.Top };
        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = match.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (TitleIdentity.Line(match) is { Length: > 0 } line) text.Children.Add(new TextBlock { Text = line, FontSize = 12, Foreground = Palette.Get("Muted"), Margin = new Thickness(0, 3, 0, 0) });
        if (match.Overview is { } overview) text.Children.Add(new TextBlock { Text = OverviewText.Plain(overview, flat: true), FontSize = 12, FontWeight = FontWeights.Normal, Foreground = Palette.Get("TextBody"), TextWrapping = TextWrapping.Wrap, MaxHeight = 38, TextTrimming = TextTrimming.WordEllipsis, Margin = new Thickness(0, 5, 0, 0) });
        var content = new DockPanel();
        DockPanel.SetDock(frame, Dock.Left); content.Children.Add(frame); content.Children.Add(text);
        var button = new Button { Style = (Style)FindResource("Quiet"), Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(-10, 0, -10, 2) };
        AutomationProperties.SetName(button, string.Join(", ", new[] { match.Name, TitleIdentity.Line(match) }.Where(x => x.Length > 0)));
        button.Click += (_, _) => ShowIdentifyConfirm(match);
        if (match.ImageUrl is { } url) _ = LoadMatchPosterAsync(client, url, poster, version);
        return button;
    }
    private async Task LoadMatchPosterAsync(JellyfinClient client, string url, Image target, int version)
    {
        try
        {
            if (await client.MatchImageAsync(url, _connectionLifetime.Token) is not { } bytes || version != _identifyVersion) return;
            target.Source = await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 112; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                return bitmap;
            });
        }
        // Only a picture: the proposal is there without it.
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException || IsExpected(ex)) { }
    }

    private void ShowIdentifyConfirm(RemoteMatch match)
    {
        if (_identifying is not { } item || _identifyApplying) return;
        _identifyChoice = match;
        IdentifyQuestion.Text = L($"« {TitleIdentity.Named(item)} » devient « {Chosen(match)} » ?", $"“{TitleIdentity.Named(item)}” becomes “{Chosen(match)}”?");
        IdentifyStatus.Text = "";
        IdentifyScroll.Visibility = Visibility.Collapsed; IdentifyConfirm.Visibility = Visibility.Visible;
        IdentifyApply.IsEnabled = true; IdentifyApply.Focus();
    }
    private static string Chosen(RemoteMatch match) => match.Year is { } year ? $"{match.Name} ({year})" : match.Name;
    private void IdentifyBack_Click(object sender, RoutedEventArgs e)
    {
        if (_identifyApplying) return;
        IdentifyConfirm.Visibility = Visibility.Collapsed; IdentifyScroll.Visibility = Visibility.Visible; IdentifyStatus.Text = "";
        IdentifyResults.Children.OfType<Button>().FirstOrDefault()?.Focus();
    }

    private async void IdentifyApply_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _identifying is not { } item || _identifyChoice is not { } match || _identifyApplying) return;
        _identifyApplying = true; IdentifyApply.IsEnabled = false;
        IdentifyStatus.Text = L($"Jellyfin prend les informations de « {Chosen(match)} »…", $"Jellyfin is taking the details of “{Chosen(match)}”…");
        try
        {
            var done = await client.ApplyMatchAsync(item.Id, match, _connectionLifetime.Token);
            if (!ReferenceEquals(client, _client)) return;
            await CloseIdentifyAsync();
            SetNotice(done ? L($"« {Chosen(match)} » : Jellyfin a mis à jour sa fiche.", $"“{Chosen(match)}”: Jellyfin updated its page.")
                : L($"Jellyfin prend « {Chosen(match)} » : il lui faut un peu plus de temps, la fiche se met à jour dans un moment.", $"Jellyfin is taking “{Chosen(match)}”: it needs a little longer, the page updates in a moment."));
            // Its page drawn again, with its new name and pictures; the library and the list follow.
            if (_detail?.Id == item.Id && DetailOverlay.Visibility == Visibility.Visible) { _metadata?.Clear(); await ShowDetailsAsync(item); }
            _ = RefreshAsync(quiet: true);
            if (FixRow.Visibility == Visibility.Visible) _ = ShowFixAsync();
        }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { IdentifyStatus.Text = Friendly(ex); IdentifyApply.IsEnabled = true; }
        finally { _identifyApplying = false; }
    }

    private async void IdentifyClose_Click(object sender, RoutedEventArgs e) => await CloseIdentifyAsync();
    private async Task CloseIdentifyAsync()
    {
        ++_identifyVersion;
        await Motion.HideAsync(IdentifyOverlay);
        if (DetailOverlay.Visibility == Visibility.Visible) FilesButton.Focus();
    }

    // —— « Titres à corriger » (Réglages → Jellyfin) ——
    /// <summary>Read each time the tab opens; hidden for another account, or while Jellyfin recognized everything.</summary>
    private async Task ShowFixAsync()
    {
        if (_demo || _client is not { } client) { FixRow.Visibility = Visibility.Collapsed; return; }
        if (_fixBusy) return;
        _fixBusy = true;
        var ct = _connectionLifetime.Token;
        try
        {
            if (!await IsAdministratorAsync(client)) { FixRow.Visibility = Visibility.Collapsed; return; }
            var titles = await client.TitlesToCheckAsync(ct);
            var entries = await Task.Run(() => TitleIdentity.Check(titles), ct);
            if (ReferenceEquals(client, _client)) { _fixEntries = entries; RenderFix(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        // Jellyfin out of reach: the rest of the tab says so already; this comes back with it.
        catch (Exception ex) when (IsExpected(ex)) { }
        finally { _fixBusy = false; }
    }
    private void FixMore_Click(object sender, RoutedEventArgs e) { _fixAll = !_fixAll; RenderFix(); }
    private void RenderFix()
    {
        FixPanel.Children.Clear();
        FixRow.Visibility = _fixEntries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var unknown = _fixEntries.Count(x => x.Problems.Contains(TitleIdentity.Problem.Unidentified));
        var count = Lang.Count(_fixEntries.Count, "titre", "titres", "title", "titles");
        FixStatus.Text = unknown > 0
            ? L($"{Lang.Count(unknown, "titre non reconnu", "titres non reconnus", "", "")} par Jellyfin, sur {_fixEntries.Count} à revoir.", $"{Lang.Count(unknown, "", "", "title", "titles")} Jellyfin didn’t recognize, of {_fixEntries.Count} to check.")
            : L($"{count} à revoir.", $"{count} to check.");
        foreach (var entry in _fixAll ? _fixEntries : _fixEntries.Take(FixShown))
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = TitleIdentity.Named(entry.Item), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = TitleIdentity.Text(entry), Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 3, 0, 0) });
            var item = entry.Item;
            var identify = new Button { Content = L("Identifier", "Identify"), Style = (Style)FindResource("Quiet"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            AutomationProperties.SetName(identify, L($"Identifier « {TitleIdentity.Named(item)} »", $"Identify “{TitleIdentity.Named(item)}”"));
            identify.Click += (_, _) => ShowIdentify(item);
            var open = new Button { Content = L("Ouvrir", "Open"), Style = (Style)FindResource("Quiet"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            AutomationProperties.SetName(open, L($"Ouvrir la fiche de « {TitleIdentity.Named(item)} »", $"Open the page of “{TitleIdentity.Named(item)}”"));
            open.Click += async (_, _) => { AutoSaveSettings(); await ShowDetailsAsync(item); };
            var row = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            DockPanel.SetDock(identify, Dock.Right); DockPanel.SetDock(open, Dock.Right);
            row.Children.Add(identify); row.Children.Add(open); row.Children.Add(text);
            FixPanel.Children.Add(row);
        }
        FixMore.Visibility = _fixEntries.Count > FixShown ? Visibility.Visible : Visibility.Collapsed;
        FixMore.Content = _fixAll ? L("Voir moins", "Show fewer") : L($"Voir les {_fixEntries.Count}", $"See all {_fixEntries.Count}");
    }
}
