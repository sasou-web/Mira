using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mira.Core;

[JsonConverter(typeof(JsonStringEnumConverter<TorLinkImportState>))]
public enum TorLinkImportState { Waiting, Blocked, Importing, Imported, Partial, Conflict, Failed, Ignored }

/// <summary>
/// A library file of an import, with its path inside the torrent (kept to re-plan a reclassification).
/// Found: it was already in the library, identical in size, before Mira placed anything; Mira never moves it.
/// </summary>
public sealed record TorLinkImportedFile(string RelativePath, string Destination, string Role, bool Found = false);

/// <summary>What Mira did with one finished TorLink download.</summary>
public sealed record TorLinkImportEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Source { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public TorLinkImportState State { get; init; } = TorLinkImportState.Waiting;
    public MediaKind? Kind { get; init; }
    /// <summary>The kind chosen with « Classer comme… »: a new attempt keeps it instead of guessing again.</summary>
    public MediaKind? ForcedKind { get; init; }
    public string Title { get; init; } = "";
    public string? LibraryRoot { get; init; }
    public IReadOnlyList<string> Folders { get; init; } = [];
    public IReadOnlyList<TorLinkImportedFile> Files { get; init; } = [];
    public string? Method { get; init; }
    /// <summary>Files left TorLink's folder for the library: TorLink must not share this download any more.</summary>
    public bool MovedOut { get; init; }
    /// <summary>TorLink's sharing of the moved download is paused (see <see cref="TorLinkState.PauseSeeds"/>).</summary>
    public bool SeedPaused { get; init; }
    public string? Message { get; init; }
    public int Attempts { get; init; }
    public bool Notified { get; init; }
    public string? JellyfinId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class TorLinkImportLog
{
    /// <summary>Automatic imports only concern downloads finished after this moment (activation of the feature).</summary>
    public DateTimeOffset Baseline { get; set; }
    public List<TorLinkImportEntry> Entries { get; set; } = [];
}

/// <summary>
/// Places finished TorLink downloads in the Jellyfin libraries and remembers each decision (data/torlink-imports.json).
/// One import runs at a time; files are never overwritten and, by default, TorLink keeps its own copy to share.
/// </summary>
public sealed class TorLinkImporter
{
    public const int MaxAttempts = 6;
    private const int MaxEntries = 500;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly TorLinkImportLog _log;
    private MediaLibraries? _blockedWith;
    /// <summary>Raised on the worker thread after an entry changed and was saved.</summary>
    public event Action<TorLinkImportEntry>? Changed;

    public TorLinkImporter(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "torlink-imports.json");
        _log = Load();
    }
    public DateTimeOffset Baseline { get { lock (_sync) return _log.Baseline; } }
    public IReadOnlyList<TorLinkImportEntry> Entries { get { lock (_sync) return _log.Entries.ToList(); } }
    public TorLinkImportEntry? Find(string id) { lock (_sync) return _log.Entries.FirstOrDefault(x => x.Id == id); }

    /// <summary>Automatic imports restart from now (used when the option is switched back on).</summary>
    public void RestartBaseline() { lock (_sync) _log.Baseline = DateTimeOffset.UtcNow; Save(); }

