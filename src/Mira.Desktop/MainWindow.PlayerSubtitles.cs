using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// « Sous-titres gardés sur Jellyfin » in the player (JellyfinSubtitles): a file loaded with « Charger un fichier… » can
// be kept on Jellyfin, which every device then offers; « Chercher des sous-titres… », shown only when Jellyfin has a
// subtitle extension, says what leaves the server, asks it, keeps the one picked and plays it.
public partial class MainWindow
{
    private (JellyfinClient Client, Task<IReadOnlyList<string>> Providers)? _subtitleProviders;
    /// <summary>The search on screen: an answer for an earlier one (another language, another title) is dropped.</summary>
    private int _subtitleSearch;
    private string _subtitleLanguage = "";
    private bool _keepingSubtitle;

    /// <summary>Jellyfin's subtitle extensions, asked once per account (again after a failure); none in the demo.</summary>
    private Task<IReadOnlyList<string>> SubtitleProvidersAsync()
    {
        if (_demo || _client is not { } client) return Task.FromResult<IReadOnlyList<string>>([]);
        if (_subtitleProviders is { } known && known.Client == client && !known.Providers.IsFaulted) return known.Providers;
        var providers = client.SubtitleProvidersAsync(_connectionLifetime.Token);
        _subtitleProviders = (client, providers);
        return providers;
    }
    /// <summary>« Chercher des sous-titres… » under « Charger un fichier… », once Jellyfin says it has an extension to ask.</summary>
    private async Task ShowSubtitleSearchAsync()
    {
        SubtitleSearchButton.Visibility = Visibility.Collapsed;
        var generation = _playGeneration;
        IReadOnlyList<string> providers;
        try { providers = await SubtitleProvidersAsync(); }
        catch (Exception e) when (IsExpected(e)) { return; }
        if (generation == _playGeneration && _session.Item is not null && providers.Count > 0) SubtitleSearchButton.Visibility = Visibility.Visible;
    }

