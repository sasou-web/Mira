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
/// <summary>A download in TorLink: its info hash, TorLink's status (downloading, queued, paused, failed), percent, bytes per second.</summary>
public sealed record RelayDownload(string Id, string Name, string Status, int Progress, long Speed, int Peers);
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
