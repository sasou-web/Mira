using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Mira.Core;
using Mira.Desktop.Services;

namespace Mira.Desktop;

/// <summary>
/// The « … » of a title's page (with « Identifier… » of an administrator, MainWindow.Identify.cs), and there the files of
/// a film or series, on the PC of Jellyfin only (elsewhere they are not on this computer): « Afficher dans
/// l'explorateur », and « Mettre à la Corbeille » for an administrator, asked once more, never for good (the Recycle Bin
/// keeps it), then said to Jellyfin, which takes it out of the library (<see cref="TitleFiles"/>).
/// </summary>
public partial class MainWindow
{
    /// <summary>What the « … » of the page holds: the title's files, on the PC of Jellyfin; « Identifier… », for an administrator.</summary>
    private (string Id, bool Files, bool Admin) _titleMore;
    /// <summary>
    /// The button beside the others of the page, once its title's place is known: for its files when Jellyfin is on
    /// this PC, for « Identifier… » and « Actualiser les métadonnées » when this account administers Jellyfin.
    /// </summary>
    private async Task ShowFilesButtonAsync(MediaItem item)
    {
        // Drawn again for the same title (after « Vu »), « … » stays while Mira asks again: the row does not jump.
        if (_titleMore.Id != item.Id) { FilesButton.Visibility = DiscordHideable(item) ? Visibility.Visible : Visibility.Collapsed; _titleMore = (item.Id, false, false); }
        if (_demo || _client is not { } client) { FilesButton.Visibility = Visibility.Collapsed; return; }
        bool files = false, admin = false;
        try { files = TitleFiles.Location(item) is not null && await JellyfinOnThisPcAsync(client); }
        catch (Exception ex) when (IsExpected(ex)) { }
        try { admin = item.Type is "Movie" or "Series" && await IsAdministratorAsync(client); }
        catch (Exception ex) when (IsExpected(ex)) { }
        if (_detail?.Id != item.Id) return;
        _titleMore = (item.Id, files, admin);
        FilesButton.Visibility = files || admin || DiscordHideable(item) ? Visibility.Visible : Visibility.Collapsed;
        ShowSeasonMore((SeasonSelector.SelectedItem as ComboBoxItem)?.Tag as EpisodePage);
    }

