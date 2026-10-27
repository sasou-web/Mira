using System.Text.Json;

namespace Mira.Core.TorLink;

// « Télécharger sur le PC serveur »: what Mira for Windows on the PC of Jellyfin and the extension's relay
// (Mira.Jellyfin/TorLinkRelay, which compiles this file) exchange at each visit, POST /Mira/torlink/agent.

/// <summary>The JSON of these exchanges: camelCase, read whatever the case.</summary>
public static class RemoteProtocol
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// The agent's report: its name, whether its TorLink can take downloads now (<paramref name="Accepting"/>), or why not
/// (<paramref name="Problem"/>), TorLink's downloads and the latest titles placed in the library, and the outcome of
/// the work it took. <paramref name="Follows"/>: the outcome of the series it checked (« Suivre une série », after 0.8.4);
/// an older agent leaves it out, and the relay hands it no series.
/// </summary>
public sealed record AgentReport(string? Name, bool Accepting, string? Problem, List<RelayDownload>? Downloads, List<RelayPlaced>? Placed,
    List<AgentSearchOutcome>? Searches, List<AgentOutcome>? Requests, List<AgentOutcome>? Controls, List<AgentFollowOutcome>? Follows = null);
/// <summary>
/// A download in TorLink: its info hash, TorLink's status (downloading, queued, paused, failed), percent, bytes per second,
/// its size and what it has of it (0 when unknown), and the seconds TorLink still expects (null when it cannot tell).
/// The last three came after 0.7.9: an older agent leaves them out. <paramref name="Error"/>: why TorLink gave up a failed one,
/// in its words (after 0.8.2).
/// </summary>
public sealed record RelayDownload(string Id, string Name, string Status, int Progress, long Speed, int Peers, long Size = 0, long Downloaded = 0, long? Eta = null, string? Error = null);
/// <summary>A finished download as Mira placed it, in Mira's words (TorLinkPlacement.StatusOf); its title in Jellyfin once indexed.</summary>
public sealed record RelayPlaced(string Id, string Title, string Status, string? ItemId, bool Ok);
/// <summary><paramref name="State"/>: done (its results) or failed (why).</summary>
public sealed record AgentSearchOutcome(string Id, string State, List<AgentHit>? Results, string? Error);
public sealed record AgentHit(string Id, string Name, long Size, int Seeders, int Leechers, string Source, string Magnet, long? Added);
/// <summary><paramref name="State"/>: sent (in TorLink now, new or already there) or done for a control; failed otherwise, with why.</summary>
public sealed record AgentOutcome(string Id, string State, string? Error);

/// <summary>
/// What the agent leaves with, and in how many seconds it comes back. <paramref name="Busy"/>: another Mira has the role.
/// <paramref name="Follows"/>: the series to check now (« Suivre une série »).
/// </summary>
public sealed record AgentWork(List<WorkSearch> Searches, List<WorkRequest> Requests, List<WorkControl> Controls, int Next, string? Busy, List<WorkFollow>? Follows = null);
public sealed record WorkSearch(string Id, string Query, string Category);
/// <summary><paramref name="Place"/>: an episode of a followed series, placed in its folder whatever its release calls it.</summary>
public sealed record WorkRequest(string Id, string Magnet, string Name, FollowPlace? Place = null);
public sealed record WorkControl(string Id, string Target, string Action);

// « Suivre une série »: the series whose new episodes the agent fetches by itself, kept by the relay on the server
// (TorLinkRelay.Follows), checked by the agent (SeriesFollowing chooses the releases).

/// <summary>An episode as Jellyfin numbers it: its season (1 and up) and its number in that season.</summary>
public sealed record FollowEpisode(int Season, int Episode);
/// <summary>What a release taken was (its group, languages, picture): the next episode is looked for the same.</summary>
public sealed record FollowRelease(string? Group, List<string>? Languages, int? Resolution);
/// <summary>
/// A series for the agent to check: what Jellyfin calls it, the name to search instead (<paramref name="Query"/>), its
/// folder and other names learned before, the latest episode known, those taken (whatever the library has since) and the
/// releases refused (cancelled, or failed in TorLink), and what the previous one taken was.
/// </summary>
public sealed record WorkFollow(string Id, string SeriesId, string Name, string? Query, string? Folder, List<string>? Names,
    FollowEpisode? Latest, List<FollowEpisode>? Taken, List<string>? Refused, FollowRelease? Prefer);
