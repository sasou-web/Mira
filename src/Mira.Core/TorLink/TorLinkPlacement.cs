namespace Mira.Core;

/// <summary>
/// TorLink's finished downloads placed in the Jellyfin libraries, reported to Jellyfin, then followed until the title
/// appears in the library: the automatic part of TorLink in Mira for Windows (MainWindow.TorLink keeps its page). The
/// file work runs on the thread pool; the rest, awaited from the interface's thread, stays on it.
/// </summary>
public sealed class TorLinkPlacement : IDisposable
{
    private readonly HashSet<string> _requested = [], _announced = [];
    private FileSystemWatcher? _watcher;
    private (DateTime Stamp, long Length) _historyStamp;
    private bool _busy, _again, _checking;
    public TorLinkPlacement(TorLinkState state, TorLinkImporter importer) { State = state; Importer = importer; }
    public TorLinkState State { get; }
    public TorLinkImporter Importer { get; }
    /// <summary>The library folders in use: chosen in Réglages → TorLink, otherwise those of Jellyfin this PC reaches.</summary>
    public MediaLibraries Libraries { get; private set; } = new(null, null, null);
    /// <summary>Jellyfin's library folders that this PC reaches.</summary>
    public MediaLibraries Detected { get; private set; } = new(null, null, null);
    /// <summary>Jellyfin's folders were looked for: nothing is placed before.</summary>
    public bool Ready { get; private set; }
    /// <summary>A few downloads TorLink finished before automatic placement, to place on request.</summary>
    public List<TorLinkCompletion> Earlier { get; private set; } = [];
    /// <summary>TorLink rewrote history.json, as it does each time a download finishes. Raised on the watcher's thread.</summary>
    public event Action? HistoryChanged;

