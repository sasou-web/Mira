using System.Security.Cryptography;
using Mira.Core.TorLink;

namespace Mira.Jellyfin;

/// <summary>
/// « Télécharger sur le PC serveur »: Mira web of an administrator asks here for a search or a download, and Mira for
/// Windows on the PC of Jellyfin (the agent) comes for them, runs them with its TorLink, and reports. The relay keeps
/// all of it in memory, nothing on disk: a restart of Jellyfin forgets what was still waiting. The agent asks again
/// every few seconds (<see cref="Exchange"/>), never holding a request open: Jellyfin would log each one as slow.
/// Free of ASP.NET (<see cref="TorLinkController"/> serves it), so that the tests check every case.
/// </summary>
public sealed class TorLinkRelay
{
    public static TorLinkRelay Shared { get; } = new(() => DateTimeOffset.UtcNow);

    /// <summary>An agent heard from within this long is « online ».</summary>
    public static readonly TimeSpan Online = TimeSpan.FromSeconds(45);
    /// <summary>Someone looked at the screen within this long: the agent comes back every <see cref="Hurry"/> seconds.</summary>
    public static readonly TimeSpan Watched = TimeSpan.FromMinutes(2);
    /// <summary>Work handed to the agent and not reported within this long is handed out again (Mira closed meanwhile).</summary>
    public static readonly TimeSpan Claim = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SearchLife = TimeSpan.FromMinutes(15);
    /// <summary>A request done or refused stays listed this long, a control this long too.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromMinutes(15);
    public const int Hurry = 2, Idle = 10, Busy = 30;
    public const int MaxWaiting = 30, MaxSearches = 20, MaxResults = 60, MaxDownloads = 60, MaxPlaced = 20, MaxControls = 20;

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly List<Lookup> _searches = [];
    private readonly List<Ask> _requests = [];
    private readonly List<Order> _controls = [];
    private Agent? _agent;
    private DateTimeOffset _watched = DateTimeOffset.MinValue;

    public TorLinkRelay(Func<DateTimeOffset> now) { _now = now; }

    // ---- Mira web ------------------------------------------------------------------------------------

    /// <summary>The screen of Mira web: the agent, the requests, TorLink's downloads and what was placed in the library.</summary>
    public RelayStatus Status()
    {
        lock (_gate)
        {
            var now = _now();
            _watched = now;
            Prune(now);
            var agent = _agent is { } a ? new RelayAgent(a.Name, now - a.Seen < Online, a.Seen, a.Accepting, a.Problem) : null;
            return new RelayStatus(agent,
                _requests.OrderByDescending(x => x.Created).Select(x => new RelayRequest(x.Id, x.Name, x.Size, x.InfoHash, x.State, x.Error, x.Created)).ToList(),
                _controls.Where(x => x.State is "waiting" or "claimed").Select(x => new RelayControl(x.Id, x.Target, x.Action, x.State)).ToList(),
                _agent?.Downloads ?? [], _agent?.Placed ?? []);
        }
    }

    /// <summary>
    /// A search for the agent; refused without an agent online, who alone can run it. In every category
    /// (<see cref="RemoteDownloads.All"/>): one search of TorLink for each, handed to the agent as three (an agent
    /// before 0.8.3 knows only those), their results mixed here.
    /// </summary>
    public (string? Id, string? Error) Search(string? query, string? category)
    {
        var clean = RemoteDownloads.CleanQuery(query);
        if (clean is null) return (null, "Écris ce que tu cherches.");
        if (!RemoteDownloads.IsSearchCategory(category)) return (null, "Choisis Films, Séries ou Animes.");
        lock (_gate)
        {
            var now = _now();
            _watched = now;
            if (_agent is not { } agent || now - agent.Seen >= Online) return (null, "Mira n’est pas ouvert sur le PC serveur : c’est lui qui cherche, avec TorLink.");
            var search = new Lookup(NewId(), clean, category!, now);
            if (category == RemoteDownloads.All) search.Parts.AddRange(RemoteDownloads.Categories.Select(x => new Lookup(NewId(), clean, x, now)));
            _searches.Add(search);
            if (_searches.Count > MaxSearches) _searches.RemoveAt(0);
            return (search.Id, null);
        }
    }

