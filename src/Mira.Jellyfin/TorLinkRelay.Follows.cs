using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mira.Core.TorLink;

namespace Mira.Jellyfin;

/// <summary>
/// « Suivre une série »: the series whose new episodes the agent fetches by itself. Kept in a file beside the plugins'
/// settings (a restart of Jellyfin keeps them, and checks them all again at the agent's next visit), handed to the agent
/// every <see cref="Every"/> or when asked; the releases it chose wait <see cref="Hold"/> as requests, for « Annuler »,
/// then go to TorLink like any other, with where to place them.
/// </summary>
public sealed partial class TorLinkRelay
{
    /// <summary>How often a series is checked, how long a release chosen waits before it goes (SeriesFollowing, on the agent).</summary>
    public static readonly TimeSpan Every = TimeSpan.FromHours(6), Hold = TimeSpan.FromMinutes(10);
    /// <summary>A check the agent took and never reported within this long is handed out again.</summary>
    public static readonly TimeSpan FollowClaim = TimeSpan.FromMinutes(5);
    /// <summary>A check that failed (TorLink not ready, Jellyfin busy) is tried again after this long.</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromMinutes(30);
    public const int MaxFollows = 40, MaxTaken = 60, MaxRefused = 100, MaxNames = 6, MaxPicks = 6, FollowsPerVisit = 2;
    private static readonly HashSet<string> NoteKinds = ["found", "held", "unknown", "nothing", "missing", "failed"];
    private static readonly HashSet<string> NoteReasons = ["pack", "language", "health", "size"];

    private static readonly JsonSerializerOptions FollowsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly string? _followsFile;
    private List<Followed> _follows = [];

    // ---- Mira web, Mira for Windows or Mac ------------------------------------------------------------------

    /// <summary>The series followed, for the page of a series and the Downloads page (never counts as a look at the screen).</summary>
    public List<RelayFollow> Follows()
    {
        lock (_gate) return FollowList(_now());
    }

    /// <summary>
    /// Follows a series of Jellyfin (its id), under its name there and the name to search instead (<paramref name="query"/>,
    /// empty for none); already followed, its names change. Checked at the agent's next visit either way.
    /// </summary>
    public (string? Id, string? Error) Follow(string? seriesId, string? name, string? query)
    {
        if (seriesId is not { Length: 32 } || !seriesId.All(Uri.IsHexDigit)) return (null, "Série inconnue.");
        var clean = Text(name);
        if (clean is null) return (null, "Série inconnue.");
        lock (_gate)
        {
            var now = _now();
            var id = seriesId.ToLowerInvariant();
            var follow = _follows.FirstOrDefault(x => x.SeriesId == id);
            if (follow is null)
            {
                if (_follows.Count >= MaxFollows) return (null, "Trop de séries suivies : arrête d’en suivre une d’abord.");
                follow = new Followed { Id = NewId(), SeriesId = id, Created = now };
                _follows.Add(follow);
            }
            follow.Name = clean;
            follow.Query = RemoteDownloads.CleanQuery(query);
            follow.Due = true;
            SaveFollows();
            return (follow.Id, null);
        }
    }

    /// <summary>Stops following a series: its releases not yet sent go with it. False when it was not followed.</summary>
    public bool Unfollow(string id)
    {
        lock (_gate)
        {
            if (_follows.FirstOrDefault(x => x.Id == id) is not { } follow) return false;
            _follows.Remove(follow);
            _requests.RemoveAll(x => x.FollowId == follow.Id && x.State is "waiting" or "failed");
            SaveFollows();
            return true;
        }
    }

    /// <summary>« Chercher maintenant »: checked at the agent's next visit. False when it is not followed.</summary>
    public bool CheckNow(string id)
    {
        lock (_gate)
        {
            if (_follows.FirstOrDefault(x => x.Id == id) is not { } follow) return false;
            follow.Due = true;
            return true;
        }
    }

    // ---- The agent ------------------------------------------------------------------------------------