/// <summary>
/// A check: <paramref name="State"/> done or failed, what it found to say, the series' folder and kind (anime or tv), the
/// other names its releases gave, the latest episode of the library, and the releases chosen.
/// </summary>
public sealed record AgentFollowOutcome(string Id, string State, FollowNote? Note, string? Folder, string? Category, List<string>? Names, FollowEpisode? Latest, List<FollowPick>? Picks);
/// <summary>
/// What a check found, for each screen to say in its language (RemoteLines.Note, torlink.js: noteText): <paramref name="Kind"/>
/// nothing (nothing after <paramref name="Episode"/>), found, held (<paramref name="Episode"/> found but not taken, for
/// <paramref name="Reason"/>: pack, language, health, size), unknown (no release under these names), missing (the series is
/// gone from Jellyfin) or failed (<paramref name="Detail"/>: why, in the agent's words).
/// </summary>
public sealed record FollowNote(string Kind, string? Episode = null, string? Reason = null, string? Detail = null);
/// <summary>A release the agent chose for an episode: held a while by the relay, for « Annuler », then downloaded.</summary>
public sealed record FollowPick(int Season, int Episode, string Name, string Magnet, long Size, int Seeders, string Source, string? Group, List<string>? Languages, int? Resolution);
/// <summary>Where a followed episode goes: the series' folder, as Jellyfin has it, and the episode's season and number.</summary>
public sealed record FollowPlace(string Folder, int Season, int Episode);

// What Mira web (and Mira for Windows or Mac away from the PC of Jellyfin) reads (GET /Mira/torlink, GET /Mira/torlink/search/{id}).
/// <summary><paramref name="Follows"/>: the series followed; an extension of 0.8.4 or before leaves it out.</summary>
public sealed record RelayStatus(RelayAgent? Agent, List<RelayRequest> Requests, List<RelayControl> Controls, List<RelayDownload> Downloads, List<RelayPlaced> Placed, List<RelayFollow>? Follows = null);
/// <summary><paramref name="Follows"/>: its Mira checks the series followed (an agent of 0.8.4 or before does not).</summary>
public sealed record RelayAgent(string Name, bool Online, DateTimeOffset Seen, bool Accepting, string? Problem, bool Follows = false);
public sealed record RelayRequest(string Id, string Name, long Size, string InfoHash, string State, string? Error, DateTimeOffset Created);
public sealed record RelayControl(string Id, string Target, string Action, string State);
public sealed record RelaySearch(string Id, string Query, string Category, string State, List<RelayHit> Results, string? Error);
/// <summary><paramref name="Category"/>: the search of TorLink that found it (movies, tv, anime); an extension before 0.8.3 leaves it out.</summary>
public sealed record RelayHit(string Id, string Name, long Size, int Seeders, int Leechers, string Source, long? Added, string? Category = null);
/// <summary>
/// A series followed: its name and the name searched instead, when it was last checked and when it is next
/// (<paramref name="Next"/> null: at the agent's next visit), <paramref name="Checking"/> while the agent searches, what the
/// last check found, the latest episode known (« S01E12 »), the latest taken and the releases chosen not yet sent.
/// </summary>
public sealed record RelayFollow(string Id, string SeriesId, string Name, string? Query, DateTimeOffset Created, DateTimeOffset? Checked, DateTimeOffset? Next, bool Checking,
    FollowNote? Note, string? Latest, List<RelayTaken> Taken, List<RelayPick> Picks);
public sealed record RelayTaken(string Episode, string Name, DateTimeOffset At);
/// <summary>
/// A release chosen for an episode (« S01E13 »): <paramref name="State"/> waiting (it goes at <paramref name="SendAt"/>,
/// « Annuler » until then), claimed (on its way to TorLink) or failed (<paramref name="Error"/>: why).
/// </summary>
public sealed record RelayPick(string Id, string Episode, string Name, long Size, string State, DateTimeOffset SendAt, string? Error);
