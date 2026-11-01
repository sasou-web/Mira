using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Desktop.TorLink;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// « Suivre une série »: an administrator follows a series from its page; on the PC of Jellyfin, Mira checks it when the
/// relay hands it over (every 6 h, or when asked): TorLink searches its name, <see cref="SeriesFollowing"/> chooses the
/// next episodes, the relay holds them 10 min for « Annuler », then they come back as downloads with where to place them
/// (<see cref="TorLinkImporter.Expect"/>), even with automatic placement off. The Downloads page lists the series followed.
/// </summary>
public partial class MainWindow
{
    private readonly List<AgentFollowOutcome> _remoteFollowOutcomes = [];
    private readonly HashSet<string> _remoteFollowing = [];
    /// <summary>The series shown on its page, as the relay follows it; null when it does not.</summary>
    private RelayFollow? _detailFollow;
    /// <summary>The series whose « Suivre… » the page shows, or asks about.</summary>
    private string? _followShownFor;

    // ---- On the PC of Jellyfin: checking a series ------------------------------------------------------------

    private async Task RunFollowCheckAsync(TorLinkInstallation? installation, WorkFollow follow)
    {
        if (!_remoteFollowing.Add(follow.Id)) return;
        try
        {
            _remoteFollowOutcomes.Add(await CheckFollowAsync(installation, follow));
            VisitRelaySoon();
        }
        catch (OperationCanceledException) { }
        finally { _remoteFollowing.Remove(follow.Id); }
    }

