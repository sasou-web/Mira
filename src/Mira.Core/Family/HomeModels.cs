using System.Net;
using System.Text.Json.Serialization;
using Mira.Core.TorLink;

namespace Mira.Core.Family;

/// <summary>A profile of the home: a Jellyfin account its administrator offered (never an administrator's), with a PIN or not.</summary>
public sealed record HomeProfile(string Id, string Name, string? ImageTag, bool Pin);

/// <summary>This device let into the home (POST Mira/home/join): its home token, once; the profiles; the device id its profiles' sessions use.</summary>
public sealed record HomeJoined(string Token, IReadOnlyList<HomeProfile> Profiles, string DeviceId);

/// <summary>The profiles this device may open (GET Mira/home/profiles), the one it has open.</summary>
public sealed record HomeProfiles(IReadOnlyList<HomeProfile> Profiles, string? Current, string DeviceId);

/// <summary>Why the home said no (<see cref="HomeRefusedException"/>).</summary>
public enum HomeRefusal
{
    /// <summary>This Jellyfin has no home (turned off, or never on).</summary>
    NoHome,
    /// <summary>Not the home's password.</summary>
    Wrong,
    /// <summary>Too many tries: a few minutes to wait.</summary>
    TooMany,
    /// <summary>The home has its most devices.</summary>
    Full,
    /// <summary>This device is no longer in the home (removed, the password changed): its password again.</summary>
    OutOfHome,
    /// <summary>The profile is no longer in the home.</summary>
    NotInHome,
    /// <summary>The profile has a PIN: asked.</summary>
    PinAsked,
    /// <summary>Not the profile's PIN.</summary>
    PinWrong,
    /// <summary>Too many wrong PINs: <see cref="HomeRefusedException.Wait"/> seconds to wait.</summary>
    PinWait,
    /// <summary>Jellyfin did not answer the extension, or an administrator must first open the home's settings.</summary>
    NotReady,
    /// <summary>Anything else, said in the extension's words.</summary>
    Other,
}

/// <summary>The home's refusal, in the extension's words (in Mira's language), and what it means.</summary>
public sealed class HomeRefusedException(string message, HttpStatusCode status, HomeRefusal kind, int? left = null, int? wait = null) : RelayRefusedException(message, status)
{
    public HomeRefusal Kind { get; } = kind;
    /// <summary>The PIN's tries left before the wait.</summary>
    public int? Left { get; } = left;
    /// <summary>The seconds to wait before another PIN.</summary>
    public int? Wait { get; } = wait;
}

/// <summary>
/// This device in a home, kept protected on it (DPAPI, the keychain): where the home's Jellyfin is, its own id, its
/// name, the home token, the device id of its profiles' sessions, whether it is reached through the home's own node (a
/// home link), and the profiles last read, for the hall to show at once.
/// </summary>
public sealed record HomeMembership
{
    public required string Server { get; init; }
    public string? ServerId { get; init; }
    public required string Name { get; init; }
    public required string Token { get; init; }
    public required string DeviceId { get; init; }
    /// <summary>Joined from a home link: <see cref="Server"/> is on the home's tailnet, reached through the home's node.</summary>
    public bool ThroughTailnet { get; init; }
    public IReadOnlyList<HomeProfile> Profiles { get; init; } = [];
    /// <summary>The profile open on this device, as the home last said.</summary>
    public string? Current { get; init; }
}

// ---- The administrator's side ----

/// <summary>An account of the server as the home's settings show it.</summary>
public sealed record HomeAccount(string Id, string Name, string? ImageTag, bool Admin, bool Disabled, bool Offered, bool Pin, bool AllLibraries, string? MaxRating);

/// <summary>A device in the home: its name, when it joined and was last seen, the profile it has open.</summary>
public sealed record HomeDeviceInfo(string Id, string Name, DateTimeOffset Joined, DateTimeOffset Seen, string? Profile, string? ProfileName);

/// <summary>The home as its administrator sees it (GET Mira/home/settings).</summary>
public sealed record HomeSettings(bool Home, DateTimeOffset? Changed, int MinPassword, IReadOnlyList<HomeAccount> Profiles, IReadOnlyList<HomeDeviceInfo> Devices);

/// <summary>What the administrator writes once in their tailnet's policy, and the OAuth client's scopes.</summary>
public sealed record HomeTailnetSetup(string Tag, string TagOwners, string Grant, IReadOnlyList<string> Scopes);

/// <summary>A rule of the tailnet's policy that lets the home's devices go further than Jellyfin.</summary>
public sealed record HomeOpenRule(string Section, int Number, IReadOnlyList<string> Sources);