    private void SubtitleSearch_Click(object sender, RoutedEventArgs e)
    {
        TracksMenu.Visibility = Visibility.Collapsed; SubtitleSearchMenu.Visibility = Visibility.Visible;
        var languages = JellyfinSubtitles.SearchLanguages(_settings.SubtitleLanguage);
        if (!languages.Contains(_subtitleLanguage)) _subtitleLanguage = languages[0];
        SubtitleLanguageList.Children.Clear();
        _menuBuilding = true;
        try
        {
            foreach (var code in languages)
            {
                var name = PlayerText.Language(code) ?? code;
                var chip = new RadioButton { Style = (Style)FindResource("PlayerSpeedChoice"), GroupName = "subtitle-language", Content = name, MinWidth = 96, IsChecked = code == _subtitleLanguage };
                AutomationProperties.SetName(chip, L("Sous-titres en " + name.ToLower(Culture), "Subtitles in " + name));
                chip.Checked += (_, _) => { if (!_menuBuilding) _ = SearchSubtitlesAsync(code); };
                SubtitleLanguageList.Children.Add(chip);
            }
        }
        finally { _menuBuilding = false; }
        _ = SearchSubtitlesAsync(_subtitleLanguage);
        Dispatcher.BeginInvoke(() => SubtitleLanguageList.Children.OfType<RadioButton>().FirstOrDefault(x => x.IsChecked == true)?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }
    private void SubtitleSearchBack_Click(object sender, RoutedEventArgs e)
    {
        ++_subtitleSearch;
        SubtitleSearchMenu.Visibility = Visibility.Collapsed; TracksMenu.Visibility = Visibility.Visible;
        CurrentMenuChoice()?.Focus();
    }
    /// <summary>What Jellyfin's extension finds in this language, the version of this very file first.</summary>
    private async Task SearchSubtitlesAsync(string language)
    {
        if (_client is not { } client || _session.Item is not { } item) return;
        _subtitleLanguage = language;
        var search = ++_subtitleSearch;
        SubtitleResultList.Children.Clear();
        SubtitleSearchStatus.Text = L("Recherche…", "Searching…");
        try
        {
            SubtitleSearchPrivacy.Text = JellyfinSubtitles.Privacy(await SubtitleProvidersAsync());
            var found = await client.SearchSubtitlesAsync(item.Id, language, _connectionLifetime.Token);
            if (search != _subtitleSearch) return;
            SubtitleSearchStatus.Text = found.Count > 0 ? "" : L($"Aucun sous-titre trouvé en {(PlayerText.Language(language) ?? language).ToLower(Culture)}.", $"No subtitles found in {PlayerText.Language(language) ?? language}.");
            foreach (var subtitle in found.Take(40)) SubtitleResultList.Children.Add(SubtitleResultRow(subtitle));
        }
        catch (Exception e) when (IsExpected(e)) { if (search == _subtitleSearch) SubtitleSearchStatus.Text = SubtitleError(e); }
        SubtitleSearchStatus.Visibility = SubtitleSearchStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private static string SubtitleError(Exception e) =>
        e is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } ? JellyfinSubtitles.KeptText(SubtitleKept.Refused, null) : Friendly(e);
    private Button SubtitleResultRow(RemoteSubtitle subtitle)
    {
        var (label, details) = JellyfinSubtitles.Describe(subtitle);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = details, FontSize = 11.5, Foreground = Palette.Get("Faint"), Margin = new Thickness(0, 1, 0, 0), TextWrapping = TextWrapping.Wrap });
        var content = new DockPanel();
        var icon = new Icon { Kind = "subtitles", Width = 15, Height = 15, Margin = new Thickness(1, 0, 11, 0), Foreground = Palette.Get("TextBody") };
        DockPanel.SetDock(icon, Dock.Left); content.Children.Add(icon); content.Children.Add(text);
        var row = new Button { Style = (Style)FindResource("PlayerMenuItem"), Content = content, ToolTip = label.Length > 40 ? label : null };
        AutomationProperties.SetName(row, L($"Garder sur Jellyfin : {label}, {details}", $"Keep on Jellyfin: {label}, {details}"));
        row.Click += (_, _) => _ = KeepFoundSubtitleAsync(subtitle);
        return row;
    }
    /// <summary>
    /// The subtitle picked, fetched and kept by Jellyfin; once Jellyfin lists it (about a second later), it plays here,
    /// and every device offers it.
    /// </summary>
    private async Task KeepFoundSubtitleAsync(RemoteSubtitle subtitle)
    {
        if (_keepingSubtitle || _client is not { } client || _session.Item is not { } item) return;
        var sourceId = _session.Source?.Id; var generation = _playGeneration; var ct = _connectionLifetime.Token;
        _keepingSubtitle = true; SubtitleResultList.IsEnabled = SubtitleLanguageList.IsEnabled = false;
        SubtitleSearchStatus.Text = L("Ajout sur Jellyfin…", "Adding to Jellyfin…"); SubtitleSearchStatus.Visibility = Visibility.Visible;
        try
        {
            var before = await client.StreamsAsync(item.Id, sourceId, ct);
            var kept = await client.KeepRemoteSubtitleAsync(item.Id, subtitle, ct);
            var said = JellyfinSubtitles.KeptText(kept, subtitle.Language);
            if (kept is SubtitleKept.Refused or SubtitleKept.NotWritten) { SubtitleSearchStatus.Text = said; return; }
            SubtitleSearchStatus.Text = L("Jellyfin lit le nouveau fichier…", "Jellyfin is reading the new file…");
            var arrived = await client.KeptSubtitleAsync(item.Id, sourceId, before, TimeSpan.FromSeconds(30), ct);
            if (generation != _playGeneration) return;
            PlayerOptionsPopup.IsOpen = false;
            if (arrived is not null && sourceId is not null && PlayKeptSubtitle(client, item.Id, sourceId, arrived)) SetNotice(said);
            else SetNotice(arrived is null ? said + L(" Jellyfin ne le montre pas encore : il sera proposé à la prochaine lecture.", " Jellyfin doesn’t list them yet: they’ll be offered the next time you play this.") : said + L(" Choisis-le à la prochaine lecture.", " Choose them the next time you play this."));
        }
        catch (Exception e) when (IsExpected(e)) { if (generation == _playGeneration) SubtitleSearchStatus.Text = SubtitleError(e); }
        finally { _keepingSubtitle = false; SubtitleResultList.IsEnabled = SubtitleLanguageList.IsEnabled = true; }
    }
    /// <summary>The subtitle file Jellyfin now keeps, added to this playback and chosen (and for the next episodes, <see cref="RememberTracks"/>).</summary>
    private bool PlayKeptSubtitle(JellyfinClient client, string itemId, string sourceId, MediaStream subtitle)
    {
        if (_mpv is null) return false;
        _mpv.Set("http-header-fields", "Authorization: " + client.AuthorizationHeader.Replace(",", "\\,"));
        if (!TryCommand("sub-add", client.SubtitleUri(itemId, sourceId, subtitle).AbsoluteUri, "select", subtitle.DisplayTitle ?? "", subtitle.Language ?? "")) return false;
        RememberTracks();
        return true;
    }

    /// <summary>
    /// After « Charger un fichier… »: the file plays here only, so Mira offers to keep it on Jellyfin, for an account
    /// allowed to, when its format is one Jellyfin keeps. Its language comes from its name (« Film.fr.srt »), else from
    /// the subtitle preferences, and is said once kept.
    /// </summary>
    private async Task OfferToKeepSubtitleAsync(string file)
    {
        if (_demo || _client is not { } client || _session.Item is not { } item) return;
        if (JellyfinSubtitles.Guess(Path.GetFileName(file), _settings.SubtitleLanguage) is not { } guess) return;
        var generation = _playGeneration;
        bool? allowed;
        try { allowed = await client.CanManageSubtitlesAsync(_connectionLifetime.Token); }
        catch (Exception e) when (IsExpected(e)) { return; }
        if (allowed == false || generation != _playGeneration || client != _client) return;
        SetNotice(JellyfinSubtitles.LoadedText, L("Garder sur Jellyfin", "Keep on Jellyfin"),
            () => _ = KeepLoadedSubtitleAsync(client, item.Id, file, guess), sticky: false);
    }
    private async Task KeepLoadedSubtitleAsync(JellyfinClient client, string itemId, string file, (string Format, string Language, bool Forced, bool HearingImpaired) guess)
    {
        SubtitleUpload? upload;
        try { upload = await JellyfinSubtitles.ReadAsync(file, guess); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SetNotice(JellyfinSubtitles.UnreadableText); return; }
        if (upload is null) { SetNotice(JellyfinSubtitles.TooBigText); return; }
        SetNotice(L("Envoi des sous-titres à Jellyfin…", "Sending the subtitles to Jellyfin…"));
        try
        {
            var kept = await client.KeepSubtitleAsync(itemId, upload, _connectionLifetime.Token);
            SetNotice(JellyfinSubtitles.KeptText(kept, guess.Language));
        }
        catch (Exception e) when (IsExpected(e)) { SetNotice(L("Jellyfin n’a pas gardé ces sous-titres. ", "Jellyfin didn’t keep these subtitles. ") + Friendly(e)); }
    }
}