    /// <summary>What Jellyfin has of the series, TorLink's results under its names, and the episodes chosen among them.</summary>
    private async Task<AgentFollowOutcome> CheckFollowAsync(TorLinkInstallation? installation, WorkFollow follow)
    {
        AgentFollowOutcome Outcome(string state, FollowNote note) => new(follow.Id, state, note, null, null, null, null, null);
        if (_client is not { } client || _torlink is not { } torlink) return Outcome("failed", new("failed", Detail: L("Mira n’est pas connecté à Jellyfin.", "Mira isn’t signed in to Jellyfin.")));
        if (installation is not { Headless: true })
            return Outcome("failed", new("failed", Detail: TorLinkRemote.Problem(installation is { HasRuntime: true }, installation is { Detected: false }, false, false, installation is { Outdated: false }) ?? L("TorLink n’est pas prêt sur le PC serveur.", "TorLink isn’t ready on the server PC.")));
        var ct = _torlinkLifetime.Token;
        try
        {
            MediaItem series;
            try { series = await client.ItemAsync(follow.SeriesId, ct); }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) { return Outcome("done", new("missing")); }
            if (series.Type != "Series") return Outcome("done", new("missing"));
            var owned = SeriesFollowing.Owned((await client.OwnedEpisodesAsync(follow.SeriesId, ct)).Items);
            var libraries = await client.VirtualFoldersAsync(ct) ?? [];
            var library = series.Path is { } path ? libraries.FirstOrDefault(x => x.Locations.Any(location => Inside(path, location))) : null;
            var category = library is not null && MediaLibraries.IsAnime(library) ? "anime" : "tv";
            var (names, title, prefer) = SeriesFollowing.FromJournal(torlink.Importer.Entries, series.Path);
            // The air dates TheTVDB gives Jellyfin (its plugin's virtual episodes): when to look next, and which number a
            // release should have. Null without them (no plugin, or a Jellyfin that refused): checked every 6 h as before.
            List<FollowAiring>? airing = null;
            try { airing = EpisodeCalendar.Airing((await client.VirtualEpisodesAsync(ct)).Where(x => x.SeriesId == follow.SeriesId), DateTimeOffset.UtcNow); }
            catch (Exception ex) when (IsExpected(ex) && !ct.IsCancellationRequested) { }
            var parts = new List<(string Category, IReadOnlyList<AgentHit> Hits, string? Error)>();
            foreach (var query in SeriesFollowing.Queries(follow.Query, series.Name, title ?? follow.Names?.FirstOrDefault()))
                foreach (var kind in SeriesFollowing.Categories(category))
                {
                    var (hits, error) = await _torlinkHeadless.SearchAsync(installation, query, kind, ct);
                    parts.Add((kind, hits, error));
                }
            var (found, failure) = RemoteDownloads.Combine(parts, x => x.Id);
            if (failure is not null) return Outcome("failed", new("failed", Detail: failure));
            var check = SeriesFollowing.Choose(follow, new FollowedSeries(series.Name, series.OriginalTitle, series.ProductionYear, owned), names, found, follow.Prefer ?? prefer,
                airing is null ? null : FollowSchedule.Out(airing, DateTimeOffset.UtcNow));
            return new(follow.Id, "done", check.Note, series.Path, category, check.Names, check.Latest, check.Picks, airing);
        }
        catch (Exception ex) when (IsExpected(ex) && !ct.IsCancellationRequested) { return Outcome("failed", new("failed", Detail: Friendly(ex))); }
    }

    /// <summary>
    /// Where an episode of a series followed goes: its series' folder, as long as it is inside a library of shows of
    /// Jellyfin (or a folder chosen in Réglages → TorLink) on this PC; null otherwise, and nothing is downloaded.
    /// </summary>
    private async Task<ImportTarget?> FollowTargetAsync(FollowPlace place, CancellationToken ct)
    {
        if (_client is not { } client || place is not { Season: >= 1 and <= 100, Episode: >= 1 and <= 5000 }) return null;
        string folder;
        try { folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(place.Folder)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var roots = new List<(string Root, MediaKind Kind)>();
        try
        {
            foreach (var library in await client.VirtualFoldersAsync(ct) ?? [])
                if (string.Equals(library.CollectionType, "tvshows", StringComparison.OrdinalIgnoreCase))
                    roots.AddRange(library.Locations.Select(x => (x, MediaLibraries.IsAnime(library) ? MediaKind.Anime : MediaKind.Series)));
        }
        catch (Exception ex) when (IsExpected(ex) && !ct.IsCancellationRequested) { }
        if (_torlink?.Libraries is { } chosen)
        {
            if (chosen.Anime is { } anime) roots.Add((anime, MediaKind.Anime));
            if (chosen.Series is { } shows) roots.Add((shows, MediaKind.Series));
        }
        foreach (var (root, kind) in roots)
        {
            if (!Inside(folder, root) || !Directory.Exists(folder)) continue;
            return new ImportTarget(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), Path.GetRelativePath(root, folder), kind, place.Season, place.Episode);
        }
        return null;
    }

    /// <summary><paramref name="path"/> is inside <paramref name="root"/>, never the root itself.</summary>
    private static bool Inside(string path, string root)
    {
        try
        {
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    // ---- The page of a series ------------------------------------------------------------------------------

    /// <summary>« Nouveaux épisodes », for an administrator, on a series' page, when the extension knows how to follow it.</summary>
    private async Task ShowFollowButtonAsync(MediaItem item)
    {
        // Drawn again for the same series (after « Vu »), the button stays while Mira asks again: the row does not jump.
        if (_followShownFor != item.Id) { FollowButton.Visibility = Visibility.Collapsed; _detailFollow = null; _followShownFor = item.Id; }
        void Hide() { if (_detail?.Id == item.Id) { FollowButton.Visibility = Visibility.Collapsed; _detailFollow = null; } }
        if (_demo || item.Type != "Series" || _client is not { } client) { Hide(); return; }
        var ct = _connectionLifetime.Token;
        List<RelayFollow> follows;
        try
        {
            if (await client.IsAdministratorAsync(ct) != true) { Hide(); return; }
            follows = await client.TorLinkFollowsAsync(ct);
        }
        catch (Exception ex) when (IsExpected(ex)) { Hide(); return; }
        if (_detail?.Id != item.Id || !ReferenceEquals(client, _client)) return;
        _detailFollow = follows.FirstOrDefault(x => string.Equals(x.SeriesId, item.Id, StringComparison.OrdinalIgnoreCase));
        DescribeFollowButton();
        FollowButton.Visibility = Visibility.Visible;
    }

    /// <summary>« Suivre… » off, « Suivie » on (outlined, a live dot): the state of the series seen at a glance.</summary>
    private void DescribeFollowButton()
    {
        var follow = _detailFollow;
        var label = follow is null ? L("Suivre…", "Follow…") : L("Suivie", "Following");
        // The same state drawn again keeps its content: the button does not flicker.
        if (FollowButton.Content is not StackPanel { Children: [Icon _, TextBlock text, ..] } shown || text.Text != label || shown.Children.Count != (follow is null ? 2 : 3))
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new Icon { Kind = "calendar", Width = 18, Height = 18 });
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(9, 0, 0, 0) });
            if (follow is not null) panel.Children.Add(LiveDot());
            FollowButton.Content = panel;
        }
        var tip = follow is null ? L("Suivre les nouveaux épisodes : le PC serveur les cherche et les télécharge tout seul, avec TorLink", "Follow new episodes: the server PC looks for them and downloads them on its own, with TorLink")
            : string.Join(" · ", new[] { L("Suivie", "Following"), RemoteLines.FollowNote(follow.Note), RemoteLines.Follow(follow, DateTimeOffset.UtcNow) }.Where(x => x.Length > 0));
        ShowState(FollowButton, follow is not null, tip);
        if (follow is null) AutomationProperties.SetName(FollowButton, tip);
    }

    /// <summary>
    /// Following downloads on its own from then on: asked first on Mira's card, saying what happens; once followed, its
    /// menu (search now, Downloads, stop, asked again).
    /// </summary>
    private async void Follow_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is not { } item) return;
        if (_detailFollow is not { } follow)
        {
            if (await ConfirmAsync(L($"Suivre « {item.Name} » ?", $"Follow “{item.Name}”?"),
                L("Le PC serveur cherchera ses nouveaux épisodes toutes les 6 h et les téléchargera avec TorLink. Chacun attend 10 min dans Téléchargements, le temps de l’annuler.",
                    "The server PC will look for its new episodes every 6 hours and download them with TorLink. Each one waits 10 min in Downloads, time to cancel it."),
                L("Suivre la série", "Follow the series"), L("Annuler", "Cancel")) && _detail?.Id == item.Id)
                await FollowSeriesAsync(item);
            return;
        }
        var menu = new ContextMenu { PlacementTarget = FollowButton, Placement = PlacementMode.Bottom };
        menu.Items.Add(MenuAction(L("Chercher maintenant", "Search now"), "refresh", () => CheckFollowNowAsync(follow)));
        menu.Items.Add(MenuAction(L("Voir dans Téléchargements", "See in Downloads"), "download", ShowTorLinkAsync));
        menu.Items.Add(MenuAction(L("Ne plus suivre…", "Stop following…"), "close", () => UnfollowAskedAsync(item, follow)));
        menu.IsOpen = true;
    }
    /// <summary>« Ne plus suivre » asked once more: the episodes already there stay.</summary>
    private async Task UnfollowAskedAsync(MediaItem item, RelayFollow follow)
    {
        if (!await ConfirmAsync(L($"Ne plus suivre « {item.Name} » ?", $"Stop following “{item.Name}”?"),
            L("Le PC serveur ne cherchera plus ses nouveaux épisodes. Ceux déjà dans ta bibliothèque restent.", "The server PC will stop looking for its new episodes. Those already in your library stay."),
            L("Ne plus suivre", "Stop following"), L("Continuer à suivre", "Keep following"))) return;
        await UnfollowAsync(follow);
        if (_detail?.Id == item.Id) await ShowFollowButtonAsync(item);
    }

    private async Task FollowSeriesAsync(MediaItem item)
    {
        if (_client is not { } client) return;
        try
        {
            await client.TorLinkFollowAsync(item.Id, item.Name, null, _connectionLifetime.Token);
            SetNotice(L($"« {item.Name} » suivie : le PC serveur cherche ses nouveaux épisodes toutes les 6 h. Chacun attend 10 min dans Téléchargements, le temps de l’annuler, puis rejoint ta bibliothèque.",
                $"Following “{item.Name}”: the server PC looks for its new episodes every 6 hours. Each one waits 10 min in Downloads, time to cancel it, then joins your library."));
        }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(ServerError(ex)); return; }
        if (_detail?.Id == item.Id) await ShowFollowButtonAsync(item);
    }

    private async Task CheckFollowNowAsync(RelayFollow follow)
    {
        if (_client is not { } client) return;
        try { await client.TorLinkCheckFollowAsync(follow.Id, _connectionLifetime.Token); SetNotice(L($"Le PC serveur cherche les nouveaux épisodes de « {follow.Name} ».", $"The server PC is looking for new episodes of “{follow.Name}”.")); }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(ServerError(ex)); }
        if (_serverScreen) await LoadServerDownloadsAsync();
    }

    private async Task UnfollowAsync(RelayFollow follow)
    {
        if (_client is not { } client) return;
        try { await client.TorLinkUnfollowAsync(follow.Id, _connectionLifetime.Token); SetNotice(L($"« {follow.Name} » n’est plus suivie.", $"No longer following “{follow.Name}”.")); }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(ServerError(ex)); }
        if (_serverScreen) await LoadServerDownloadsAsync();
    }

    private async Task RenameFollowAsync(RelayFollow follow, string? query)
    {
        if (_client is not { } client) return;
        try { await client.TorLinkFollowAsync(follow.SeriesId, follow.Name, query, _connectionLifetime.Token); SetNotice(L("Nom cherché changé : le PC serveur cherche de nouveau.", "Search name changed: the server PC is searching again.")); }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(ServerError(ex)); }
        await LoadServerDownloadsAsync();
    }

    // ---- The Downloads page: « Séries suivies » ------------------------------------------------------------

    /// <summary>Each series followed, what its last check found and its latest episode taken, then the releases chosen, with « Annuler ».</summary>
    private List<ServerRowData> ServerFollowRows(IReadOnlyList<RelayFollow>? follows, DateTimeOffset now)
    {
        var rows = new List<ServerRowData>();
        foreach (var follow in follows ?? [])
        {
            var line = string.Join("\n", new[] { RemoteLines.Follow(follow, now), RemoteLines.FollowNote(follow.Note), follow.Taken.FirstOrDefault() is { } taken ? RemoteLines.Taken(taken, now) : "" }.Where(x => x.Length > 0));
            rows.Add(new($"f:{follow.Id}", $"f|{follow.Query}", follow.Name, line, null, () => ServerFollowRow(follow, line)));
            foreach (var pick in follow.Picks)
                rows.Add(new($"fp:{pick.Id}", $"fp|{pick.State}", Shown(pick.Name).Title, RemoteLines.Pick(pick, now), null, () => ServerPickRow(pick, now)));
        }
        return rows;
    }

    private ServerRow ServerFollowRow(RelayFollow follow, string line)
    {
        var actions = new StackPanel();
        var buttons = new WrapPanel { Margin = new Thickness(-6, 6, 0, 0) };
        buttons.Children.Add(Named(RowButton(L("Chercher maintenant", "Search now"), "refresh", () => CheckFollowNowAsync(follow)), L($"Chercher maintenant les nouveaux épisodes de « {follow.Name} »", $"Search now for new episodes of “{follow.Name}”")));
        buttons.Children.Add(Named(RowButton(L("Nom cherché", "Search name"), "search", () => { EditFollowQuery(actions, follow); return Task.CompletedTask; }), L($"Changer le nom cherché pour « {follow.Name} »", $"Change the search name for “{follow.Name}”")));
        buttons.Children.Add(Named(RowButton(L("Ne plus suivre", "Stop following"), "close", () => UnfollowAsync(follow)), L($"Ne plus suivre « {follow.Name} »", $"Stop following “{follow.Name}”")));
        actions.Children.Add(buttons);
        return ServerRowView("series", "TextBody", follow.Name, line, null, follow.Note?.Kind is "failed" or "missing" or "unknown", actions, follow.Query is { } query ? L($"Cherchée sous « {query} »", $"Searched as “{query}”") : null);
    }

    /// <summary>The name TorLink searches, changed in the row: empty for the series' own.</summary>
    private void EditFollowQuery(StackPanel actions, RelayFollow follow)
    {
        var kept = actions.Children.Cast<UIElement>().ToList();
        actions.Children.Clear();
        var box = new TextBox { Text = follow.Query ?? follow.Name, Margin = new Thickness(0, 8, 0, 0), MaxLength = RemoteDownloads.MaxQuery };
        AutomationProperties.SetName(box, L("Nom cherché", "Search name"));
        actions.Children.Add(new TextBlock { Text = L("Le nom sous lequel ses épisodes sortent, par exemple son nom japonais. Vide : son nom dans Jellyfin.", "The name its episodes come out under, its Japanese name for example. Empty: its name in Jellyfin."), FontSize = 11.5, LineHeight = 17, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        actions.Children.Add(box);
        var buttons = new WrapPanel { Margin = new Thickness(-6, 4, 0, 0) };
        Task Save()
        {
            var query = RemoteDownloads.CleanQuery(box.Text);
            return RenameFollowAsync(follow, query is null || ReleaseRanking.TitleKey(query) == ReleaseRanking.TitleKey(follow.Name) ? null : query);
        }
        box.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Save(); } };
        buttons.Children.Add(Named(RowButton(L("Enregistrer", "Save"), "check", Save), L("Enregistrer le nom cherché", "Save the search name")));
        buttons.Children.Add(Named(RowButton(L("Annuler", "Cancel"), "close", () =>
        {
            actions.Children.Clear();
            foreach (var element in kept) actions.Children.Add(element);
            return Task.CompletedTask;
        }), L("Garder le nom cherché", "Keep the search name")));
        actions.Children.Add(buttons);
        _ = Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
    }

    private ServerRow ServerPickRow(RelayPick pick, DateTimeOffset now)
    {
        var (name, full) = Shown(pick.Name);
        var actions = new StackPanel();
        if (pick.State is "waiting" or "failed")
        {
            var label = pick.State == "waiting" ? L("Annuler", "Cancel") : L("Retirer", "Remove");
            var buttons = new WrapPanel { Margin = new Thickness(-6, 6, 0, 0) };
            buttons.Children.Add(Named(RowButton(label, "close", () => CancelPickAsync(pick)), L($"{label} « {name} »", $"{label} “{name}”")));
            actions.Children.Add(buttons);
        }
        var failed = pick.State == "failed";
        return ServerRowView(failed ? "warning" : "clock", failed ? "Warning" : "Faint", name, RemoteLines.Pick(pick, now), null, failed, actions, full);
    }

    private async Task CancelPickAsync(RelayPick pick)
    {
        if (_client is not { } client) return;
        try
        {
            SetNotice(await client.TorLinkCancelAsync(pick.Id, _connectionLifetime.Token) switch
            {
                true => pick.State == "waiting" ? L($"{pick.Episode} annulé : ce fichier ne sera plus pris, un autre de l’épisode pourra l’être.", $"{pick.Episode} canceled: this file won’t be taken again, another one of the episode may be.") : L("Retiré de la liste.", "Removed from the list."),
                false => L("Le PC serveur l’a déjà pris : annule-le dans sa liste dès qu’il y apparaît.", "The server PC has already taken it: cancel it in its list as soon as it shows up there."),
                null => L("Cet épisode n’est plus en attente.", "This episode is no longer waiting.")
            });
        }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(ServerError(ex)); }
        await LoadServerDownloadsAsync();
    }
}