    /// <summary>Watches TorLink's history, once its folder exists.</summary>
    public void Watch()
    {
        if (_watcher is not null || !Directory.Exists(State.DataDirectory)) return;
        try
        {
            var watcher = new FileSystemWatcher(State.DataDirectory) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            void Changed(string? name) { if (string.Equals(name, "history.json", StringComparison.OrdinalIgnoreCase)) HistoryChanged?.Invoke(); }
            watcher.Changed += (_, e) => Changed(e.Name); watcher.Created += (_, e) => Changed(e.Name); watcher.Renamed += (_, e) => Changed(e.Name);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
    }
    /// <summary>The safety net for missed file events: the history changed since the last round, or a download still waits.</summary>
    public bool PlacementDue() => HistoryStamp() != _historyStamp || Importer.Entries.Any(x => x.State == TorLinkImportState.Waiting);
    private (DateTime, long) HistoryStamp()
    {
        try { var file = new FileInfo(State.HistoryFile); return file.Exists ? (file.LastWriteTimeUtc, file.Length) : default; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
    }

    /// <summary>Jellyfin's library folders (none without <paramref name="client"/>), kept only where this PC reaches them.</summary>
    public async Task DetectLibrariesAsync(JellyfinClient? client, CancellationToken token)
    {
        List<VirtualFolder>? folders = null;
        if (client is not null)
        {
            try { folders = await client.VirtualFoldersAsync(token); }
            catch (Exception ex) when (ExpectedErrors.Are(ex)) { }
        }
        // A remote or containerised server may describe paths this PC does not have: those are not used.
        var detected = MediaLibraries.FromJellyfin(folders);
        Detected = await Task.Run(() => new MediaLibraries(Reachable(detected.Movies), Reachable(detected.Series), Reachable(detected.Anime)));
        Ready = true;
    }
    /// <summary>The folders chosen in Réglages → TorLink first (empty: the one found). True when the folders in use changed.</summary>
    public bool Choose(string movies, string series, string anime)
    {
        var previous = Libraries;
        Libraries = new(Manual(movies) ?? Detected.Movies, Manual(series) ?? Detected.Series, Manual(anime) ?? Detected.Anime);
        return Libraries != previous;
    }
    private static string? Manual(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Reachable(string? path)
    {
        try { return path is not null && Directory.Exists(path) ? path : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>A download to place at the next round, even without automatic placement.</summary>
    public void Request(string id) => _requested.Add(id);
    /// <summary>
    /// Places what TorLink finished, then <paramref name="placed"/>; again while a round was asked for meanwhile, never two
    /// at once. <paramref name="options"/> is read at each round: automatic placement, and whether files keep being shared.
    /// </summary>
    public async Task PlaceAsync(Func<(bool Automatic, ImportMode Mode)> options, Func<Task> placed, CancellationToken token)
    {
        if (!Ready || token.IsCancellationRequested) return;
        if (_busy) { _again = true; return; }
        _busy = true;
        try
        {
            do
            {
                _again = false;
                _historyStamp = HistoryStamp();
                var libraries = Libraries; var (automatic, mode) = options();
                var requested = _requested.ToList(); _requested.Clear();
                var state = State; var importer = Importer;
                // File work stays on the thread pool: a copy never slows the interface or the video.
                Earlier = await Task.Run(async () =>
                {
                    await importer.ProcessAsync(state, libraries, mode, automatic, requested, token);
                    // A better version of a title is never offered as a download to place.
                    var known = importer.Entries.Select(x => x.Id).Concat(importer.Replacements.Select(x => x.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    return (state.ReadHistory() ?? []).Where(x => !known.Contains(x.Id)).Take(5).ToList();
                }, token);
                await placed();
            } while (_again && !token.IsCancellationRequested);
        }
        catch (OperationCanceledException) { }
        finally { _busy = false; }
    }

    /// <summary>Tells Jellyfin where new titles arrived; its own folder monitor would find them too, a little later.</summary>
    public async Task NotifyAsync(JellyfinClient client, CancellationToken token)
    {
        foreach (var entry in Importer.Entries.Where(x => !x.Notified && x.State is TorLinkImportState.Imported or TorLinkImportState.Partial && x.Folders.Count > 0).Take(10).ToList())
        {
            try { if (await client.ReportMediaChangedAsync(entry.Folders, ct: token)) Importer.MarkNotified(entry.Id); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Finds the placed titles Jellyfin has indexed, by their exact file paths: one request for all of them.</summary>
    public async Task FindIndexedAsync(JellyfinClient client, CancellationToken token)
    {
        if (_checking) return;
        var pending = Importer.Entries.Where(x => x.JellyfinId is null && x.State is TorLinkImportState.Imported or TorLinkImportState.Partial
            && DateTimeOffset.UtcNow - x.UpdatedAt < TimeSpan.FromHours(6) && x.Files.Any(f => f.Role == "video")).Take(20).ToList();
        if (pending.Count == 0) return;
        _checking = true;
        try
        {
            var found = await client.FindIndexedAsync(pending.SelectMany(x => x.Files.Where(f => f.Role == "video").Select(f => f.Destination)).ToList(), token);
            foreach (var entry in pending)
                if (entry.Files.Where(f => f.Role == "video").Select(f => found.GetValueOrDefault(f.Destination)).FirstOrDefault(x => x is not null) is { } id)
                    Importer.MarkAvailable(entry.Id, id);
        }
        catch (Exception ex) when (ExpectedErrors.Are(ex)) { }
        finally { _checking = false; }
    }

    /// <summary>Moves a placed title to the library of <paramref name="kind"/>, and tells Jellyfin (when given) about both.</summary>
    public async Task ReclassifyAsync(string id, MediaKind kind, JellyfinClient? client, CancellationToken token)
    {
        var libraries = Libraries; var importer = Importer;
        try
        {
            var (entry, left) = await Task.Run(() => importer.ReclassifyAsync(id, kind, libraries, token), token);
            if (entry is { State: TorLinkImportState.Imported or TorLinkImportState.Partial } && client is not null
                && await client.ReportMediaChangedAsync(entry.Folders, left, token)) importer.MarkNotified(entry.Id);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Just before TorLink starts, closed at this point: the downloads moved into the library stop being shared (started
    /// again, TorLink would fetch pieces of them back), and a TorLink never set up downloads on the library's drive, where
    /// each move is a rename. Returns that folder when it was chosen.
    /// </summary>
    public string? PrepareStart()
    {
        if (Importer.MovedSeeds() is { Count: > 0 } moved) Importer.MarkSeedPaused(State.PauseSeeds(moved));
        return Ready && TorLinkState.DownloadFolderBeside(Libraries, State.DownloadDirectory()) is { } folder && State.ChooseDownloadDirectory(folder) ? folder : null;
    }

    /// <summary>An outcome not told yet (one message per outcome, not one per step): false while it is still being placed.</summary>
    public bool FirstOutcome(TorLinkImportEntry entry) =>
        entry.State is not (TorLinkImportState.Importing or TorLinkImportState.Waiting) && _announced.Add($"{entry.Id}:{entry.State}:{entry.JellyfinId is not null}");
    /// <summary>What Mira says of an outcome, if anything.</summary>
    public static string? Outcome(TorLinkImportEntry entry) => entry.State switch
    {
        TorLinkImportState.Imported when entry.Replaced is not null => L($"« {TitleOf(entry)} » : la meilleure version a pris la place de l’ancienne, mise à la Corbeille.", $"“{TitleOf(entry)}”: the better version took the old one’s place, moved to the Recycle Bin."),
        TorLinkImportState.Failed when entry.Replaced is not null => L($"« {TitleOf(entry)} » n’a pas été remplacé : {entry.Message}", $"“{TitleOf(entry)}” wasn’t replaced: {entry.Message}"),
        TorLinkImportState.Imported when entry.JellyfinId is not null => L($"« {TitleOf(entry)} » est disponible dans ta bibliothèque.", $"“{TitleOf(entry)}” is available in your library."),
        TorLinkImportState.Imported => L($"« {TitleOf(entry)} » rejoint {KindName(entry.Kind)}. Jellyfin l’ajoute à la bibliothèque.", $"“{TitleOf(entry)}” goes to {KindName(entry.Kind)}. Jellyfin is adding it to the library."),
        TorLinkImportState.Partial => L($"« {TitleOf(entry)} » : une partie des fichiers est rangée. Détails dans TorLink.", $"“{TitleOf(entry)}”: some of the files are in place. Details in TorLink."),
        TorLinkImportState.Conflict => L($"« {TitleOf(entry)} » est déjà dans la bibliothèque : rien n’a été remplacé.", $"“{TitleOf(entry)}” is already in the library: nothing was replaced."),
        TorLinkImportState.Failed => L($"« {TitleOf(entry)} » n’a pas pu être rangé. Détails dans TorLink.", $"“{TitleOf(entry)}” couldn’t be put in place. Details in TorLink."),
        TorLinkImportState.Blocked => L("TorLink : choisis les dossiers de ta bibliothèque dans Réglages → TorLink.", "TorLink: choose your library folders in Settings → TorLink."),
        _ => null
    };
    /// <summary>The line above the list of downloads.</summary>
    public string Summary(bool automatic)
    {
        var libraries = Libraries;
        var missing = new[] { (L("Films", "Movies"), libraries.RootFor(MediaKind.Movie)), (L("Séries", "Shows"), libraries.RootFor(MediaKind.Series)) }.Where(x => x.Item2 is null).Select(x => x.Item1).ToList();
        if (!automatic) return L("Rangement automatique désactivé : « Importer » range un téléchargement à la demande.", "Automatic sorting is off: “Import” puts a download in place when you ask.");
        if (missing.Count == 2) return L("Choisis les dossiers de ta bibliothèque dans Réglages → TorLink pour ranger les téléchargements.", "Choose your library folders in Settings → TorLink to sort downloads.");
        if (missing.Count == 1) return L($"Rangement automatique. Dossier {missing[0]} à choisir dans Réglages → TorLink.", $"Automatic sorting. Choose the {missing[0]} folder in Settings → TorLink.");
        return libraries.Anime is null ? L("Rangement automatique dans Films et Séries (les animes rejoignent les séries).", "Automatic sorting into Movies and Shows (anime goes with shows).") : L("Rangement automatique dans Films, Séries et Animes.", "Automatic sorting into Movies, Shows and Anime.");
    }
    public static string TitleOf(TorLinkImportEntry entry) => entry.Title.Length > 0 ? entry.Title : entry.Name;
    public static string KindName(MediaKind? kind) => kind switch { MediaKind.Movie => L("Films", "Movies"), MediaKind.Series => L("Séries", "Shows"), MediaKind.Anime => L("Animes", "Anime"), _ => L("la bibliothèque", "the library") };
    public static string StatusOf(TorLinkImportEntry entry) => entry.State switch
    {
        TorLinkImportState.Imported when entry.Replaced is not null => L("meilleure version en place, l’ancienne dans la Corbeille", "better version in place, the old one in the Recycle Bin"),
        TorLinkImportState.Failed when entry.Replaced is not null => L($"pas remplacé : {entry.Message}", $"not replaced: {entry.Message}"),
        TorLinkImportState.Waiting => L("en attente des fichiers", "waiting for the files"),
        TorLinkImportState.Blocked => L("dossier de bibliothèque à choisir", "library folder to choose"),
        TorLinkImportState.Importing => L("rangement en cours…", "sorting…"),
        TorLinkImportState.Imported => entry.JellyfinId is not null ? L("disponible dans Jellyfin", "available in Jellyfin") : entry.Notified ? L("Jellyfin l’ajoute", "Jellyfin is adding it") : L("rangé", "in place"),
        TorLinkImportState.Partial => L("rangé en partie", "partly in place"),
        TorLinkImportState.Conflict => L("déjà dans la bibliothèque", "already in the library"),
        TorLinkImportState.Failed => L("échec", "failed"),
        _ => L("ignoré", "skipped")
    };
    public static string DetailOf(TorLinkImportEntry entry)
    {
        var status = StatusOf(entry);
        var line = entry.Kind is { } kind && entry.State is not (TorLinkImportState.Ignored or TorLinkImportState.Waiting) ? $"{KindName(kind)} · {status}" : char.ToUpper(status[0], Culture) + status[1..];
        if (entry.Method is { } method && entry.State is TorLinkImportState.Imported or TorLinkImportState.Partial) line += " · " + method;
        // A replacement that failed says why in its status already.
        return entry.Message is { Length: > 0 } message && !(entry.Replaced is not null && entry.State == TorLinkImportState.Failed) ? line + "\n" + message : line;
    }

    public void Dispose() { _watcher?.Dispose(); _watcher = null; }
}
