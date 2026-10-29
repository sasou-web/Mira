using System.Text.Json;
using System.Text.Json.Serialization;
using Mira.Core.TorLink;

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
    /// <summary>« Remplacer par une meilleure version »: the title's file this download was to replace (<see cref="TorLinkReplacement"/>).</summary>
    public string? Replaced { get; init; }
}

public sealed class TorLinkImportLog
{
    /// <summary>Automatic imports only concern downloads finished after this moment (activation of the feature).</summary>
    public DateTimeOffset Baseline { get; set; }
    public List<TorLinkImportEntry> Entries { get; set; } = [];
    /// <summary>Downloads sent for a series followed: placed where they were chosen for, even with automatic placement off.</summary>
    public List<TorLinkExpected> Expected { get; set; } = [];
    /// <summary>Downloads that take a title's place once finished (« Remplacer par une meilleure version »): never imported as new titles.</summary>
    public List<TorLinkReplacement> Replacements { get; set; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<ReplacementState>))]
public enum ReplacementState { Waiting, Swapping, Swapped, Done, Failed }

/// <summary>
/// « Remplacer par une meilleure version »: a download (its info hash) that takes the place of a film's or an episode's
/// file once finished, checked (readable, as long as the old one) and swapped (<see cref="TitleSwap"/>): its title in
/// Jellyfin, the old file and its length (ticks, as Jellyfin measured it), then where it is: <see cref="ReplacementState.Swapping"/>
/// while the files change, <see cref="ReplacementState.Swapped"/> until Jellyfin has the new file and the state of the
/// old title (<see cref="Saved"/>) is copied onto it, <see cref="ReplacementState.Done"/> or <see cref="ReplacementState.Failed"/>
/// (<see cref="Message"/>: why; the old file stays).
/// </summary>
public sealed record TorLinkReplacement
{
    public string Id { get; init; } = "";
    public string ItemId { get; init; } = "";
    public string Title { get; init; } = "";
    public string OldPath { get; init; } = "";
    public long Runtime { get; init; }
    /// <summary>The old file's size when the files began to change: told from the new one when both have the same name.</summary>
    public long OldSize { get; init; }
    public ReplacementState State { get; init; } = ReplacementState.Waiting;
    public string? NewPath { get; init; }
    public string? NewItemId { get; init; }
    public TitleSnapshot? Saved { get; init; }
    public string? Message { get; init; }
    /// <summary>Times the old file was open when its turn came (someone watching, Jellyfin reading it), and when to try again.</summary>
    public int Attempts { get; init; }
    public DateTimeOffset? RetryAt { get; init; }
    public DateTimeOffset At { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// What Mira for Windows says before a file is replaced: the old title's state to copy onto the new one, why it cannot
/// be replaced (<paramref name="Problem"/>: the title is gone or moved), or why it waits (<paramref name="Wait"/>: watched now).
/// </summary>
public sealed record ReplacementCheck(TitleSnapshot? Saved, string? Problem = null, string? Wait = null);

/// <summary>What Jellyfin knew of a title before its file was replaced, copied onto the new one: each account's state, the collections holding it.</summary>
public sealed record TitleSnapshot(List<SavedUserData> Users, List<string> Collections);
/// <summary>An account's state of a title: watched, how many times, where it resumes, favourite, last watched, liked, rated.</summary>
public sealed record SavedUserData(string UserId, bool Played, int PlayCount, long PlaybackPositionTicks, bool IsFavorite, DateTimeOffset? LastPlayedDate, bool? Likes, double? Rating)
{
    /// <summary>Something to copy: an account that never touched the title has nothing.</summary>
    [JsonIgnore] public bool Kept => Played || PlayCount > 0 || PlaybackPositionTicks > 0 || IsFavorite || Likes is not null || Rating is not null;
}

/// <summary>A download sent to TorLink for a series followed (its info hash), and where it goes once finished.</summary>
public sealed record TorLinkExpected(string Id, ImportTarget Target, DateTimeOffset At);

/// <summary>
/// Places finished TorLink downloads in the Jellyfin libraries and remembers each decision (data/torlink-imports.json).
/// One import runs at a time; files are never overwritten and, by default, TorLink keeps its own copy to share.
/// </summary>
public sealed class TorLinkImporter
{
    public const int MaxAttempts = 6;
    private const int MaxEntries = 500, MaxExpected = 200, MaxReplacements = 100;
    /// <summary>An old file open this many times in a row, <see cref="RetryEvery"/> apart, is given up: about an hour.</summary>
    private const int MaxBusy = 30;
    private static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(2);
    /// <summary>A download expected that long and never finished is forgotten.</summary>
    private static readonly TimeSpan ExpectedFor = TimeSpan.FromDays(30);
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

    /// <summary>« Suivre une série »: this download (its info hash) goes to <paramref name="target"/> once finished.</summary>
    public void Expect(string id, ImportTarget target)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            _log.Expected.RemoveAll(x => x.Id == id || now - x.At > ExpectedFor);
            _log.Expected.Insert(0, new(id.ToLowerInvariant(), target, now));
            if (_log.Expected.Count > MaxExpected) _log.Expected.RemoveRange(MaxExpected, _log.Expected.Count - MaxExpected);
        }
        Save();
    }
    public ImportTarget? Expected(string id) { lock (_sync) return _log.Expected.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))?.Target; }

    /// <summary>« Remplacer par une meilleure version »: this download takes <paramref name="replacement"/>'s title's place once finished, never imported as a new title.</summary>
    public void ExpectReplacement(TorLinkReplacement replacement)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            // Finished ones are forgotten after the time a download is expected; one under way never is.
            _log.Replacements.RemoveAll(x => x.Id == replacement.Id || x.State is ReplacementState.Done or ReplacementState.Failed && now - x.UpdatedAt > ExpectedFor);
            _log.Replacements.Insert(0, replacement with { Id = replacement.Id.ToLowerInvariant(), State = ReplacementState.Waiting, At = now, UpdatedAt = now });
            while (_log.Replacements.Count > MaxReplacements && _log.Replacements.FindLastIndex(x => x.State is ReplacementState.Done or ReplacementState.Failed) is var last and >= 0) _log.Replacements.RemoveAt(last);
        }
        Save();
    }
    public TorLinkReplacement? Replacement(string id) { lock (_sync) return _log.Replacements.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)); }
    public IReadOnlyList<TorLinkReplacement> Replacements { get { lock (_sync) return _log.Replacements.ToList(); } }
    /// <summary>A replacement moved on (Jellyfin has the new title, its state copied, or given up).</summary>
    public void UpdateReplacement(TorLinkReplacement replacement)
    {
        lock (_sync)
        {
            var index = _log.Replacements.FindIndex(x => x.Id == replacement.Id);
            if (index < 0) return;
            _log.Replacements[index] = replacement with { UpdatedAt = DateTimeOffset.UtcNow };
        }
        Save();
    }

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
                // A better version of a title takes its place (ReplaceAsync), never imported beside it.
                if (Replacement(completion.Id) is not null) continue;
                var entry = Find(completion.Id);
                var asked = requested?.Contains(completion.Id) == true;
                var target = Expected(completion.Id);
                // A download blocked by a missing library folder is tried again once the folders change.
                var due = asked || (entry is null
                    ? automatic && completion.CompletedAt >= baseline || target is not null
                    : entry.State is TorLinkImportState.Waiting && entry.Attempts < MaxAttempts || entry.State is TorLinkImportState.Blocked && !Equals(libraries, _blockedWith));
                if (!due) continue;
                await ImportAsync(completion, entry, state, libraries, mode, target, ct);
                processed++;
            }
            return processed;
        }
        finally { _gate.Release(); }
    }

    private async Task ImportAsync(TorLinkCompletion completion, TorLinkImportEntry? previous, TorLinkState state, MediaLibraries libraries, ImportMode mode, ImportTarget? target, CancellationToken ct)
    {
        var entry = previous ?? new TorLinkImportEntry { Id = completion.Id, Name = completion.Name, Source = completion.Source, CompletedAt = completion.CompletedAt };
        entry = entry with { State = TorLinkImportState.Importing, Message = null, UpdatedAt = DateTimeOffset.UtcNow };
        Update(entry);
        // Files an earlier attempt already placed stay tracked; a move took them out of TorLink's folder.
        var tracked = await Task.Run(() => entry.Files.Where(f => File.Exists(f.Destination)).ToList(), ct);
        var (files, refused) = await Task.Run(() => (DownloadedFiles(state, completion, tracked, out var torrentRefused), torrentRefused), ct);
        string? Noted(string? message) => refused ? string.Join(" · ", new[] { message, TorrentRefusedHint }.OfType<string>()) : message;
        if (files is null)
        {
            var attempts = entry.Attempts + 1;
            Update(entry with
            {
                State = attempts >= MaxAttempts ? TorLinkImportState.Failed : TorLinkImportState.Waiting,
                Attempts = attempts,
                UpdatedAt = DateTimeOffset.UtcNow,
                Message = Noted(attempts >= MaxAttempts ? L("Les fichiers terminés sont introuvables dans le dossier de TorLink.", "The finished files can’t be found in TorLink’s folder.") : L("Fichiers pas encore disponibles, nouvel essai bientôt.", "Files not available yet, trying again soon."))
            });
            return;
        }
        // The shows placed before: an episode named otherwise joins its show (« JoJo no Kimyou na Bouken - 05 » after « JoJos Bizarre Adventure S06E04 »).
        var known = Entries.Where(x => x.Id != completion.Id).ToList();
        // « Classer comme… » another kind wins over the series it was sent for.
        if (entry.ForcedKind is { } forced && forced != target?.Kind) target = null;
        var plan = await Task.Run(() => MediaPlanner.Plan(completion.Name, completion.Source, files, libraries, entry.ForcedKind, MediaPlanner.KnownShows(known), target), ct);
        if (plan.Ignored is { } reason) { Update(entry with { State = TorLinkImportState.Ignored, Message = reason, UpdatedAt = DateTimeOffset.UtcNow }); return; }
        if (plan.Problem is not null || !Directory.Exists(plan.LibraryRoot))
        {
            _blockedWith = libraries;
            Update(entry with { State = TorLinkImportState.Blocked, Kind = plan.Kind, Message = plan.Problem ?? L($"Dossier de bibliothèque introuvable : {plan.LibraryRoot}", $"Library folder not found: {plan.LibraryRoot}"), UpdatedAt = DateTimeOffset.UtcNow });
            return;
        }
        var result = await MediaImporter.ExecuteAsync(plan, mode, ct);
        // The download's folders in TorLink's, once emptied by the move, go too; TorLink's own folder stays.
        if (result.Moved) MediaImporter.PruneEmptyFolders(result.Placed.Select(x => Path.GetDirectoryName(x.Source)), [completion.Directory]);
        var outcome = Outcome(entry, plan, result, tracked);
        // Only for a download leaving TorLink: a reclassified file crossing drives says nothing about TorLink's folder.
        if (result.OtherDrive) outcome = outcome with { Message = string.Join(" · ", new[] { outcome.Message, OtherDriveHint }.OfType<string>()) };
        outcome = outcome with { Message = Noted(outcome.Message) };
        // Interrupted by closing Mira: what was placed is kept, the rest follows at the next start.
        Update(result.Canceled ? outcome with { State = TorLinkImportState.Waiting, Message = L("Rangement interrompu : il reprendra à la prochaine ouverture de Mira.", "Sorting stopped: it’ll resume the next time Mira opens.") } : outcome);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>The replacements whose download TorLink has finished, to check and swap now (<see cref="ReplaceAsync"/>).</summary>
    public IReadOnlyList<TorLinkReplacement> Due(TorLinkState state)
    {
        var waiting = Replacements.Where(x => x.State is ReplacementState.Waiting or ReplacementState.Swapping).ToList();
        if (waiting.Count == 0) return [];
        var finished = (state.ReadHistory() ?? []).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return waiting.Where(x => finished.Contains(x.Id)).ToList();
    }

    /// <summary>
    /// « Remplacer par une meilleure version », once TorLink finished the download: its video checked (one film or episode,
    /// readable by <paramref name="probe"/>, as long as the old file within <see cref="TitleUpgrade.LengthTolerance"/>), then
    /// put in the old file's place, which goes to the Recycle Bin by <paramref name="recycle"/> (<see cref="TitleSwap"/>), and
    /// written in the log as a title placed. <paramref name="prepare"/>, just before: the old title's state, kept to copy
    /// onto the new one, or why it waits (watched now) or cannot be (gone from Jellyfin).
    /// Null while its files are not all there; otherwise the replacement as it now is. Nothing changes when a check fails:
    /// the old file stays, the download stays in TorLink.
    /// </summary>
    public async Task<TorLinkReplacement?> ReplaceAsync(string id, TorLinkState state, ImportMode mode, Func<TorLinkReplacement, CancellationToken, Task<ReplacementCheck>> prepare,
        Func<string, CancellationToken, Task<TimeSpan?>> probe, Func<string, bool> recycle, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Replacement(id) is not { State: ReplacementState.Waiting or ReplacementState.Swapping } replacement) return Replacement(id);
            if (replacement.RetryAt > DateTimeOffset.UtcNow) return replacement;
            var history = await Task.Run(state.ReadHistory, ct);
            if (history?.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) is not { } completion) return null;
            // Mira closed while the files changed: the old file still there means nothing happened (a new attempt clears
            // what it left); gone, the new one is in its place, or still beside it under its temporary name.
            if (replacement.State == ReplacementState.Swapping && Recover(replacement) is { } recovered)
                return recovered.Placed is { } path ? Swapped(replacement with { NewPath = path }, completion, path, false)
                    : Failed(replacement, completion, recovered.Problem ?? "", kept: false);
            if (replacement.State == ReplacementState.Swapping) replacement = replacement with { State = ReplacementState.Waiting };
            var files = await Task.Run(() => DownloadedFiles(state, completion), ct);
            if (files is null) return null;
            var videos = files.Where(f => ReleaseName.IsVideo(f.RelativePath) && !ReleaseName.InExtrasFolder(f.RelativePath)).ToList();
            var largest = videos.Count == 0 ? 0 : videos.Max(v => v.Length);
            // A clip called "sample" beside the feature is left out; anything else as long is another title.
            var features = videos.Where(v => v.Length >= largest * .5 && !(ReleaseName.IsSample(v.RelativePath) && v.Length < largest * .3)).ToList();
            if (features.Count != 1)
                return Failed(replacement, completion, features.Count == 0 ? L("Aucune vidéo dans ce téléchargement.", "No video in this download.")
                    : L("Ce téléchargement contient plusieurs vidéos : Mira ne sait pas laquelle garder.", "This download has several videos: Mira can’t tell which one to keep."));
            var video = features[0];
            if (await probe(video.Path, ct) is not { } length)
                return Failed(replacement, completion, L("Jellyfin ne sait pas lire la nouvelle version (ffprobe).", "Jellyfin can’t read the new version (ffprobe)."));
            var old = TimeSpan.FromTicks(replacement.Runtime);
            if (!TitleUpgrade.SameLength(old, length))
                return Failed(replacement, completion, L($"La nouvelle version dure {MediaItem.FormatDuration(length)}, l’ancienne {MediaItem.FormatDuration(old)} : un autre montage, ou un autre titre.",
                    $"The new version runs {MediaItem.FormatDuration(length)}, the old one {MediaItem.FormatDuration(old)}: another cut, or another title."));
            var check = await prepare(replacement, ct);
            if (check.Problem is { } problem) return Failed(replacement, completion, problem);
            if (check.Wait is { } wait) return Later(replacement, wait, counted: false);
            var subtitles = files.Where(f => ReleaseName.IsSubtitle(f.RelativePath) && !ReleaseName.IsSample(f.RelativePath) && !ReleaseName.InExtrasFolder(f.RelativePath)).ToList();
            var size = new FileInfo(replacement.OldPath) is { Exists: true } info ? info.Length : 0;
            replacement = replacement with { State = ReplacementState.Swapping, NewPath = TitleSwap.Destination(replacement.OldPath, video.Path), OldSize = size, Saved = check.Saved, Message = null };
            UpdateReplacement(replacement);
            var result = await TitleSwap.SwapAsync(replacement.OldPath, video, subtitles, mode, recycle, ct);
            if (result.Busy)
                return replacement.Attempts + 1 >= MaxBusy ? Failed(replacement, completion, L("L’ancien fichier est resté ouvert pendant plus d’une heure.", "The old file stayed open for over an hour."))
                    : Later(replacement with { State = ReplacementState.Waiting }, result.Problem ?? "", counted: true);
            if (result.NewPath is not { } placed) return Failed(replacement, completion, result.Problem ?? "", kept: !result.Recycled);
            if (result.Moved) MediaImporter.PruneEmptyFolders([Path.GetDirectoryName(video.Path)], [completion.Directory]);
            return Swapped(replacement, completion, placed, result.Moved, result.Subtitles, video.RelativePath);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Where a swap cut short by Mira's end stands: null when the old file is still there (nothing happened); otherwise the
    /// new file in its place (moved there now when it was still beside it), or why not.
    /// </summary>
    private static (string? Placed, string? Problem)? Recover(TorLinkReplacement replacement)
    {
        if (replacement.NewPath is not { } path) return File.Exists(replacement.OldPath) ? null : (null, Lost);
        var same = string.Equals(Path.GetFullPath(path), Path.GetFullPath(replacement.OldPath), StringComparison.OrdinalIgnoreCase);
        // Under the same name, the old file is the one of the size it had.
        if (File.Exists(replacement.OldPath) && (!same || new FileInfo(path).Length == replacement.OldSize)) return null;
        if (File.Exists(path)) return (path, null);
        var temporary = path + TitleSwap.Suffix;
        if (!File.Exists(temporary)) return (null, Lost);
        try { File.Move(temporary, path, false); JellyfinServiceAccess.ShareWithFolder(path); return (path, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, Lost); }
    }
    private static string Lost => L("Mira s’est fermé pendant le remplacement : l’ancien fichier est dans la Corbeille, la nouvelle version à côté du titre ou dans le dossier de TorLink.",
        "Mira closed during the replacement: the old file is in the Recycle Bin, the new version beside the title or in TorLink’s folder.");

    /// <summary>Tried again in a few minutes: someone watches it (<paramref name="counted"/> false: as long as it takes), or it is open.</summary>
    private TorLinkReplacement Later(TorLinkReplacement replacement, string why, bool counted)
    {
        var later = replacement with { Message = why, Attempts = replacement.Attempts + (counted ? 1 : 0), RetryAt = DateTimeOffset.UtcNow + RetryEvery };
        UpdateReplacement(later);
        return later;
    }

    private TorLinkReplacement Swapped(TorLinkReplacement replacement, TorLinkCompletion completion, string placed, bool moved, IReadOnlyList<string>? subtitles = null, string? relative = null)
    {
        var done = replacement with { State = ReplacementState.Swapped, NewPath = placed, Message = null };
        UpdateReplacement(done);
        Update(new TorLinkImportEntry
        {
            Id = completion.Id, Name = completion.Name, Source = completion.Source, CompletedAt = completion.CompletedAt,
            State = TorLinkImportState.Imported, Title = replacement.Title, Folders = [Path.GetDirectoryName(placed) ?? placed],
            Files = (subtitles ?? []).Select(x => new TorLinkImportedFile(Path.GetFileName(x), x, "subtitle")).Prepend(new TorLinkImportedFile(relative ?? Path.GetFileName(placed), placed, "video")).ToList(),
            MovedOut = moved, Replaced = replacement.OldPath,
            // Mira tells Jellyfin itself, with the old file gone.
            Notified = true, UpdatedAt = DateTimeOffset.UtcNow
        });
        return done;
    }

    /// <param name="kept">The old file is where it was (every check that fails before the swap).</param>
    private TorLinkReplacement Failed(TorLinkReplacement replacement, TorLinkCompletion completion, string reason, bool kept = true)
    {
        var failed = replacement with { State = ReplacementState.Failed, Message = reason };
        UpdateReplacement(failed);
        Update(new TorLinkImportEntry
        {
            Id = completion.Id, Name = completion.Name, Source = completion.Source, CompletedAt = completion.CompletedAt,
            State = TorLinkImportState.Failed, Title = replacement.Title, Replaced = replacement.OldPath, Notified = true, UpdatedAt = DateTimeOffset.UtcNow,
            Message = kept ? string.Join(" ", reason, L("L’ancien fichier reste ; le téléchargement aussi, dans TorLink.", "The old file stays; so does the download, in TorLink.")) : reason
        });
        return failed;
    }

    /// <summary>Moves an imported download to another kind of library (film, series, anime), keeping its files.</summary>
    /// <returns>The entry and the library folders it left, to report to Jellyfin.</returns>
    public async Task<(TorLinkImportEntry? Entry, IReadOnlyList<string> Left)> ReclassifyAsync(string id, MediaKind kind, MediaLibraries libraries, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Find(id) is not { } entry || entry.Files.Count == 0 || entry.Kind == kind || entry.Replaced is not null) return (Find(id), []);
            // Only the links and copies Mira placed move; a file that was already in the library stays where it is.
            var sources = entry.Files.Where(f => !f.Found && File.Exists(f.Destination)).Select(f => new SourceFile(f.Destination, f.RelativePath, new FileInfo(f.Destination).Length)).ToList();
            if (sources.Count == 0)
            {
                var gone = entry with
                {
                    Message = entry.Files.All(f => f.Found) ? L("Ces fichiers étaient déjà dans la bibliothèque : Mira ne les déplace pas.", "These files were already in the library: Mira doesn’t move them.") : L("Les fichiers importés ont été déplacés ou supprimés depuis.", "The imported files have been moved or deleted since."),
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                Update(gone); return (gone, []);
            }
            var known = Entries.Where(x => x.Id != id).ToList();
            var plan = await Task.Run(() => MediaPlanner.Plan(entry.Name, entry.Source, sources, libraries, kind, MediaPlanner.KnownShows(known)), ct);
            if (plan.Problem is not null || plan.Ignored is not null || !Directory.Exists(plan.LibraryRoot))
            {
                // Ignored: the video was already in the library before Mira, so only side files would move.
                var message = plan.Problem ?? (plan.Ignored is not null ? L("La vidéo était déjà dans la bibliothèque : le classement reste tel quel.", "The video was already in the library: its sorting stays as it is.") : L($"Dossier de bibliothèque introuvable : {plan.LibraryRoot}", $"Library folder not found: {plan.LibraryRoot}"));
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
                    Message = L("Classement interrompu : choisis à nouveau « Classer » pour le terminer.", "Sorting stopped: choose “Sort” again to finish it."),
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
    public static List<SourceFile>? DownloadedFiles(TorLinkState state, TorLinkCompletion completion, IReadOnlyList<TorLinkImportedFile>? placed = null) =>
        DownloadedFiles(state, completion, placed, out _);
    /// <summary><paramref name="torrentRefused"/>: the download's .torrent was too large to read; its files are looked for by name.</summary>
    public static List<SourceFile>? DownloadedFiles(TorLinkState state, TorLinkCompletion completion, IReadOnlyList<TorLinkImportedFile>? placed, out bool torrentRefused)
    {
        torrentRefused = false;
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
            var metadata = state.ReadTorrent(completion.Id, out torrentRefused);
            if (metadata is { Files.Count: > 0 } torrent)
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

    /// <summary>Said when the download's .torrent was over <see cref="Bencode"/>'s limits: Mira did not read it.</summary>
    public static string TorrentRefusedHint => L("Son fichier .torrent est trop gros pour que Mira le lise : ses fichiers sont cherchés d’après le nom du téléchargement.",
        "Its .torrent file is too big for Mira to read: its files are looked for by the download’s name.");
    /// <summary>Said when files crossed from another drive: the way to make the next moves instant.</summary>
    public static string OtherDriveHint => L("TorLink télécharge sur un autre disque que la bibliothèque : chaque fichier a été copié puis supprimé. Pour un déplacement instantané, choisis dans TorLink (touche o) un dossier sur le disque de la bibliothèque.",
        "TorLink downloads to another drive than the library: each file was copied, then deleted. For an instant move, choose in TorLink (o key) a folder on the library’s drive.");

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
            TorLinkImportState.Partial => L($"{placed.Count} fichier{(placed.Count > 1 ? "s" : "")} placé{(placed.Count > 1 ? "s" : "")}, {problems} non placé{(problems > 1 ? "s" : "")} : ",
                $"{placed.Count} {(placed.Count == 1 ? "file" : "files")} in place, {problems} not: ") + (result.Conflicts.FirstOrDefault()?.Reason ?? result.Failures.FirstOrDefault()?.Reason),
            TorLinkImportState.Conflict => L("Déjà dans la bibliothèque avec un fichier différent : rien n’a été remplacé.", "Already in the library with a different file: nothing was replaced."),
            _ => result.Failures.FirstOrDefault()?.Reason ?? L("Aucun fichier n’a pu être placé.", "No file could be put in place.")
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
            Method = result.Method ?? entry.Method ?? (result.AlreadyThere.Count > 0 ? L("déjà présent", "already there") : null),
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
                    log.Expected = (log.Expected ?? []).Where(x => x is { Id: not null, Target: { Root: not null, Folder: not null } } && TorLinkState.IsInfoHash(x.Id)).ToList();
                    log.Replacements = (log.Replacements ?? []).Where(x => x is { Id: not null, ItemId: not null, OldPath: not null } && TorLinkState.IsInfoHash(x.Id)).ToList();
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
