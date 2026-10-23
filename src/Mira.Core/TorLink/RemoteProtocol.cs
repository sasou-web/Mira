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
/// the work it took.
/// </summary>
public sealed record AgentReport(string? Name, bool Accepting, string? Problem, List<RelayDownload>? Downloads, List<RelayPlaced>? Placed,
    List<AgentSearchOutcome>? Searches, List<AgentOutcome>? Requests, List<AgentOutcome>? Controls);
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

/// <summary>What the agent leaves with, and in how many seconds it comes back. <paramref name="Busy"/>: another Mira has the role.</summary>
public sealed record AgentWork(List<WorkSearch> Searches, List<WorkRequest> Requests, List<WorkControl> Controls, int Next, string? Busy);
public sealed record WorkSearch(string Id, string Query, string Category);
public sealed record WorkRequest(string Id, string Magnet, string Name);
public sealed record WorkControl(string Id, string Target, string Action);

// What Mira web (and Mira for Windows or Mac away from the PC of Jellyfin) reads (GET /Mira/torlink, GET /Mira/torlink/search/{id}).
public sealed record RelayStatus(RelayAgent? Agent, List<RelayRequest> Requests, List<RelayControl> Controls, List<RelayDownload> Downloads, List<RelayPlaced> Placed);
public sealed record RelayAgent(string Name, bool Online, DateTimeOffset Seen, bool Accepting, string? Problem);
public sealed record RelayRequest(string Id, string Name, long Size, string InfoHash, string State, string? Error, DateTimeOffset Created);
public sealed record RelayControl(string Id, string Target, string Action, string State);
public sealed record RelaySearch(string Id, string Query, string Category, string State, List<RelayHit> Results, string? Error);
/// <summary><paramref name="Category"/>: the search of TorLink that found it (movies, tv, anime); an extension before 0.8.3 leaves it out.</summary>
public sealed record RelayHit(string Id, string Name, long Size, int Seeders, int Leechers, string Source, long? Added, string? Category = null);