    private void Files_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is not { } item || _titleMore.Id != item.Id && !DiscordHideable(item)) return;
        var menu = new ContextMenu { PlacementTarget = FilesButton, Placement = PlacementMode.Bottom };
        if (_titleMore.Admin) { AddIdentifyActions(menu, item); if (UpgradeMenuItem(item) is { } upgrade) menu.Items.Add(upgrade); }
        if (_titleMore.Files && TitleFiles.Location(item) is { } location)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction(L("Afficher dans l’explorateur", "Show in File Explorer"), "folder", () => { ShowInExplorer(item, location); return Task.CompletedTask; }));
            menu.Items.Add(MenuAction(L("Mettre à la Corbeille…", "Move to the Recycle Bin…"), "trash", () => AskToRecycleAsync(item)));
        }
        if (DiscordHideable(item))
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(DiscordHideMenuItem(item));
        }
        if (menu.Items.Count > 0) menu.IsOpen = true;
    }

    /// <summary>A series' folder opened; a film's file (or disc folder) selected in its folder.</summary>
    private void ShowInExplorer(MediaItem item, string location)
    {
        // By its full path: never an explorer.exe put beside Mira, whose folder anyone may write to.
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var series = item.Type == "Series";
        if (!File.Exists(location) && !Directory.Exists(location)) { SetNotice(L($"« {item.DisplayTitle} » n’est plus à sa place sur ce PC : {location}", $"“{item.DisplayTitle}” is no longer where it was on this PC: {location}")); return; }
        try { using var _ = Process.Start(new ProcessStartInfo(explorer, series ? $"\"{location}\"" : $"/select,\"{location}\"")); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { SetNotice(L($"L’explorateur ne s’est pas ouvert. Le titre est ici : {location}", $"File Explorer didn’t open. The title is here: {location}")); }
    }

    /// <summary>
    /// « … » at the end of the Episodes line, on the PC of Jellyfin for an administrator (as the page's « … »): the season
    /// shown in File Explorer, or to the Recycle Bin.
    /// </summary>
    private void ShowSeasonMore(EpisodePage? page) =>
        SeasonMoreButton.Visibility = page is { Absent: false } && _detail is { Type: "Series" } series && _titleMore is { Files: true, Admin: true } more && more.Id == series.Id
            && _episodes.Any(x => EpisodePages.Season(x) == page.Season) ? Visibility.Visible : Visibility.Collapsed;

    private void SeasonMore_Click(object sender, RoutedEventArgs e)
    {
        if ((SeasonSelector.SelectedItem as ComboBoxItem)?.Tag is not EpisodePage { Absent: false } page || _detail is not { Type: "Series" } series) return;
        var menu = new ContextMenu { PlacementTarget = SeasonMoreButton, Placement = PlacementMode.Bottom };
        menu.Items.Add(MenuAction(L("Afficher dans l’explorateur", "Show in File Explorer"), "folder", () => ShowSeasonInExplorerAsync(series, page.Season)));
        menu.Items.Add(MenuAction(L($"Mettre {SeasonName(page.Season, true)} à la Corbeille…", $"Move {SeasonName(page.Season, false)} to the Recycle Bin…"), "trash", () => AskToRecycleSeasonAsync(series, page.Season)));
        menu.IsOpen = true;
    }

    /// <summary>« la saison 10 », « les épisodes spéciaux » (French when <paramref name="french"/>), as a sentence says it.</summary>
    private static string SeasonName(int season, bool french) => french
        ? season == 0 ? "les épisodes spéciaux" : $"la saison {season}"
        : season == 0 ? "the specials" : $"season {season}";

    /// <summary>An episode's entries of its right-click menu, shown only when the page's « … » holds the files (Jellyfin on this PC).</summary>
    private void AddEpisodeFileActions(ContextMenu menu, MediaItem episode)
    {
        var line = new Separator();
        var show = MenuAction(L("Afficher dans l’explorateur", "Show in File Explorer"), "folder", () => ShowEpisodeInExplorerAsync(episode));
        var recycle = MenuAction(L("Mettre à la Corbeille…", "Move to the Recycle Bin…"), "trash", () => AskToRecycleAsync(episode));
        menu.Items.Add(line); menu.Items.Add(show); menu.Items.Add(recycle);
        menu.Opened += (_, _) =>
        {
            var files = _detail is { } series && _titleMore is { Files: true } more && more.Id == series.Id && episode.SeriesId == series.Id;
            line.Visibility = show.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
            recycle.Visibility = files && _titleMore.Admin ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    private async Task ShowEpisodeInExplorerAsync(MediaItem episode)
    {
        if (_client is not { } client) return;
        try { if (TitleFiles.Location(await client.ItemAsync(episode.Id, _connectionLifetime.Token)) is { } location) ShowInExplorer(episode, location); }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); }
    }

    /// <summary>The season's folder when it has one, else the series' (its episodes right in it).</summary>
    private async Task ShowSeasonInExplorerAsync(MediaItem series, int season)
    {
        if (_client is not { } client) return;
        try
        {
            var ct = _connectionLifetime.Token;
            var seasonId = _episodes.FirstOrDefault(x => EpisodePages.Season(x) == season)?.SeasonId;
            var folder = seasonId is null ? null : (await client.ItemAsync(seasonId, ct)).Path;
            if (folder is not { Length: > 0 } || !Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) folder = (await client.ItemAsync(series.Id, ct)).Path;
            if (folder is { Length: > 0 } && Path.IsPathFullyQualified(folder)) ShowInExplorer(series, folder);
        }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); }
    }

    /// <summary>The library folders of an administrator (not those of collections); null, said, for anyone else.</summary>
    private async Task<List<string>?> RecycleLibrariesAsync(JellyfinClient client, CancellationToken ct)
    {
        if (await client.IsAdministratorAsync(ct) != true || await client.VirtualFoldersAsync(ct) is not { } libraries)
        { SetNotice(L("Seul un compte administrateur de Jellyfin peut mettre un titre à la Corbeille depuis Mira.", "Only a Jellyfin administrator account can move a title to the Recycle Bin from Mira.")); return null; }
        return [.. libraries.Where(x => !LibrarySection.Groups(x.CollectionType)).SelectMany(x => x.Locations)];
    }

    /// <summary>A film, a series or an episode: what goes, asked on a card (<see cref="ConfirmRecycleAsync"/>); nothing while someone watches it.</summary>
    private async Task AskToRecycleAsync(MediaItem item)
    {
        if (_client is not { } client) return;
        var ct = _connectionLifetime.Token;
        try
        {
            if (await RecycleLibrariesAsync(client, ct) is not { } folders) return;
            var episode = item.Type == "Episode";
            // The list of episodes gives no paths: the title alone does.
            var title = item.Type is "Movie" or "Episode" ? await client.ItemAsync(item.Id, ct) : item;
            // A title in several files goes whole: its other parts (« Film - cd2.mkv »…) and versions (« Film - 720p.mkv »…).
            var others = new List<string>();
            if (item.Type is "Movie" or "Episode")
            {
                others.AddRange((title.MediaSources ?? []).Select(x => x.Path).OfType<string>());
                if (MovieParts.Has(title)) others.AddRange((await client.AdditionalPartsAsync(item.Id, ct)).Select(x => x.Path).OfType<string>());
            }
            var seriesFolder = episode && title.SeriesId is { } seriesId ? (await client.ItemAsync(seriesId, ct)).Path : null;
            var name = episode ? EpisodeName(title) : item.DisplayTitle;
            var targets = await Task.Run(() => TitleFiles.ToRemove(title, folders, others, seriesFolder), ct);
            if (targets is null) { SetNotice(L($"Mira ne met à la Corbeille que ce qui est dans un dossier de tes bibliothèques, et « {name} » n’y est pas (ou plus).", $"Mira only moves to the Recycle Bin what’s in one of your library folders, and “{name}” isn’t (or no longer is) there.")); return; }
            var (seasonOf, first, last) = (EpisodePages.Season(title), title.IndexNumber, title.IndexNumberEnd ?? title.IndexNumber);
            var watching = (await client.ViewersAsync(ct)).FirstOrDefault(x => item.Type switch
            {
                "Series" => x.Series == item.Name,
                "Episode" => x.Type == "Episode" && x.Series == title.SeriesName && x.Season == seasonOf && x.Episode is { } shown && shown >= first && shown <= last,
                _ => x.Type == "Movie" && x.Name == item.Name
            });
            if (watching is not null) { SetNotice(L($"« {name} » est en lecture sur {watching.Device} ({watching.User}) : réessaie une fois la lecture finie.", $"“{name}” is playing on {watching.Device} ({watching.User}): try again once it’s over.")); return; }
            var kind = item.Type switch { "Series" => RecycledKind.Series, "Episode" => RecycledKind.Episode, _ => RecycledKind.Movie };
            // Two episodes in one file (« S01E01-E02 »): both go.
            var extra = episode && last > first ? L($"Ce fichier contient aussi l’épisode {last} : il part avec. ", $"This file also holds episode {last}: it goes too. ") : "";
            if (!await ConfirmTargetsAsync(name, episode ? title.SeriesName : null, kind, targets, extra)) return;
            await RecycleAsync(client, name, targets, () => Forget(item, episode ? new HashSet<string> { item.Id } : null));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); }
    }

    /// <summary>A season: its folder, or its episodes' files when they share theirs; nothing while one of them is watched.</summary>
    private async Task AskToRecycleSeasonAsync(MediaItem series, int season)
    {
        if (_client is not { } client) return;
        var ct = _connectionLifetime.Token;
        try
        {
            if (await RecycleLibrariesAsync(client, ct) is not { } folders) return;
            var episodes = (await client.EpisodeFilesAsync(series.Id, ct)).Where(x => EpisodePages.Season(x) == season).ToList();
            var files = episodes.SelectMany(x => (x.MediaSources ?? []).Select(source => source.Path)).OfType<string>().ToList();
            foreach (var parted in episodes.Where(MovieParts.Has)) files.AddRange((await client.AdditionalPartsAsync(parted.Id, ct)).Select(x => x.Path).OfType<string>());
            var seriesFolder = (await client.ItemAsync(series.Id, ct)).Path;
            var seasonFolder = episodes.Select(x => x.SeasonId).OfType<string>().FirstOrDefault() is { } seasonId ? (await client.ItemAsync(seasonId, ct)).Path : null;
            var name = $"{series.DisplayTitle} · {(season == 0 ? L("Épisodes spéciaux", "Specials") : L($"Saison {season}", $"Season {season}"))}";
            var targets = seriesFolder is null ? null : await Task.Run(() => TitleFiles.SeasonToRemove(season, files, folders, seriesFolder, seasonFolder), ct);
            if (targets is null) { SetNotice(L($"Mira ne met à la Corbeille que ce qui est dans un dossier de tes bibliothèques, et « {name} » n’y est pas (ou plus).", $"Mira only moves to the Recycle Bin what’s in one of your library folders, and “{name}” isn’t (or no longer is) there.")); return; }
            var watching = (await client.ViewersAsync(ct)).FirstOrDefault(x => x.Series == series.Name && x.Season == season);
            if (watching is not null) { SetNotice(L($"« {name} » est en lecture sur {watching.Device} ({watching.User}) : réessaie une fois la lecture finie.", $"“{name}” is playing on {watching.Device} ({watching.User}): try again once it’s over.")); return; }
            if (!await ConfirmTargetsAsync(name, null, RecycledKind.Season, targets, "")) return;
            var ids = episodes.Select(x => x.Id).ToHashSet();
            await RecycleAsync(client, name, targets, () => Forget(series, ids));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); }
    }

    private enum RecycledKind { Movie, Series, Season, Episode }

    /// <summary>« S10 E03 · Tsubasa Tiger, Part 3 ».</summary>
    private static string EpisodeName(MediaItem episode) =>
        $"S{EpisodePages.Season(episode):00} E{episode.IndexNumber ?? 0:00}{(episode.IndexNumberEnd is { } end && end > episode.IndexNumber ? $"-E{end:00}" : "")}" + (episode.Name.Length > 0 ? " · " + episode.Name : "");

    /// <summary>
    /// The card: what goes, counted from the disk itself (videos, subtitles and their languages, .nfo and images), its
    /// folder when a whole folder goes, where. Refused before it when a drive has no Recycle Bin: Mira never deletes for good.
    /// </summary>
    private async Task<bool> ConfirmTargetsAsync(string name, string? of, RecycledKind kind, IReadOnlyList<string> targets, string extra)
    {
        var (contents, kept) = await Task.Run(() => (TitleFiles.Describe(targets), targets.All(RecycleBin.Available)));
        if (!kept)
        {
            SetNotice(L("Ce disque n’a pas de Corbeille, et Mira ne supprime jamais rien pour de bon : rien n’a été déplacé. Passe par « Afficher dans l’explorateur » si tu veux vraiment l’effacer.",
                "This drive has no Recycle Bin, and Mira never deletes for good: nothing was moved. Use “Show in File Explorer” if you really want it gone."), null, null, sticky: true);
            return false;
        }
        var rows = new List<(string, string, string)>();
        var folder = targets[0];
        if (Directory.Exists(folder))
            rows.Add(("folder", kind switch
            {
                RecycledKind.Series => L("Le dossier de la série", "The show’s folder"),
                RecycledKind.Movie => L("Le dossier du film", "The movie’s folder"),
                _ => L("Le dossier de la saison", "The season’s folder")
            }, Path.GetFileName(folder)));
        if (contents.Videos > 0)
            rows.Add(("film", kind switch
            {
                RecycledKind.Episode => contents.Videos > 1 ? L("L’épisode, en plusieurs fichiers", "The episode, in several files") : L("L’épisode", "The episode"),
                RecycledKind.Movie => contents.Videos > 1 ? L("Le film et ses autres fichiers", "The movie and its other files") : L("Le film", "The movie"),
                _ => L("Ses épisodes", "Its episodes")
            }, $"{Count(contents.Videos, "vidéo", "vidéos", "video", "videos")} · {OfflineCopies.SizeText(contents.VideoSize)}"));
        if (contents.Subtitles > 0)
            rows.Add(("subtitles", L("Ses sous-titres", "Its subtitles"),
                string.Join(" · ", new[] { Count(contents.Subtitles, "fichier", "fichiers", "file", "files"), string.Join(", ", contents.Languages.Take(3)) + (contents.Languages.Count > 3 ? "…" : "") }.Where(x => x.Length > 0))));
        if (contents.Others > 0)
            rows.Add(("info", L("Ses fiches .nfo et ses images", "Its info files and images"),
                contents.OtherKinds.Count <= 3 ? string.Join(", ", contents.OtherKinds) : Count(contents.Others, "fichier", "fichiers", "file", "files")));
        var files = Count(contents.Videos + contents.Subtitles + contents.Others, "fichier", "fichiers", "file", "files");
        var sub = string.Join(" · ", new[] { of, files, OfflineCopies.SizeText(contents.Size) }.Where(x => !string.IsNullOrEmpty(x)));
        var where = targets.Count == 1 ? folder : folder + L($"  (+ {targets.Count - 1} à côté)", $"  (+ {targets.Count - 1} beside it)");
        return await ConfirmRecycleAsync(L($"Mettre « {name} » à la Corbeille ?", $"Move “{name}” to the Recycle Bin?"), sub, rows, where,
            extra + L("Tu pourras le remettre depuis la Corbeille de Windows. Jellyfin le retire de la bibliothèque dans une minute environ.", "You can put it back from the Windows Recycle Bin. Jellyfin takes it out of the library in about a minute."));
    }

    private static string Count(int count, string one, string many, string oneEn, string manyEn) =>
        L($"{count} {(count == 1 ? one : many)}", $"{count} {(count == 1 ? oneEn : manyEn)}");

    private async Task RecycleAsync(JellyfinClient client, string name, IReadOnlyList<string> targets, Func<Task> forget)
    {
        if (!ReferenceEquals(client, _client)) return;
        var outcome = RecycleBin.Send(targets, new WindowInteropHelper(this).Handle);
        var gone = targets.Where(x => !File.Exists(x) && !Directory.Exists(x)).ToList();
        // What did go is said to Jellyfin all the same: its own folder monitor would see it too.
        if (gone.Count > 0) await client.ReportMediaChangedAsync([], gone);
        if (outcome != RecycleBin.Outcome.Recycled)
        {
            var part = gone.Count > 0 ? L(" Une partie est déjà dans la Corbeille.", " Part of it is already in the Recycle Bin.") : "";
            SetNotice(outcome == RecycleBin.Outcome.NotKept
                ? L($"Windows n’aurait pas pu garder « {name} » dans la Corbeille (trop gros pour elle, ou une Corbeille qui efface tout de suite) : Mira s’est arrêté là, rien n’a été supprimé pour de bon.{part}", $"Windows couldn’t have kept “{name}” in the Recycle Bin (too large for it, or a Recycle Bin set to delete at once): Mira stopped there, nothing was deleted for good.{part}")
                : gone.Count == targets.Count ? L($"« {name} » n’était plus à sa place.", $"“{name}” was no longer there.")
                : L($"« {name} » n’a pas été mis à la Corbeille (refusé, annulé ou un fichier encore ouvert).{part}", $"“{name}” wasn’t moved to the Recycle Bin (refused, cancelled or a file still open).{part}"), null, null, sticky: true);
            return;
        }
        await forget();
        SetNotice(L($"« {name} » est dans la Corbeille. Jellyfin le retire de la bibliothèque dans une minute environ.", $"“{name}” is in the Recycle Bin. Jellyfin takes it out of the library in about a minute."));
    }

    /// <summary>
    /// Gone from Mira at once: a title leaves the library and its page; episodes (<paramref name="episodes"/>) leave
    /// their series' page, and a season left with none leaves its list.
    /// </summary>
    private async Task Forget(MediaItem item, IReadOnlySet<string>? episodes)
    {
        if (episodes is null)
        {
            _catalog.Forget(item.Id); RenderLibrary();
            _detailTrail.Forget(item.Id);
            if (_detail?.Id == item.Id && DetailOverlay.Visibility == Visibility.Visible) await BackFromDetailsAsync();
            return;
        }
        foreach (var id in episodes) _catalog.Forget(id);
        if (_detail?.Id != (item.Type == "Series" ? item.Id : item.SeriesId)) return;
        var seasons = _episodes.Select(x => x.ParentIndexNumber).Distinct().Count();
        _episodes.RemoveAll(x => episodes.Contains(x.Id));
        _metadata?.Clear();
        // Lecture played the episode just sent to the Recycle Bin: it goes on to the next one still there.
        var nextGone = _detailNext is { } next && episodes.Contains(next.Id);
        if (nextGone) { _detailNext = null; _detailFile = null; UpdateDetailPlayLabel(); }
        FillSeasons(); RenderEpisodes();
        // The facts at the top follow at once (« 2 seasons · 2 MB »), not when the page opens again.
        var gone = seasons - _episodes.Select(x => x.ParentIndexNumber).Distinct().Count();
        if (gone > 0 && _detail is { Type: "Series", ChildCount: > 0 } series) _detail = series with { ChildCount = Math.Max(0, series.ChildCount.Value - gone) };
        if (_episodeSizes is not null) _detailSize = TitleStorage.Sum(_episodes, _episodeSizes);
        RenderDetailMeta();
        if (nextGone) { ShowOfflineButton(); if (_detailNext is { } target && _metadata is not null) await LoadDetailFileAsync(target, _detailVersion); }
        // Gone from « Continuer à regarder » too, not at the next refresh.
        RenderLibrary();
    }
}