    /// <summary>Where a search is: waiting, running, done (its results, without their magnet links) or failed.</summary>
    public RelaySearch? SearchState(string id)
    {
        lock (_gate)
        {
            _watched = _now();
            if (_searches.FirstOrDefault(x => x.Id == id) is not { } s) return null;
            var (state, hits, error) = Outcome(s);
            return new RelaySearch(s.Id, s.Query, s.Category, state, hits.Select(x => new RelayHit(x.Hit.Id, x.Hit.Name, x.Hit.Size, x.Hit.Seeders, x.Hit.Leechers, x.Hit.Source, x.Hit.Added, x.Category)).ToList(), error);
        }
    }

    /// <summary>
    /// A download for the agent: a result of one of its searches (<paramref name="searchId"/> and <paramref name="hitId"/>,
    /// its magnet kept here), or a pasted magnet link. Waits for the agent when it is not online.
    /// </summary>
    public (string? Id, string? Error) Request(string? magnet, string? searchId, string? hitId, string? name, long? size)
    {
        lock (_gate)
        {
            var now = _now();
            _watched = now;
            if (searchId is not null || hitId is not null)
            {
                if (_searches.FirstOrDefault(x => x.Id == searchId) is not { } search || Units(search).SelectMany(x => x.Results).FirstOrDefault(x => x.Id == hitId) is not { } hit)
                    return (null, "Ce résultat n’est plus là : cherche de nouveau.");
                (magnet, name, size) = (hit.Magnet, hit.Name, hit.Size);
            }
            if (RemoteDownloads.InfoHash(magnet) is not { } hash) return (null, "Ce n’est pas un lien magnet (« magnet:?xt=urn:btih:… »).");
            if (_requests.FirstOrDefault(x => x.InfoHash == hash && x.State is "waiting" or "claimed") is { } same) return (same.Id, null);
            if (_requests.Count(x => x.State is "waiting" or "claimed") >= MaxWaiting) return (null, "Trop de demandes attendent déjà le PC serveur.");
            var ask = new Ask(NewId(), magnet!.Trim(), RemoteDownloads.CleanName(name) ?? RemoteDownloads.DisplayName(magnet) ?? hash, Math.Max(0, size ?? 0), hash, now);
            _requests.Add(ask);
            return (ask.Id, null);
        }
    }

    /// <summary>True: removed before the agent took it; false: it has it already; null: unknown.</summary>
    public bool? Cancel(string id)
    {
        lock (_gate)
        {
            if (_requests.FirstOrDefault(x => x.Id == id) is not { } ask) return null;
            if (ask.State is not ("waiting" or "failed" or "sent")) return false;
            _requests.Remove(ask);
            return true;
        }
    }

    /// <summary>Cancel (delete), pause or resume a download TorLink has: done by the agent, through TorLink.</summary>
    public (string? Id, string? Error) Control(string? target, string? action)
    {
        if (!RemoteDownloads.IsInfoHash(target) || !RemoteDownloads.IsAction(action)) return (null, "Demande inconnue.");
        lock (_gate)
        {
            var now = _now();
            _watched = now;
            if (_controls.FirstOrDefault(x => x.Target == target && x.Action == action && x.State is "waiting" or "claimed") is { } same) return (same.Id, null);
            if (_controls.Count(x => x.State is "waiting" or "claimed") >= MaxControls) return (null, "Trop de demandes attendent déjà le PC serveur.");
            var order = new Order(NewId(), target!, action!, now);
            _controls.Add(order);
            return (order.Id, null);
        }
    }

    // ---- Mira for Windows --------------------------------------------------------------------------------

