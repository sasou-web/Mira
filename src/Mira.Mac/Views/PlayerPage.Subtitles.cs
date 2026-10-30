using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// « Chercher des sous-titres… » in the menu of the tracks, as on Windows (MainWindow.PlayerSubtitles.cs): offered once
// Jellyfin says it has an extension to ask; the languages of the settings as chips, what the extension finds (this very
// file's version first), and the one picked kept on Jellyfin, then played here as soon as Jellyfin lists it.
public sealed partial class PlayerPage
{
    private static (JellyfinClient Client, Task<IReadOnlyList<string>> Providers)? _subtitleProviders;
    private bool _canSearchSubtitles, _subtitleView, _keepingSubtitle;
    private string _subtitleLanguage = "";
    private int _subtitleSearch;
    private IReadOnlyList<RemoteSubtitle>? _subtitlesFound;
    private string? _subtitleStatus, _subtitlePrivacy;
    /// <summary>For the self-check: « Chercher des sous-titres… » offered, then the search opened in the menu.</summary>
    public bool CanSearchSubtitles => _canSearchSubtitles;
    public Button TracksButton => _tracks;
    public void SearchSubtitles() => OpenSubtitleSearch();
    public int SubtitlesFound => _subtitlesFound?.Count ?? -1;

    /// <summary>Jellyfin's subtitle extensions, asked once per account (again after a failure).</summary>
    private static Task<IReadOnlyList<string>> SubtitleProvidersAsync(Session session)
    {
        if (_subtitleProviders is { } known && known.Client == session.Client && !known.Providers.IsFaulted) return known.Providers;
        var providers = session.Client.SubtitleProvidersAsync(session.Lifetime);
        _subtitleProviders = (session.Client, providers);
        return providers;
    }
    /// <summary>At each title: whether the menu offers the search.</summary>
    private async Task LoadSubtitleSearchAsync(Session session, int generation)
    {
        _canSearchSubtitles = false; _subtitleView = false;
        try { var providers = await SubtitleProvidersAsync(session); if (generation == _generation) _canSearchSubtitles = providers.Count > 0; }
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }
    /// <summary>« Chercher des sous-titres… », under « Charger un fichier… ».</summary>
    private void AddSubtitleSearch(Panel list)
    {
        if (_canSearchSubtitles) list.Children.Add(Entry(L("Chercher des sous-titres…", "Search for subtitles…"), null, false, OpenSubtitleSearch));
    }
    private void OpenSubtitleSearch()
    {
        var languages = JellyfinSubtitles.SearchLanguages(_shell.Settings.SubtitleLanguage);
        if (!languages.Contains(_subtitleLanguage)) _subtitleLanguage = languages[0];
        _subtitleView = true;
        _ = SearchSubtitlesAsync(_subtitleLanguage);
    }
    private Control SubtitleSearchMenu()
    {
        var menu = new StackPanel { Width = 420 };
        var back = Ui.IconButton("back", L("Retour aux pistes", "Back to the tracks"), () => { ++_subtitleSearch; _subtitleView = false; RefreshMenus(); }, "player", 17);
        back.Width = back.Height = 32;
        var header = Ui.Row(6, back, new TextBlock { Text = L("Chercher des sous-titres", "Search for subtitles"), FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        header.Margin = new Thickness(2, 0, 0, 6);
        menu.Children.Add(header);
        menu.Children.Add(MenuHeading(L("LANGUE", "LANGUAGE")));
        var languages = JellyfinSubtitles.SearchLanguages(_shell.Settings.SubtitleLanguage);
        var chips = Chips(languages.Select(code =>
        {
            var name = PlayerText.Language(code) ?? code;
            return (name, L("Sous-titres en " + name.ToLower(Culture), "Subtitles in " + name), code == _subtitleLanguage, (Action)(() => { if (code != _subtitleLanguage) _ = SearchSubtitlesAsync(code); }));
        }), languages.Count > 4 ? 4 : 0);
        chips.IsEnabled = !_keepingSubtitle;
        menu.Children.Add(chips);
        if (_subtitlePrivacy is { Length: > 0 } privacy) menu.Children.Add(MenuNote(privacy));
        if (_subtitleStatus is { Length: > 0 } status) menu.Children.Add(new TextBlock { Text = status, FontSize = 13, Foreground = Ui.Brush("#DEDEE4"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 4, 10, 8) });
        if (_subtitlesFound is { Count: > 0 } found)
        {
            var list = new StackPanel { Spacing = 2, IsEnabled = !_keepingSubtitle };
            foreach (var subtitle in found.Take(40))
            {
                var (label, details) = JellyfinSubtitles.Describe(subtitle);
                var row = Entry(label, details, false, () => _ = KeepFoundSubtitleAsync(subtitle));
                Avalonia.Automation.AutomationProperties.SetName(row, L($"Garder sur Jellyfin : {label}, {details}", $"Keep on Jellyfin: {label}, {details}"));
                if (label.Length > 40) ToolTip.SetTip(row, label);
                list.Children.Add(row);
            }
            menu.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        }
        return menu;
    }
    /// <summary>What Jellyfin's extension finds in this language; an answer for an earlier search is dropped.</summary>
    private async Task SearchSubtitlesAsync(string language)
    {
        if (_shell.Session is not { } session || _session.Item is not { } item) return;
        _subtitleLanguage = language;
        var search = ++_subtitleSearch;
        _subtitlesFound = null; _subtitleStatus = L("Recherche…", "Searching…");
        RefreshMenus();
        try
        {
            _subtitlePrivacy = JellyfinSubtitles.Privacy(await SubtitleProvidersAsync(session));
            var found = await session.Client.SearchSubtitlesAsync(item.Id, language, session.Lifetime);
            if (search != _subtitleSearch) return;
            _subtitlesFound = found;
            _subtitleStatus = found.Count > 0 ? null : L($"Aucun sous-titre trouvé en {(PlayerText.Language(language) ?? language).ToLower(Culture)}.", $"No subtitles found in {PlayerText.Language(language) ?? language}.");
        }
        catch (Exception ex) when (Errors.Expected(ex)) { if (search == _subtitleSearch) _subtitleStatus = SubtitleError(ex); }
        if (search == _subtitleSearch) RefreshMenus();
    }
    private static string SubtitleError(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } ? JellyfinSubtitles.KeptText(SubtitleKept.Refused, null) : Errors.Friendly(ex);
    /// <summary>The subtitle picked, fetched and kept by Jellyfin; once Jellyfin lists it, it plays here, and every device offers it.</summary>
    private async Task KeepFoundSubtitleAsync(RemoteSubtitle subtitle)
    {
        if (_keepingSubtitle || _shell.Session is not { } session || _session.Item is not { } item) return;
        var client = session.Client; var sourceId = _session.Source?.Id; var generation = _generation; var ct = session.Lifetime;
        _keepingSubtitle = true; _subtitleStatus = L("Ajout sur Jellyfin…", "Adding to Jellyfin…"); RefreshMenus();
        try
        {
            var before = await client.StreamsAsync(item.Id, sourceId, ct);
            var kept = await client.KeepRemoteSubtitleAsync(item.Id, subtitle, ct);
            var said = JellyfinSubtitles.KeptText(kept, subtitle.Language);
            if (kept is SubtitleKept.Refused or SubtitleKept.NotWritten) { _subtitleStatus = said; return; }
            _subtitleStatus = L("Jellyfin lit le nouveau fichier…", "Jellyfin is reading the new file…"); RefreshMenus();
            var arrived = await client.KeptSubtitleAsync(item.Id, sourceId, before, TimeSpan.FromSeconds(30), ct);
            if (generation != _generation) return;
            _subtitleView = false; (_tracks.Flyout as Flyout)?.Hide();
            if (arrived is not null && sourceId is not null && PlayKeptSubtitle(client, item.Id, sourceId, arrived)) _shell.Notice(said);
            else _shell.Notice(arrived is null ? said + L(" Jellyfin ne le montre pas encore : il sera proposé à la prochaine lecture.", " Jellyfin doesn’t list them yet: they’ll be offered the next time you play this.") : said + L(" Choisis-le à la prochaine lecture.", " Choose them the next time you play this."));
        }
        catch (Exception ex) when (Errors.Expected(ex)) { if (generation == _generation) _subtitleStatus = SubtitleError(ex); }
        finally { _keepingSubtitle = false; RefreshMenus(); }
    }
    /// <summary>The subtitle file Jellyfin now keeps, added to this playback and chosen (and for the next episodes).</summary>
    private bool PlayKeptSubtitle(JellyfinClient client, string itemId, string sourceId, MediaStream subtitle)
    {
        if (_mpv is null) return false;
        _mpv.SetAuthorization(client.AuthorizationHeader);
        if (!_mpv.TryCommand("sub-add", client.SubtitleUri(itemId, sourceId, subtitle).AbsoluteUri, "select", subtitle.DisplayTitle ?? "", subtitle.Language ?? "")) return false;
        RememberTracks();
        return true;
    }
}
