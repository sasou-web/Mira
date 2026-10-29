using System.Net.Http;

namespace Mira.Core.Tailscale;

/// <summary>
/// Mira's own Tailscale node (src/Mira.Tailscale) could not do what was asked, for the reason <see cref="Code"/> names
/// (one of <see cref="TailnetCodes"/>). A network failure like any other for the apps (<see cref="ExpectedErrors.Are"/>):
/// the code only says which words to show and what the person can do about it.
/// </summary>
public sealed class TailnetException(string code, string? message = null, Exception? inner = null)
    : HttpRequestException(HttpRequestError.ConnectionError, message ?? code, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// Why the node failed, as the Go code names it (tailnet/dial.go, tailnet/relay.go: answers of the helper, header
/// X-Mira-Tailscale of its proxy and relay), plus what only the apps see of the helper process. A check of
/// Mira.Core.Tests reads the Go files and wants each of their codes here.
/// </summary>
public static class TailnetCodes
{
    /// <summary>The node was never signed in to a tailnet, or was signed out.</summary>
    public const string NeedsLogin = "needs-login";
    /// <summary>The node's key expired (180 days by default): signing in again keeps its name and addresses.</summary>
    public const string Expired = "expired";
    /// <summary>The tailnet's administrator must approve this device in the admin console.</summary>
    public const string MachineAuth = "machine-auth";
    /// <summary>The node is still starting (or still reaching Tailscale) after the time it was given.</summary>
    public const string Starting = "starting";
    /// <summary>The node is stopped.</summary>
    public const string Stopped = "stopped";
    /// <summary>This device has no working network, or cannot reach Tailscale's servers.</summary>
    public const string Offline = "offline";
    /// <summary>Not a device of the tailnet (this computer, the home network, the Internet): never sent through the node.</summary>
    public const string Refused = "refused";
    /// <summary>The device is in the tailnet but offline (the Jellyfin PC is off, asleep or without network).</summary>
    public const string PeerOffline = "peer-offline";
    /// <summary>The device is online but did not answer through the tailnet.</summary>
    public const string Unreachable = "unreachable";
    /// <summary>The device answered, but nothing listens on that port (Jellyfin is stopped).</summary>
    public const string Closed = "closed";
    /// <summary>Another Mira already runs the node of this profile.</summary>
    public const string Locked = "locked";
    /// <summary>The relay reached an HTTPS server whose certificate it refused.</summary>
    public const string Certificate = "certificate";
    /// <summary>The relay's request took too long.</summary>
    public const string Timeout = "timeout";
    /// <summary>The helper process is missing, ended or no longer answers.</summary>
    public const string Helper = "helper";
    /// <summary>The helper is not of this Mira's version, or could upload logs: never used.</summary>
    public const string Version = "version";
    /// <summary>The key that encrypts the node's state could not be read or kept.</summary>
    public const string Key = "key";
    /// <summary>The helper did not understand a request.</summary>
    public const string Protocol = "protocol";

    public static IReadOnlyList<string> All { get; } =
        [NeedsLogin, Expired, MachineAuth, Starting, Stopped, Offline, Refused, PeerOffline, Unreachable, Closed, Locked, Certificate, Timeout, Helper, Version, Key, Protocol];
}