    /// <summary>
    /// The agent's visit: what it reports (its state, then the outcome of what it took) is kept, and it leaves with new
    /// work, at most a few of each, and when to come back. Downloads and controls are handed out only while it accepts
    /// them (its TorLink can take them now). Another Mira online already keeps the role: this one gets nothing.
    /// </summary>
    public AgentWork Exchange(string device, AgentReport report)
    {
        lock (_gate)
        {
            var now = _now();
            if (_agent is { } current && current.Device != device && now - current.Seen < Online) return new([], [], [], Busy, current.Name);
            _agent = new Agent(device, Text(report.Name) ?? "PC serveur", now, report.Accepting, Text(report.Problem),
                (report.Downloads ?? []).Where(x => RemoteDownloads.IsInfoHash(x.Id)).Take(MaxDownloads)
                    .Select(x => x with { Name = Text(x.Name) ?? x.Id, Status = Text(x.Status) ?? "", Progress = Math.Clamp(x.Progress, 0, 100), Speed = Math.Max(0, x.Speed), Peers = Math.Max(0, x.Peers), Size = Math.Max(0, x.Size), Downloaded = Math.Clamp(x.Downloaded, 0, x.Size > 0 ? x.Size : long.MaxValue), Eta = x.Eta is > 0 and <= RemoteDownloads.MaxEta ? x.Eta : null }).ToList(),
                (report.Placed ?? []).Where(x => RemoteDownloads.IsInfoHash(x.Id)).Take(MaxPlaced)
                    .Select(x => x with { Title = Text(x.Title) ?? x.Id, Status = Text(x.Status) ?? "", ItemId = x.ItemId is { Length: 32 } item && item.All(Uri.IsHexDigit) ? item : null }).ToList());

            foreach (var outcome in report.Searches ?? [])
                if (_searches.SelectMany(Units).FirstOrDefault(x => x.Id == outcome.Id && x.Device == device && x.State == "running") is { } search)
                {
                    search.State = outcome.State == "done" ? "done" : "failed";
                    search.Error = search.State == "failed" ? Text(outcome.Error) ?? "La recherche n’a pas abouti." : null;
                    search.Results = search.State == "done" ? (outcome.Results ?? []).Where(x => RemoteDownloads.InfoHash(x.Magnet) == x.Id).DistinctBy(x => x.Id).Take(MaxResults)
                        .Select(x => x with { Name = Text(x.Name) ?? x.Id, Source = Text(x.Source) ?? "", Size = Math.Max(0, x.Size), Seeders = Math.Max(0, x.Seeders), Leechers = Math.Max(0, x.Leechers) }).ToList() : [];
                }
            foreach (var outcome in report.Requests ?? [])
                if (_requests.FirstOrDefault(x => x.Id == outcome.Id && x.Device == device && x.State == "claimed") is { } ask)
                    (ask.State, ask.Error, ask.Done) = outcome.State == "sent" ? ("sent", null, now) : ("failed", Text(outcome.Error) ?? "TorLink a refusé ce téléchargement.", now);
            foreach (var outcome in report.Controls ?? [])
                if (_controls.FirstOrDefault(x => x.Id == outcome.Id && x.Device == device && x.State == "claimed") is { } order)
                    (order.State, order.Done) = (outcome.State == "done" ? "done" : "failed", now);
            // Sent and now in TorLink's list (or placed): Mira web shows it there, and no longer once it is cancelled.
            var listed = _agent.Downloads.Select(x => x.Id).Concat(_agent.Placed.Select(x => x.Id)).ToHashSet();
            _requests.RemoveAll(x => x.State == "sent" && listed.Contains(x.InfoHash));
            Prune(now);

            bool Free(string state, string? owner, DateTimeOffset? claimed) => state == "waiting" || state is "claimed" or "running" && (owner != device || now - claimed >= Claim);
            // Three: a search in every category goes in one visit.
            var searches = _searches.SelectMany(Units).Where(x => Free(x.State, x.Device, x.Claimed)).Take(3).ToList();
            var requests = report.Accepting ? _requests.Where(x => Free(x.State, x.Device, x.Claimed)).Take(5).ToList() : [];
            var controls = report.Accepting ? _controls.Where(x => Free(x.State, x.Device, x.Claimed)).Take(10).ToList() : [];
            foreach (var s in searches) (s.State, s.Device, s.Claimed) = ("running", device, now);
            foreach (var r in requests) (r.State, r.Device, r.Claimed) = ("claimed", device, now);
            foreach (var c in controls) (c.State, c.Device, c.Claimed) = ("claimed", device, now);
            var hurry = now - _watched < Watched || _searches.SelectMany(Units).Any(x => x.State == "waiting") || report.Accepting && (_requests.Any(x => x.State == "waiting") || _controls.Any(x => x.State == "waiting"));
            return new(searches.Select(x => new WorkSearch(x.Id, x.Query, x.Category)).ToList(),
                requests.Select(x => new WorkRequest(x.Id, x.Magnet, x.Name)).ToList(),
                controls.Select(x => new WorkControl(x.Id, x.Target, x.Action)).ToList(), hurry ? Hurry : Idle, null);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        _searches.RemoveAll(x => now - x.Created > SearchLife);
        _requests.RemoveAll(x => x.Done is { } done && now - done > Kept);
        _controls.RemoveAll(x => x.Done is { } done && now - done > Kept);
    }

    /// <summary>What the agent runs of a search: itself, or its search in each category.</summary>
    private static IEnumerable<Lookup> Units(Lookup search) => search.Parts.Count > 0 ? search.Parts : [search];

    /// <summary>
    /// Where a search is for Mira web: one in every category is done once each of its searches is (with the results of
    /// those that went through, mixed by <see cref="RemoteDownloads.Combine{T}"/>), failed when all of them failed.
    /// </summary>
    private static (string State, List<(string Category, AgentHit Hit)> Hits, string? Error) Outcome(Lookup search)
    {
        var units = Units(search).ToList();
        if (units.Any(x => x.State is "waiting" or "running")) return (units.Any(x => x.State == "running") ? "running" : "waiting", [], null);
        var (hits, error) = RemoteDownloads.Combine<AgentHit>(units.Select(x => (x.Category, (IReadOnlyList<AgentHit>)x.Results, x.State == "failed" ? x.Error ?? "La recherche n’a pas abouti." : null)).ToList(), x => x.Id);
        return error is null ? ("done", hits, null) : ("failed", [], error);
    }

    private static string? Text(string? value) => RemoteDownloads.CleanName(value);
    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    private sealed record Agent(string Device, string Name, DateTimeOffset Seen, bool Accepting, string? Problem, List<RelayDownload> Downloads, List<RelayPlaced> Placed);
    private sealed class Lookup(string id, string query, string category, DateTimeOffset created)
    {
        public string Id { get; } = id; public string Query { get; } = query; public string Category { get; } = category; public DateTimeOffset Created { get; } = created;
        public string State { get; set; } = "waiting"; public string? Device { get; set; }
        public DateTimeOffset? Claimed { get; set; }
        public List<AgentHit> Results { get; set; } = []; public string? Error { get; set; }
        /// <summary>A search in every category: its search in each, which the agent runs; empty otherwise.</summary>
        public List<Lookup> Parts { get; } = [];
    }
    private sealed class Ask(string id, string magnet, string name, long size, string infoHash, DateTimeOffset created)
    {
        public string Id { get; } = id; public string Magnet { get; } = magnet; public string Name { get; } = name; public long Size { get; } = size;
        public string InfoHash { get; } = infoHash; public DateTimeOffset Created { get; } = created;
        public string State { get; set; } = "waiting"; public string? Error { get; set; }
        public string? Device { get; set; }
        public DateTimeOffset? Claimed { get; set; }
        public DateTimeOffset? Done { get; set; }
    }
    private sealed class Order(string id, string target, string action, DateTimeOffset created)
    {
        public string Id { get; } = id; public string Target { get; } = target; public string Action { get; } = action; public DateTimeOffset Created { get; } = created;
        public string State { get; set; } = "waiting"; public string? Device { get; set; }
        public DateTimeOffset? Claimed { get; set; }
        public DateTimeOffset? Done { get; set; }
    }
}