    /// <summary>
    /// What the agent says of the series it checked (their folder, kind, names, latest episode, what it found) and the
    /// releases it chose, held as requests until <see cref="Hold"/> is over. A release TorLink gave up is never taken
    /// again: its episode is looked for anew at the next check.
    /// </summary>
    private void ReportFollows(string device, AgentReport report, DateTimeOffset now)
    {
        var changed = false;
        foreach (var outcome in report.Follows ?? [])
        {
            if (_follows.FirstOrDefault(x => x.Id == outcome.Id && x.Device == device) is not { } follow) continue;
            (follow.Device, follow.Claimed, follow.Due) = (null, null, false);
            var failed = outcome.State != "done";
            // Failed (TorLink not ready, Jellyfin busy): again in a while rather than in six hours.
            follow.Checked = failed ? now - Every + Retry : now;
            follow.Note = CleanNote(outcome.Note) ?? (failed ? new FollowNote("failed") : null);
            if (outcome.Folder is { Length: > 0 and <= 1024 } folder && !folder.Any(char.IsControl)) follow.Folder = folder;
            if (outcome.Category is "anime" or "tv") follow.Category = outcome.Category;
            if (outcome.Names is { } names) follow.Names = names.Select(Text).OfType<string>().Distinct().Take(MaxNames).ToList();
            if (Valid(outcome.Latest) is { } latest && (follow.Latest is null || Later(latest, follow.Latest))) follow.Latest = latest;
            foreach (var pick in (outcome.Picks ?? []).Take(MaxPicks))
            {
                var episode = Valid(new FollowEpisode(pick.Season, pick.Episode));
                if (episode is null || RemoteDownloads.InfoHash(pick.Magnet) is not { } hash || follow.Refused.Contains(hash) || follow.Taken.Any(x => x.Season == episode.Season && x.Episode == episode.Episode)
                    || _requests.Any(x => x.FollowId == follow.Id && x.Episode == episode && x.State is "waiting" or "claimed")) continue;
                _requests.Add(new Ask(NewId(), pick.Magnet.Trim(), Text(pick.Name) ?? RemoteDownloads.DisplayName(pick.Magnet) ?? hash, Math.Max(0, pick.Size), hash, now)
                {
                    FollowId = follow.Id, Episode = episode, NotBefore = now + Hold,
                    Release = new FollowRelease(Text(pick.Group), pick.Languages?.Select(Text).OfType<string>().Take(5).ToList() ?? [], pick.Resolution is > 0 and <= 4320 ? pick.Resolution : null),
                });
            }
            changed = true;
        }
        // Given up by TorLink: that release is refused, and its episode looked for again.
        foreach (var failed in _agent?.Downloads.Where(x => x.Status == "failed") ?? [])
            foreach (var follow in _follows)
                if (follow.Taken.FirstOrDefault(x => x.InfoHash == failed.Id) is { } taken) { follow.Taken.Remove(taken); Refuse(follow, taken.InfoHash); changed = true; }
        if (changed) SaveFollows();
    }

    /// <summary>A release sent to TorLink for a series followed: its episode is taken, and the next one is looked for alike.</summary>
    private void Took(Ask ask, DateTimeOffset now)
    {
        if (FollowOf(ask) is not { } follow || ask.Episode is not { } episode) return;
        follow.Taken.RemoveAll(x => x.Season == episode.Season && x.Episode == episode.Episode);
        follow.Taken.Add(new TakenEpisode(episode.Season, episode.Episode, ask.InfoHash, ask.Name, ask.Release?.Group, ask.Release?.Languages, ask.Release?.Resolution, now));
        if (follow.Taken.Count > MaxTaken) follow.Taken.RemoveRange(0, follow.Taken.Count - MaxTaken);
        SaveFollows();
    }

    /// <summary>The series to check now: never checked, asked for, or not for <see cref="Every"/>; a few at a time.</summary>
    private List<WorkFollow> DueFollows(string device, DateTimeOffset now)
    {
        var due = _follows.Where(x => (x.Device is null || now - x.Claimed >= FollowClaim) && (x.Due || x.Checked is not { } at || now - at >= Every))
            .OrderByDescending(x => x.Due).ThenBy(x => x.Checked ?? DateTimeOffset.MinValue).Take(FollowsPerVisit).ToList();
        foreach (var follow in due) (follow.Device, follow.Claimed) = (device, now);
        return due.Select(x =>
        {
            var last = x.Taken.LastOrDefault();
            return new WorkFollow(x.Id, x.SeriesId, x.Name, x.Query, x.Folder, x.Names.ToList(), x.Latest,
                x.Taken.Select(t => new FollowEpisode(t.Season, t.Episode)).ToList(), x.Refused.ToList(),
                last is null ? null : new FollowRelease(last.Group, last.Languages, last.Resolution));
        }).ToList();
    }

    /// <summary>Where an episode of a series followed goes: its series' folder, as the agent found it.</summary>
    private FollowPlace? PlaceOf(Ask ask) =>
        FollowOf(ask) is { Folder: { } folder } && ask.Episode is { } episode ? new FollowPlace(folder, episode.Season, episode.Episode) : null;

    private Followed? FollowOf(Ask ask) => ask.FollowId is null ? null : _follows.FirstOrDefault(x => x.Id == ask.FollowId);

    private static void Refuse(Followed follow, string hash)
    {
        if (follow.Refused.Contains(hash)) return;
        follow.Refused.Add(hash);
        if (follow.Refused.Count > MaxRefused) follow.Refused.RemoveRange(0, follow.Refused.Count - MaxRefused);
    }