    /// <summary>
    /// Imports the downloads TorLink finished since the baseline (when <paramref name="automatic"/>), retries the ones
    /// still waiting for their files or for a library folder, and imports the <paramref name="requested"/> ones in any case.
    /// </summary>
    public async Task<int> ProcessAsync(TorLinkState state, MediaLibraries libraries, ImportMode mode, bool automatic, IReadOnlyCollection<string>? requested = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var history = await Task.Run(state.ReadHistory, ct);
            if (history is null) return 0;
            var baseline = Baseline; var processed = 0;
            foreach (var completion in history.OrderBy(x => x.CompletedAt))
            {
                ct.ThrowIfCancellationRequested();
                var entry = Find(completion.Id);
                var asked = requested?.Contains(completion.Id) == true;
                // A download blocked by a missing library folder is tried again once the folders change.
                var due = asked || (entry is null
                    ? automatic && completion.CompletedAt >= baseline
                    : entry.State is TorLinkImportState.Waiting && entry.Attempts < MaxAttempts || entry.State is TorLinkImportState.Blocked && !Equals(libraries, _blockedWith));
                if (!due) continue;
                await ImportAsync(completion, entry, state, libraries, mode, ct);
                processed++;
            }
            return processed;
        }
        finally { _gate.Release(); }
    }

    private async Task ImportAsync(TorLinkCompletion completion, TorLinkImportEntry? previous, TorLinkState state, MediaLibraries libraries, ImportMode mode, CancellationToken ct)
    {
        var entry = previous ?? new TorLinkImportEntry { Id = completion.Id, Name = completion.Name, Source = completion.Source, CompletedAt = completion.CompletedAt };
        entry = entry with { State = TorLinkImportState.Importing, Message = null, UpdatedAt = DateTimeOffset.UtcNow };
        Update(entry);
        // Files an earlier attempt already placed stay tracked; a move took them out of TorLink's folder.
        var tracked = await Task.Run(() => entry.Files.Where(f => File.Exists(f.Destination)).ToList(), ct);
        var files = await Task.Run(() => DownloadedFiles(state, completion, tracked), ct);
        if (files is null)
        {
            var attempts = entry.Attempts + 1;
            Update(entry with
            {
                State = attempts >= MaxAttempts ? TorLinkImportState.Failed : TorLinkImportState.Waiting,
                Attempts = attempts,
                UpdatedAt = DateTimeOffset.UtcNow,
                Message = attempts >= MaxAttempts ? "Les fichiers terminés sont introuvables dans le dossier de TorLink." : "Fichiers pas encore disponibles, nouvel essai bientôt."
            });
            return;
        }
        var plan = await Task.Run(() => MediaPlanner.Plan(completion.Name, completion.Source, files, libraries, entry.ForcedKind), ct);
        if (plan.Ignored is { } reason) { Update(entry with { State = TorLinkImportState.Ignored, Message = reason, UpdatedAt = DateTimeOffset.UtcNow }); return; }
        if (plan.Problem is not null || !Directory.Exists(plan.LibraryRoot))
        {
            _blockedWith = libraries;
            Update(entry with { State = TorLinkImportState.Blocked, Kind = plan.Kind, Message = plan.Problem ?? $"Dossier de bibliothèque introuvable : {plan.LibraryRoot}", UpdatedAt = DateTimeOffset.UtcNow });
            return;
        }
        var result = await MediaImporter.ExecuteAsync(plan, mode, ct);
        // The download's folders in TorLink's, once emptied by the move, go too; TorLink's own folder stays.
        if (result.Moved) MediaImporter.PruneEmptyFolders(result.Placed.Select(x => Path.GetDirectoryName(x.Source)), [completion.Directory]);
        var outcome = Outcome(entry, plan, result, tracked);
        // Only for a download leaving TorLink: a reclassified file crossing drives says nothing about TorLink's folder.
        if (result.OtherDrive) outcome = outcome with { Message = string.Join(" · ", new[] { outcome.Message, OtherDriveHint }.OfType<string>()) };
        // Interrupted by closing Mira: what was placed is kept, the rest follows at the next start.
        Update(result.Canceled ? outcome with { State = TorLinkImportState.Waiting, Message = "Rangement interrompu : il reprendra à la prochaine ouverture de Mira." } : outcome);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Moves an imported download to another kind of library (film, series, anime), keeping its files.</summary>
    /// <returns>The entry and the library folders it left, to report to Jellyfin.</returns>
    public async Task<(TorLinkImportEntry? Entry, IReadOnlyList<string> Left)> ReclassifyAsync(string id, MediaKind kind, MediaLibraries libraries, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Find(id) is not { } entry || entry.Files.Count == 0 || entry.Kind == kind) return (Find(id), []);
            // Only the links and copies Mira placed move; a file that was already in the library stays where it is.
            var sources = entry.Files.Where(f => !f.Found && File.Exists(f.Destination)).Select(f => new SourceFile(f.Destination, f.RelativePath, new FileInfo(f.Destination).Length)).ToList();
            if (sources.Count == 0)
            {
                var gone = entry with
                {
                    Message = entry.Files.All(f => f.Found) ? "Ces fichiers étaient déjà dans la bibliothèque : Mira ne les déplace pas." : "Les fichiers importés ont été déplacés ou supprimés depuis.",
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                Update(gone); return (gone, []);
            }
            var plan = await Task.Run(() => MediaPlanner.Plan(entry.Name, entry.Source, sources, libraries, kind), ct);
            if (plan.Problem is not null || plan.Ignored is not null || !Directory.Exists(plan.LibraryRoot))
            {
                // Ignored: the video was already in the library before Mira, so only side files would move.
                var message = plan.Problem ?? (plan.Ignored is not null ? "La vidéo était déjà dans la bibliothèque : le classement reste tel quel." : $"Dossier de bibliothèque introuvable : {plan.LibraryRoot}");
                var blocked = entry with { Message = message, UpdatedAt = DateTimeOffset.UtcNow };
                Update(blocked); return (blocked, []);
            }
            // Only Mira's own copies or links move here; TorLink's download is not touched.
            var result = await MediaImporter.ExecuteAsync(plan, ImportMode.Move, ct);
            var moved = result.Placed.Select(x => x.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stayed = entry.Files.Where(f => !moved.Contains(f.Destination)).ToList();
            MediaImporter.PruneEmptyFolders(entry.Files.Select(f => Path.GetDirectoryName(f.Destination)), [libraries.Movies, libraries.Series, libraries.Anime, entry.LibraryRoot]);
            // Moving Mira's own link or copy does not change how the file first arrived. Interrupted by closing Mira:
            // every file stays tracked where it now is, and choosing the same kind again finishes the move.
            var updated = result.Canceled
                ? entry with
                {
                    State = TorLinkImportState.Partial,
                    Folders = entry.Folders.Concat(plan.Folders).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Files = stayed.Concat(result.Placed.Select(x => new TorLinkImportedFile(x.RelativePath, x.Destination, x.Role))).ToList(),
                    Message = "Classement interrompu : choisis à nouveau « Classer » pour le terminer.",
                    Notified = false,
                    UpdatedAt = DateTimeOffset.UtcNow
                }
                : Outcome(entry, plan, result, stayed) with { Method = entry.Method, MovedOut = entry.MovedOut, ForcedKind = kind };
            Update(updated);
            return (updated, entry.Folders.Where(x => !Directory.Exists(x) || !updated.Folders.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList());
        }
        finally { _gate.Release(); }
    }

    public void MarkNotified(string id) => Change(id, x => x with { Notified = true });
    /// <summary>Downloads moved out of TorLink's folder whose sharing is not paused yet.</summary>
    public IReadOnlyList<string> MovedSeeds() { lock (_sync) return _log.Entries.Where(x => x.MovedOut && !x.SeedPaused).Select(x => x.Id).ToList(); }
    public void MarkSeedPaused(IEnumerable<string> ids) { foreach (var id in ids) Change(id, x => x with { SeedPaused = true }); }
    public void MarkAvailable(string id, string jellyfinId) => Change(id, x => x with { JellyfinId = jellyfinId });

    /// <summary>
    /// Finished files of a download, only once every video and subtitle is complete on disk; never outside TorLink's folder.
    /// A file an earlier attempt moved into the library (<paramref name="placed"/>) counts where it now is.
    /// </summary>
    public static List<SourceFile>? DownloadedFiles(TorLinkState state, TorLinkCompletion completion, IReadOnlyList<TorLinkImportedFile>? placed = null)
    {
        string root;
        try { root = Path.GetFullPath(completion.Directory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var prefix = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        bool Inside(string path) => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        var ours = (placed ?? []).Where(f => !f.Found).GroupBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Destination, StringComparer.OrdinalIgnoreCase);
        SourceFile? Placed(string relative, long? length)
        {
            if (!ours.TryGetValue(relative, out var destination)) return null;
            var info = new FileInfo(destination);
            return info.Exists && (length is null || info.Length == length) ? new(destination, relative, info.Length) : null;
        }
        try
        {
            if (state.ReadTorrent(completion.Id) is { Files.Count: > 0 } torrent)
            {
                var files = new List<SourceFile>();
                foreach (var file in torrent.Files)
                {
                    var path = Path.GetFullPath(Path.Combine(root, file.RelativePath));
                    if (!Inside(path)) continue;
                    var media = ReleaseName.IsVideo(path) || ReleaseName.IsSubtitle(path);
                    var info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        if (!media) continue;
                        if (Placed(file.RelativePath, file.Length) is { } moved) { files.Add(moved); continue; }
                        return null;
                    }
                    if (media && info.Length != file.Length) return null;
                    files.Add(new(path, file.RelativePath, info.Length));
                }
                return files;
            }
            // Without saved metadata: the file or folder named after the torrent, as WebTorrent names it.
            var sanitized = new string(completion.Name.Where(c => c >= 32 && "<>:\"/\\|?*".IndexOf(c) < 0).ToArray());
            foreach (var name in new[] { completion.Name, sanitized }.Where(x => x.Length > 0).Distinct())
            {
                var path = Path.GetFullPath(Path.Combine(root, name));
                if (!Inside(path)) continue;
                if (File.Exists(path)) return [new(path, name, new FileInfo(path).Length)];
                if (!Directory.Exists(path)) continue;
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
                var found = Directory.EnumerateFiles(path, "*", options).Take(20_000)
                    .Select(f => new SourceFile(f, Path.Combine(name, Path.GetRelativePath(path, f)), new FileInfo(f).Length)).ToList();
                var present = found.Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return found.Concat(ours.Keys.Where(x => !present.Contains(x)).Select(x => Placed(x, null)).OfType<SourceFile>()).ToList();
            }
            // Everything already moved into the library by an earlier attempt.
            var moves = ours.Keys.Select(x => Placed(x, null)).OfType<SourceFile>().ToList();
            return moves.Count > 0 ? moves : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>Said when files crossed from another drive: the way to make the next moves instant.</summary>
    public const string OtherDriveHint = "TorLink télécharge sur un autre disque que la bibliothèque : chaque fichier a été copié puis supprimé. Pour un déplacement instantané, choisis dans TorLink (touche o) un dossier sur le disque de la bibliothèque.";

    private static TorLinkImportEntry Outcome(TorLinkImportEntry entry, ImportPlan plan, ImportResult result, IReadOnlyList<TorLinkImportedFile> kept)
    {
        var placed = result.Placed.Concat(result.AlreadyThere).ToList();
        var total = plan.Operations.Count;
        var state = placed.Count == total ? TorLinkImportState.Imported
            : placed.Count > 0 ? TorLinkImportState.Partial
            : result.Conflicts.Count > 0 ? TorLinkImportState.Conflict : TorLinkImportState.Failed;
        var problems = result.Conflicts.Count + result.Failures.Count;
        var message = state switch
        {
            TorLinkImportState.Imported => plan.Notes.Count > 0 ? string.Join(" · ", plan.Notes) : null,
            TorLinkImportState.Partial => $"{placed.Count} fichier{(placed.Count > 1 ? "s" : "")} placé{(placed.Count > 1 ? "s" : "")}, {problems} non placé{(problems > 1 ? "s" : "")} : " + (result.Conflicts.FirstOrDefault()?.Reason ?? result.Failures.FirstOrDefault()?.Reason),
            TorLinkImportState.Conflict => "Déjà dans la bibliothèque avec un fichier différent : rien n’a été remplacé.",
            _ => result.Failures.FirstOrDefault()?.Reason ?? "Aucun fichier n’a pu être placé."
        };
        // A same-size file already at its destination is Mira's own when it was tracked, when it is the source itself
        // (a file moved earlier) or a hard link of it; otherwise it was found there and Mira will never move it.
        var ours = kept.Select(x => x.Destination).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = kept
            .Concat(result.Placed.Select(x => new TorLinkImportedFile(x.RelativePath, x.Destination, x.Role)))
            .Concat(result.AlreadyThere.Select(x => new TorLinkImportedFile(x.RelativePath, x.Destination, x.Role, Found: !ours.Contains(x.Destination) && !MediaImporter.IsSameFile(x.Source, x.Destination))))
            .DistinctBy(x => x.Destination, StringComparer.OrdinalIgnoreCase).ToList();
        return entry with
        {
            State = state,
            Kind = plan.Kind,
            Title = plan.Title,
            LibraryRoot = plan.LibraryRoot,
            Folders = plan.Folders,
            Files = files,
            Method = result.Method ?? entry.Method ?? (result.AlreadyThere.Count > 0 ? "déjà présent" : null),
            Message = message,
            MovedOut = entry.MovedOut || result.Moved,
            Notified = false,
            JellyfinId = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private void Change(string id, Func<TorLinkImportEntry, TorLinkImportEntry> change)
    {
        TorLinkImportEntry? updated = null;
        lock (_sync)
        {
            var index = _log.Entries.FindIndex(x => x.Id == id);
            if (index < 0) return;
            updated = _log.Entries[index] = change(_log.Entries[index]);
        }
        Save(); Changed?.Invoke(updated);
    }

    private void Update(TorLinkImportEntry entry)
    {
        lock (_sync)
        {
            _log.Entries.RemoveAll(x => x.Id == entry.Id);
            _log.Entries.Insert(0, entry);
            if (_log.Entries.Count > MaxEntries) _log.Entries.RemoveRange(MaxEntries, _log.Entries.Count - MaxEntries);
        }
        Save(); Changed?.Invoke(entry);
    }

    private TorLinkImportLog Load()
    {
        if (File.Exists(_path))
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (JsonSerializer.Deserialize<TorLinkImportLog>(File.ReadAllText(_path), Json.Options) is not { } log) break;
                    // An import interrupted by closing Mira is simply tried again; incomplete entries are dropped.
                    log.Entries = (log.Entries ?? []).Where(x => x is not null && x.Id is not null && TorLinkState.IsInfoHash(x.Id))
                        .Select(x => x with
                        {
                            State = x.State == TorLinkImportState.Importing ? TorLinkImportState.Waiting : x.State,
                            Name = x.Name ?? "",
                            Title = x.Title ?? "",
                            Folders = x.Folders ?? [],
                            Files = (x.Files ?? []).Where(f => f is { Destination: not null, RelativePath: not null }).ToList()
                        }).ToList();
                    return log;
                }
                // A scanner or a backup may hold the file for a moment.
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 4) { Thread.Sleep(150); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { break; }
            }
            // Unreadable: kept aside instead of being lost, then a new log starts.
            try { File.Copy(_path, _path + ".bad-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"), true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var created = new TorLinkImportLog { Baseline = DateTimeOffset.UtcNow };
        Write(created);
        return created;
    }

    private void Save() { lock (_sync) Write(_log); }
    private void Write(TorLinkImportLog log)
    {
        try
        {
            var temporary = _path + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(log, Json.Options));
            File.Move(temporary, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
