namespace Mira.Core.TorLink;

/// <summary>
/// One line of « Sur le PC serveur » on the Downloads page (« Régie », 2026-11-14): a request the relay holds, an episode a
/// series followed chose (waiting its 10 min), or a download TorLink has. <paramref name="Title"/> and <paramref name="Year"/>
/// / <paramref name="Episode"/> as read from the release's name, its picture in a few words (<paramref name="Quality"/>),
/// <paramref name="Followed"/> when it is an episode of a series followed, <paramref name="PosterId"/> the title of Jellyfin whose
/// poster it shows (a series followed or asked from its page, the title a better version replaces), never a picture from elsewhere.
/// </summary>
public sealed record BoardLine(string Key, string Title, int? Year, string Episode, string Quality, string? Full, bool Followed, string? PosterId, bool Episodic,
    RelayDownload? Download = null, RelayRequest? Request = null, RelayPick? Pick = null);

/// <summary>
/// A series followed as its row of the table shows it: what is under way for it (or what its last check found), its next
/// episode and when it airs, and when the next search is.
/// </summary>
public sealed record BoardFollow(RelayFollow Follow, string Line, bool Live, bool Warning, string NextEpisode, string Airs, string NextSearch);

/// <summary>
/// The Downloads page of Mira for Windows and Mac read from what the relay (or this PC's TorLink) says: the queue in one list,
/// the series followed in a table, the titles placed; the words of each in <see cref="RemoteLines"/>.
/// </summary>
public static class RemoteBoard
{
    /// <summary>
    /// The queue: requests not yet in TorLink, then the episodes of the series followed waiting their turn, then TorLink's
    /// downloads; each with what Jellyfin knows of it, and « Followed » when a series followed took it (same release name as
    /// one of its picks or of the episodes it took).
    /// </summary>
    public static List<BoardLine> Queue(RelayStatus status)
    {
        var follows = status.Follows ?? [];
        var byName = new Dictionary<string, RelayFollow>(StringComparer.OrdinalIgnoreCase);
        foreach (var follow in follows)
            foreach (var name in follow.Picks.Select(x => x.Name).Concat(follow.Taken.Select(x => x.Name)))
                byName.TryAdd(name.Trim(), follow);
        var missing = (status.Missing ?? []).Where(x => x.InfoHash.Length > 0).GroupBy(x => x.InfoHash, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var lines = new List<BoardLine>();
        foreach (var request in RemoteLines.Pending(status))
            lines.Add(Line($"r:{request.Id}", request.Name, byName.GetValueOrDefault(request.Name.Trim()), missing.GetValueOrDefault(request.InfoHash)?.SeriesId ?? request.Replace) with { Request = request });
        foreach (var follow in follows)
            foreach (var pick in follow.Picks)
                lines.Add(Line($"p:{pick.Id}", pick.Name, follow, null) with { Pick = pick });
        foreach (var download in status.Downloads)
            lines.Add(Line($"d:{download.Id}", download.Name, byName.GetValueOrDefault(download.Name.Trim()), missing.GetValueOrDefault(download.Id)?.SeriesId) with { Download = download });
        return lines;
    }

    private static BoardLine Line(string key, string name, RelayFollow? follow, string? poster)
    {
        var release = ReleaseRanking.Parse(name);
        var episode = ReleaseRanking.EpisodeLabel(release);
        var (title, full) = release.Title.Length > 0 ? (release.Title, name.Trim()) : (name.Trim(), null);
        return new(key, title, episode.Length == 0 ? release.Year : null, episode, ReleaseRanking.QualityLine(release), full, follow is not null, follow?.SeriesId ?? poster,
            episode.Length > 0 || follow is not null);
    }

    /// <summary>
    /// « 2 downloading · 14.2 MB/s · 1 paused · 1 waiting »: what the queue does, its speed all together; empty when nothing is
    /// under way.
    /// </summary>
    public static string Summary(IReadOnlyList<BoardLine> queue)
    {
        var downloads = queue.Select(x => x.Download).OfType<RelayDownload>().ToList();
        var downloading = downloads.Count(x => x.Status == "downloading");
        var paused = downloads.Count(x => x.Status is "paused" or "stopped");
        var failed = downloads.Count(x => x.Status == "failed") + queue.Count(x => x.Request?.State == "failed" || x.Pick?.State == "failed");
        var waiting = downloads.Count(x => x.Status == "queued") + queue.Count(x => x.Request is { State: not "failed" } || x.Pick is { State: not "failed" });
        var speed = downloading > 0 ? RemoteLines.Speed(downloads.Where(x => x.Status == "downloading").Sum(x => Math.Max(0, x.Speed))) : "";
        return string.Join(" · ", new[]
        {
            downloading > 0 ? L($"{downloading} en cours", $"{downloading} downloading") : "", speed,
            paused > 0 ? L($"{paused} en pause", $"{paused} paused") : "",
            waiting > 0 ? L($"{waiting} en attente", $"{waiting} waiting") : "",
            failed > 0 ? L($"{failed} en échec", $"{failed} failed") : "",
        }.Where(x => x.Length > 0));
    }

    /// <summary>« 62 % », « 18% »: the share TorLink has of a download.</summary>
    public static string Percent(RelayDownload download) => L($"{Math.Clamp(download.Progress, 0, 100)} %", $"{Math.Clamp(download.Progress, 0, 100)}%");

    /// <summary>
    /// The columns of a download in the wide queue: what it has of its size, its speed (or its state when nothing comes:
    /// « Paused », « Queued », « Looking for peers… »), the time left and its peers; empty where there is nothing to say.
    /// </summary>
    public static (string Amount, string Rate, string Left, string Peers) Columns(RelayDownload download)
    {
        var peers = Math.Max(0, download.Peers);
        var amount = RemoteLines.Amount(download);
        switch (download.Status)
        {
            case "downloading":
                var rate = RemoteLines.Speed(download.Speed);
                if (rate.Length == 0) rate = peers == 0 ? L("Recherche de pairs…", "Looking for peers…") : L("En attente de données", "Waiting for data");
                var left = RemoteLines.TimeLeft(download) is { } seconds ? L($"reste {RemoteLines.Remaining(seconds)}", $"{RemoteLines.Remaining(seconds)} left") : "";
                return (amount, rate, left, peers switch { 0 => "", 1 => L("1 pair", "1 peer"), _ => L($"{peers} pairs", $"{peers} peers") });
            case "queued": return (amount, L("En file d’attente", "Queued"), "", "");
            case "paused": return (amount, L("En pause", "Paused"), "", "");
            case "stopped": return (amount, L("Arrêté", "Stopped"), "", "");
            default: return (amount, "", "", "");
        }
    }

    /// <summary>
    /// What a line says when it has no columns: a request (« Waiting for the server PC »), an episode chosen (« Waiting · 1.4 GB ·
    /// goes in 8 min · found for a series you follow »), a download done or failed; null for a download under way, which has
    /// its columns.
    /// </summary>
    public static string? Waiting(BoardLine line, DateTimeOffset now)
    {
        if (line.Request is { } request) return RemoteLines.Request(request);
        if (line.Pick is { } pick)
            return pick.State switch
            {
                "waiting" => string.Join(" · ", new[] { L("En attente", "Waiting"), RemoteLines.Size(pick.Size), pick.SendAt > now ? L($"part dans {In(pick.SendAt - now)}", $"goes in {In(pick.SendAt - now)}") : L("part maintenant", "going now"), L("trouvé pour une série que tu suis", "found for a series you follow") }),
                "claimed" => L("Envoi à TorLink…", "Sending to TorLink…"),
                _ => string.IsNullOrEmpty(pick.Error) ? L("TorLink a refusé ce téléchargement.", "TorLink refused this download.") : pick.Error
            };
        if (line.Download is { Status: "failed" or "completed" or "seeding" } download) return RemoteLines.Download(download);
        return null;
    }

    /// <summary>The series followed, each with what its row says (<see cref="BoardFollow"/>).</summary>
    public static List<BoardFollow> Follows(RelayStatus status, DateTimeOffset now)
    {
        var rows = new List<BoardFollow>();
        foreach (var follow in status.Follows ?? [])
        {
            // Under way for it: an episode chosen waiting its turn, else the latest it took while TorLink has it.
            string? live = null;
            if (follow.Picks.FirstOrDefault(x => x.State != "failed") is { } pick) live = RemoteLines.Pick(pick, now);
            else if (follow.Taken.FirstOrDefault() is { } taken && status.Downloads.FirstOrDefault(x => string.Equals(x.Name.Trim(), taken.Name.Trim(), StringComparison.OrdinalIgnoreCase)) is { Status: "downloading" or "queued" or "paused" or "stopped" } download)
                live = $"{taken.Episode} · " + (download.Status == "downloading" ? L($"téléchargement, {Math.Clamp(download.Progress, 0, 100)} %", $"downloading, {Math.Clamp(download.Progress, 0, 100)}%")
                    : download.Status == "queued" ? L("en file d’attente", "queued") : L($"en pause, {Math.Clamp(download.Progress, 0, 100)} %", $"paused, {Math.Clamp(download.Progress, 0, 100)}%"));
            var line = live;
            if (line is null)
            {
                line = follow.Checking ? L("Recherche en cours sur le PC serveur…", "Searching on the server PC…") : RemoteLines.FollowNote(follow.Note);
                if (!follow.Checking && follow.Note?.Kind is "nothing" or null && follow.Taken.FirstOrDefault() is { } last)
                    line = string.Join(" ", new[] { line, L($"Pris {Ago(now - last.At)}.", $"Taken {Ago(now - last.At)}.") }.Where(x => x.Length > 0));
                if (line.Length == 0) line = follow.Latest is { } latest ? L($"Dernier épisode : {latest}", $"Latest episode: {latest}") : L("Pas encore cherchée.", "Not searched yet.");
            }
            var (episode, airs) = follow.Airs is { } coming ? (coming.Episode, AirsDay(coming, now)) : ("", L("pas encore de date", "no date yet"));
            var next = follow.Checking ? L("en cours", "now") : follow.Next is { } at && at > now ? L($"dans {In(at - now)}", $"in {In(at - now)}") : L("bientôt", "soon");
            rows.Add(new(follow, line, live is not null, live is null && !follow.Checking && follow.Note?.Kind is "failed" or "missing" or "unknown", episode, airs, next));
        }
        return rows;
    }

    /// <summary>« airs tomorrow », « aired yesterday », « airs Friday ».</summary>
    private static string AirsDay(RelayAiring airs, DateTimeOffset now)
    {
        var (day, today) = (DateOnly.FromDateTime(airs.Day.UtcDateTime), EpisodeCalendar.Today(now.LocalDateTime));
        var when = EpisodeCalendar.DayText(day, today);
        return day < today ? L($"sorti {when}", $"aired {when}") : L($"sort {when}", $"airs {when}");
    }

    /// <summary>« Arcane S02E06 airs tomorrow »: the first episode coming among the series followed, for its folded line; empty without one.</summary>
    public static string Coming(IReadOnlyList<RelayFollow>? follows, DateTimeOffset now)
    {
        var today = EpisodeCalendar.Today(now.LocalDateTime);
        var next = (follows ?? []).Where(x => x.Airs is { } airs && DateOnly.FromDateTime(airs.Day.UtcDateTime) >= today).OrderBy(x => x.Airs!.Day).FirstOrDefault();
        return next is null ? "" : $"{next.Name} {next.Airs!.Episode} {AirsDay(next.Airs, now)}";
    }

    /// <summary>« Flow, Nimona, Arcane S02E05… »: the latest titles placed, for their folded line.</summary>
    public static string Latest(IReadOnlyList<RelayPlaced> placed, int count = 3) =>
        placed.Count == 0 ? "" : string.Join(", ", placed.Take(count).Select(x => x.Title)) + (placed.Count > count ? "…" : "");

    /// <summary>« 4.9 TB free », « 850 GB free »: what is left on the disks of the libraries of the PC of Jellyfin.</summary>
    public static string Free(long bytes)
    {
        const double Tib = 1L << 40;
        if (bytes >= 1L << 40)
        {
            var size = (bytes / Tib).ToString(bytes >= 10L << 40 ? "0" : "0.0", Culture);
            return L($"{size} To libres", $"{size} TB free");
        }
        return L($"{RemoteLines.Size(bytes)} libres", $"{RemoteLines.Size(bytes)} free");
    }

    /// <summary>
    /// What is left on the disks holding these folders (the libraries of Jellyfin on this PC): each disk once, those that
    /// answer only (<paramref name="free"/> null: unknown). Null when none does.
    /// </summary>
    public static long? FreeSpace(IEnumerable<string?> folders, Func<string, string?> root, Func<string, long?> free)
    {
        long? total = null;
        foreach (var disk in folders.OfType<string>().Where(x => x.Length > 0).Select(root).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            if (free(disk) is { } bytes and >= 0) total = (total ?? 0) + bytes;
        return total;
    }

    private static string In(TimeSpan span)
    {
        var seconds = span.TotalSeconds;
        return seconds < 60 ? L("moins d’une minute", "less than a minute") : seconds < 3570 ? $"{Round(seconds / 60)} min" : seconds < 129600 ? L($"{Round(seconds / 3600)} h", $"{Round(seconds / 3600)} hr") : Count(Round(seconds / 86400), "jour", "jours", "day", "days");
    }
    private static string Ago(TimeSpan span)
    {
        var seconds = Math.Max(0, span.TotalSeconds);
        return seconds < 90 ? L("à l’instant", "just now") : seconds < 5400 ? L($"il y a {Round(seconds / 60)} min", $"{Round(seconds / 60)} min ago")
            : seconds < 129600 ? L($"il y a {Round(seconds / 3600)} h", $"{Round(seconds / 3600)} hr ago") : L($"il y a {Round(seconds / 86400)} jours", $"{Round(seconds / 86400)} days ago");
    }
    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