    private List<RelayFollow> FollowList(DateTimeOffset now) => _follows.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).Select(x =>
    {
        var checking = x.Device is not null && now - x.Claimed < FollowClaim;
        var latest = x.Taken.Select(t => new FollowEpisode(t.Season, t.Episode)).Append(x.Latest).OfType<FollowEpisode>().OrderBy(e => e.Season).ThenBy(e => e.Episode).LastOrDefault();
        var picks = _requests.Where(r => r.FollowId == x.Id && r.State is "waiting" or "claimed" or "failed" && r.Episode is not null).OrderBy(r => r.Episode!.Season).ThenBy(r => r.Episode!.Episode)
            .Select(r => new RelayPick(r.Id, Label(r.Episode!), r.Name, r.Size, r.State, r.NotBefore ?? r.Created, r.Error)).ToList();
        return new RelayFollow(x.Id, x.SeriesId, x.Name, x.Query, x.Created, x.Checked, x.Due || x.Checked is null ? null : x.Checked + Every, checking, x.Note,
            latest is null ? null : Label(latest), x.Taken.AsEnumerable().Reverse().Take(10).Select(t => new RelayTaken(Label(new(t.Season, t.Episode)), t.Name, t.At)).ToList(), picks);
    }).ToList();

    private static FollowNote? CleanNote(FollowNote? note) => note is { Kind: { } kind } && NoteKinds.Contains(kind)
        ? new FollowNote(kind, Text(note.Episode), note.Reason is { } reason && NoteReasons.Contains(reason) ? reason : null, Text(note.Detail)) : null;

    private static FollowEpisode? Valid(FollowEpisode? episode) => episode is { Season: >= 1 and <= 100, Episode: >= 1 and <= 5000 } ? episode : null;
    private static bool Later(FollowEpisode a, FollowEpisode b) => a.Season > b.Season || a.Season == b.Season && a.Episode > b.Episode;
    private static string Label(FollowEpisode episode) => string.Create(CultureInfo.InvariantCulture, $"S{episode.Season:00}E{episode.Episode:00}");

    private void LoadFollows()
    {
        if (_followsFile is null || !File.Exists(_followsFile)) return;
        try
        {
            var store = JsonSerializer.Deserialize<FollowStore>(File.ReadAllText(_followsFile), FollowsJson) ?? new FollowStore();
            // Checked again at the agent's next visit: what was claimed or waiting before the restart is gone.
            _follows = (store.Follows ?? []).Where(x => x is { Id.Length: > 0, Name.Length: > 0, SeriesId: { Length: 32 } } && x.SeriesId.All(Uri.IsHexDigit)).Take(MaxFollows).ToList();
            foreach (var follow in _follows)
            {
                follow.Names ??= []; follow.Taken ??= []; follow.Refused ??= [];
                follow.Taken.RemoveAll(x => x is null || !RemoteDownloads.IsInfoHash(x.InfoHash));
                follow.Refused.RemoveAll(x => !RemoteDownloads.IsInfoHash(x));
                follow.Due = true;
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Unreadable: put aside, and the series followed start again empty.
            try { File.Move(_followsFile, _followsFile + ".bad-" + _now().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), true); }
            catch (Exception moving) when (moving is IOException or UnauthorizedAccessException) { }
            _follows = [];
        }
    }

    private void SaveFollows()
    {
        if (_followsFile is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_followsFile)!);
            var next = _followsFile + ".next";
            File.WriteAllText(next, JsonSerializer.Serialize(new FollowStore { Follows = _follows }, FollowsJson));
            File.Move(next, _followsFile, true);
        }
        // Kept in memory until the next change: a restart of Jellyfin would forget it.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private sealed class FollowStore
    {
        public List<Followed> Follows { get; set; } = [];
    }

    /// <summary>A series followed, as kept in the file; who checks it and since when only in memory.</summary>
    private sealed class Followed
    {
        public string Id { get; set; } = "";
        public string SeriesId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Query { get; set; }
        public DateTimeOffset Created { get; set; }
        public DateTimeOffset? Checked { get; set; }
        public FollowNote? Note { get; set; }
        public string? Folder { get; set; }
        public string? Category { get; set; }
        public List<string> Names { get; set; } = [];
        /// <summary>The latest episode the library had: never goes back (watched episodes deleted are not new).</summary>
        public FollowEpisode? Latest { get; set; }
        public List<TakenEpisode> Taken { get; set; } = [];
        public List<string> Refused { get; set; } = [];
        [JsonIgnore] public bool Due { get; set; } = true;
        [JsonIgnore] public string? Device { get; set; }
        [JsonIgnore] public DateTimeOffset? Claimed { get; set; }
    }

    private sealed record TakenEpisode(int Season, int Episode, string InfoHash, string Name, string? Group, List<string>? Languages, int? Resolution, DateTimeOffset At);
}