/// <summary>
/// What Mira would change in the tailnet's policy (<see cref="HomeGuide.Change"/>): « tag » (added to tagOwners), « grant »
/// (the rule to Jellyfin), « narrow » (a rule's sources, as they become).
/// </summary>
public sealed record HomePolicyChange(string Kind, string? Section, int Number, IReadOnlyList<string>? Sources);

/// <summary>A device of the home's tag on the tailnet, and the home's device it is, when it is one.</summary>
public sealed record HomeTailnetNode(string Id, string Name, IReadOnlyList<string> Addresses, DateTimeOffset? Created, DateTimeOffset? LastSeen, string? Device, string? DeviceName);

/// <summary>
/// The home on its administrator's tailnet (GET Mira/home/tailnet): connected by an OAuth client or not, the server's
/// Tailscale address, what to write once, and the policy's verdict (<see cref="HomeText.Policy"/>).
/// </summary>
public sealed record HomeTailnetState
{
    public bool Connected { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public string? ClientId { get; init; }
    public string? Server { get; init; }
    public int Port { get; init; }
    public HomeTailnetSetup? Setup { get; init; }
    public string? Policy { get; init; }
    public IReadOnlyList<HomeOpenRule> Open { get; init; } = [];
    /// <summary>What Mira can change itself for the policy to be right (POST Mira/home/tailnet/fix); null when it can't, or needn't.</summary>
    public IReadOnlyList<HomePolicyChange>? Fix { get; init; }
    /// <summary>The OAuth client can do more than the home needs (« All »): its secret, kept on the server, is worth too much.</summary>
    public bool Broad { get; init; }
    /// <summary>The scopes the home needs that the client lacks (auth_keys, devices:core, policy_file).</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];
    /// <summary>Whether the tailnet's policy has the home's tag (unknown, so true, when it could not be read).</summary>
    public bool Tag { get; init; } = true;
    /// <summary>The policy's lines Mira's fix adds or changes (« See the exact lines »).</summary>
    public IReadOnlyList<string>? Lines { get; init; }
    /// <summary>Until when Mira's fix of the policy can be undone; null when it can't.</summary>
    public DateTimeOffset? UndoUntil { get; init; }
    public IReadOnlyList<HomeTailnetNode> Devices { get; init; } = [];
}

/// <summary>
/// The administrator's side refused something of the tailnet (connect, fix, undo): the extension's words, its code
/// (« too-broad », « missing », « refused », « no-write »…, read by <see cref="HomeGuide.Trouble"/>), the scopes missing,
/// and whether the client's id is to be typed apart (the secret did not carry it, or Tailscale refused it).
/// </summary>
public sealed class HomeTailnetRefusedException(string message, HttpStatusCode status, string? policy, IReadOnlyList<string> missing, bool needsId) : RelayRefusedException(message, status)
{
    public string? Policy { get; } = policy;
    public IReadOnlyList<string> Missing { get; } = missing;
    public bool NeedsId { get; } = needsId;
}

/// <summary>A home link made (POST Mira/home/link), and when its key ends.</summary>
public sealed record HomeLinkMade(string Link, DateTimeOffset Expires);

/// <summary>A link refused because of the tailnet (409, 502): its policy's verdict, in the extension's words.</summary>
public sealed class HomeLinkRefusedException(string message, HttpStatusCode status, string? policy, IReadOnlyList<HomeOpenRule> open) : RelayRefusedException(message, status)
{
    public string? Policy { get; } = policy;
    public IReadOnlyList<HomeOpenRule> Open { get; } = open;
}

internal sealed record HomeAnswer
{
    [JsonPropertyName("home")] public bool Home { get; init; }
    [JsonPropertyName("token")] public string? Token { get; init; }
    [JsonPropertyName("profiles")] public List<HomeProfile>? Profiles { get; init; }
    [JsonPropertyName("current")] public string? Current { get; init; }
    [JsonPropertyName("deviceId")] public string? DeviceId { get; init; }
    [JsonPropertyName("accessToken")] public string? AccessToken { get; init; }
    [JsonPropertyName("userId")] public string? UserId { get; init; }
    [JsonPropertyName("userName")] public string? UserName { get; init; }
    [JsonPropertyName("serverId")] public string? ServerId { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("pin")] public bool Pin { get; init; }
    [JsonPropertyName("left")] public int? Left { get; init; }
    [JsonPropertyName("wait")] public int? Wait { get; init; }
    [JsonPropertyName("policy")] public string? Policy { get; init; }
    [JsonPropertyName("open")] public List<HomeOpenRule>? Open { get; init; }
    [JsonPropertyName("link")] public string? Link { get; init; }
    [JsonPropertyName("missing")] public List<string>? Missing { get; init; }
    [JsonPropertyName("needsId")] public bool NeedsId { get; init; }
    [JsonPropertyName("expires")] public DateTimeOffset? Expires { get; init; }
}
