using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Mira.Jellyfin;

/// <summary>
/// « Inviter un ami » by a link, at /Mira/friend (<see cref="FriendInvites"/>). The administrator's side, with their own
/// session: a short link for a friend's account (a key of <see cref="HomeTailnet.FriendTag"/> made with the home's OAuth client,
/// the policy given friends' tag first when it lacks it, or the home's tag when Tailscale refuses friends' own), the
/// invitations and devices, one let in, cancelled or taken out, a friend forgotten. The friend's side, without a session:
/// the invitation used, then, once the administrator let the device in, a session of the account, made by Jellyfin itself (<see cref="ISessionManager.AuthenticateDirect"/>)
/// under a device id of its own (<see cref="FriendInvites.JellyfinDevice"/>). Never an administrator's account. Never
/// through Tailscale Funnel: <see cref="TunnelGuard"/> lets nothing of /Mira/friend in from the Internet.
/// </summary>
[ApiController]
[Route("Mira/friend")]
public sealed class FriendController : ControllerBase
{
    private readonly IHttpClientFactory _clients;
    private readonly IServerApplicationHost _host;
    private readonly ISessionManager _sessions;
    private readonly ILogger<FriendController> _logger;

    public FriendController(IHttpClientFactory clients, IServerApplicationHost host, ISessionManager sessions, ILogger<FriendController> logger)
    {
        _clients = clients;
        _host = host;
        _sessions = sessions;
        _logger = logger;
    }

    private static FriendInvites Friends => Plugin.Friends;
    private bool French => RelayText.French(Request);
    private IEnumerable<string> Roots() => JellyfinLoopback.Roots(_host, Request.PathBase);

    // ---- The friend's side ----

    /// <summary>
    /// An invitation used, once: the device waits for the administrator to let it in (202 and a wait token), then asks
    /// with that token at /Mira/friend/wait for its session.
    /// </summary>
    [HttpPost("redeem")]
    [AllowAnonymous]
    [RequestSizeLimit(4_096)]
    public async Task<IActionResult> Redeem(CancellationToken cancellationToken)
    {
        var body = await ReadAsync<RedeemBody>(cancellationToken).ConfigureAwait(false);
        if (body?.Device is not { } device || !DeviceRule(device))
        {
            return BadRequest();
        }

        var (result, invite, entry, replaced) = Friends.Redeem(body.Invite, device, DeviceName(body.Name), ClientAddress(), out var wait);
        switch (result)
        {
            case FriendInvites.Redeeming.TooMany:
                return Refuse(StatusCodes.Status429TooManyRequests, "too-many", L("Trop d’essais : attends quelques minutes.", "Too many tries: wait a few minutes."));
            case FriendInvites.Redeeming.Wrong:
                return Refuse(StatusCodes.Status404NotFound, "wrong", L("Ce lien n’est pas une invitation de ce Jellyfin.", "This link isn’t an invitation from this Jellyfin."));
            case FriendInvites.Redeeming.Used:
                return Refuse(StatusCodes.Status410Gone, "used", L("Ce lien a déjà servi. Demande-en un nouveau à qui te l’a envoyé.", "This link has already been used. Ask whoever sent it for a new one."));
            case FriendInvites.Redeeming.Ended:
                return Refuse(StatusCodes.Status410Gone, "ended", L("Ce lien a expiré. Demande-en un nouveau à qui te l’a envoyé.", "This link has ended. Ask whoever sent it for a new one."));
            case FriendInvites.Redeeming.Full:
                return Refuse(StatusCodes.Status409Conflict, "full", L("Ce Jellyfin a déjà trop d’appareils d’amis.", "This Jellyfin already has too many friends’ devices."));
        }

        using var client = _clients.CreateClient();
        var account = await AccountAsync(client, invite!.User, null, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            Friends.Undo(invite, entry!);
            return Refuse(StatusCodes.Status502BadGateway, "unreachable", L("Jellyfin n’a pas répondu. Réessaie dans un instant.", "Jellyfin didn’t answer. Try again in a moment."));
        }

        if (!account.Found || account.Admin || account.Disabled)
        {
            // Never an administrator, never an account gone or disabled since the link was made.
            Friends.RemoveDevice(entry!.Id);
            return Refuse(StatusCodes.Status410Gone, "ended", L("Ce compte n’existe plus sur ce Jellyfin.", "This account no longer exists on this Jellyfin."));
        }

        if (replaced is not null)
        {
            await EndSessionsAsync(client, FriendInvites.JellyfinDevice(replaced), null, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Mira: a friend's invitation was used ({Device}); waiting for an administrator to let it in", entry!.Name);
        return Answer(StatusCodes.Status202Accepted, new { state = "waiting", wait, userName = account.Name });
    }

    /// <summary>
    /// A device that used an invitation asks whether it may come in: 202 while it waits, its session once the
    /// administrator let it in (once), 410 when it was refused or taken out.
    /// </summary>
    [HttpPost("wait")]
    [AllowAnonymous]
    [RequestSizeLimit(4_096)]
    public async Task<IActionResult> Wait(CancellationToken cancellationToken)
    {
        var body = await ReadAsync<WaitBody>(cancellationToken).ConfigureAwait(false);
        if (Friends.Waiting(body?.Wait) is not { } entry)
        {
            return Refuse(StatusCodes.Status410Gone, "refused", L("Ton ami n’a pas laissé entrer cet appareil. Demande-lui une nouvelle invitation.", "Your friend didn’t let this device in. Ask them for a new invite."));
        }

        if (!entry.Approved)
        {
            return Answer(StatusCodes.Status202Accepted, new { state = "waiting" });
        }

        using var client = _clients.CreateClient();
        var account = await AccountAsync(client, entry.User, null, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Refuse(StatusCodes.Status502BadGateway, "unreachable", L("Jellyfin n’a pas répondu. Réessaie dans un instant.", "Jellyfin didn’t answer. Try again in a moment."));
        }

        if (!account.Found || account.Admin || account.Disabled)
        {
            Friends.RemoveDevice(entry.Id);
            return Refuse(StatusCodes.Status410Gone, "ended", L("Ce compte n’existe plus sur ce Jellyfin.", "This account no longer exists on this Jellyfin."));
        }

        string? token;
        string? serverId;
        try
        {
            var opened = await _sessions.AuthenticateDirect(new AuthenticationRequest
            {
                UserId = Guid.ParseExact(entry.User, "N"),
                Username = account.Name,
                App = Short(body!.Client, "Mira"),
                AppVersion = Short(body.Version, "0"),
                DeviceId = FriendInvites.JellyfinDevice(entry),
                DeviceName = entry.Name,
                RemoteEndPoint = ClientAddress(),
            }).ConfigureAwait(false);
            token = opened.AccessToken;
            serverId = opened.ServerId;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(error, "Mira: Jellyfin did not open the session of a friend's device");
            token = null;
            serverId = null;
        }

        if (string.IsNullOrEmpty(token))
        {
            // The same wait token works again.
            return Refuse(StatusCodes.Status502BadGateway, "unreachable", L("Jellyfin n’a pas ouvert la séance. Réessaie dans un instant.", "Jellyfin didn’t open the session. Try again in a moment."));
        }

        Friends.Delivered(entry.Id);
        return Answer(StatusCodes.Status200OK, new { accessToken = token, userId = entry.User, userName = account.Name, serverId, deviceId = FriendInvites.JellyfinDevice(entry) });
    }

    // ---- The administrator's side ----

    /// <summary>
    /// The invitations and friends' devices (their last connection from Tailscale when it says), whether a link can be
    /// made now, and with which tag. Nodes to delete are deleted now.
    /// </summary>
    [HttpGet("")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> State(CancellationToken cancellationToken)
    {
        var server = HomeTailnet.OwnAddress();
        IReadOnlyList<TailnetDevice> nodes = [];
        string? tailnet = Plugin.HomeTailnet.Client is null ? "disconnected" : server is null ? "no-tailscale" : null;
        if (Plugin.HomeTailnet.Client is { } owner)
        {
            using var client = _clients.CreateClient();
            try
            {
                nodes = await HomeController.SweepAsync(new TailscaleApi(client, owner.Id, owner.Secret), cancellationToken).ConfigureAwait(false);
            }
            catch (TailscaleApi.TailscaleException error)
            {
                tailnet ??= error.Failure == TailscaleApi.Failure.Refused ? "refused" : "unreachable";
            }
        }

        var now = DateTimeOffset.UtcNow;
        var seen = nodes.Where(x => x.Tagged).SelectMany(n => n.Addresses.Select(a => (a, n))).GroupBy(x => x.a, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().n, StringComparer.OrdinalIgnoreCase);
        Response.Headers.CacheControl = "no-store";
        return Ok(new
        {
            tailnet = tailnet ?? "ok",
            tag = Friends.FriendTagRefused ? HomeTailnet.Tag : HomeTailnet.FriendTag,
            invites = Friends.Invites().Select(x => new
            {
                id = x.Id,
                user = x.User,
                name = x.Name,
                created = x.Created,
                until = x.Until,
                used = x.Used,
                state = x.Used is not null ? "used" : x.Cancelled ? "cancelled" : now >= x.Until ? "ended" : "open",
            }),
            devices = Friends.Devices().Select(x => new
            {
                id = x.Id,
                user = x.User,
                name = x.Name,
                joined = x.Joined,
                tailnet = x.Tailnet is not null,
                waiting = !x.Approved,
                lastSeen = x.Tailnet is { } a && seen.TryGetValue(a, out var node) ? node.LastSeen : null,
            }),
        });
    }

    /// <summary>
    /// A link for a friend's account: a key for one device, that ends in <see cref="FriendInvites.Life"/>, this PC's
    /// Tailscale address, and an invitation for the account. Friends' tag is added to the policy first when it lacks it
    /// (its owners and its one rule to Jellyfin: nothing else changes); the home's tag is used when Tailscale refuses
    /// friends' own to the home's client. Only when the policy limits the chosen tag to this Jellyfin.
    /// </summary>
    [HttpPost("invite")]
    [Authorize(Policy = "RequiresElevation")]
    [RequestSizeLimit(1_024)]
    public async Task<IActionResult> Invite(CancellationToken cancellationToken)
    {
        var body = await ReadAsync<InviteBody>(cancellationToken).ConfigureAwait(false);
        if (HomeFamily.Plain(body?.User) is not { } user)
        {
            return BadRequest();
        }

        if (Plugin.HomeTailnet.Client is not { } owner)
        {
            return Refuse(StatusCodes.Status409Conflict, "disconnected", L("Relie d’abord Mira à ton réseau Tailscale (Réglages → Foyer).", "First connect Mira to your Tailscale network (Settings → Home)."));
        }

        if (HomeTailnet.OwnAddress() is not { } server)
        {
            return Refuse(StatusCodes.Status409Conflict, "no-tailscale", L("Tailscale n’est pas allumé sur le PC de Jellyfin.", "Tailscale isn’t on on Jellyfin’s PC."));
        }

        using var client = _clients.CreateClient();
        var account = await AccountAsync(client, user, GuestAccounts.SessionOf(Request), cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Refuse(StatusCodes.Status502BadGateway, "unreachable", L("Jellyfin n’a pas répondu. Réessaie dans un instant.", "Jellyfin didn’t answer. Try again in a moment."));
        }

        if (!account.Found || account.Disabled)
        {
            return Refuse(StatusCodes.Status404NotFound, "no-account", L("Ce compte n’existe plus.", "This account no longer exists."));
        }

        if (account.Admin)
        {
            return Refuse(StatusCodes.Status409Conflict, "admin", L("Une invitation n’ouvre jamais un compte administrateur.", "An invitation never opens an administrator’s account."));
        }

        var api = new TailscaleApi(client, owner.Id, owner.Secret);
        var port = _host.HttpPort;
        try
        {
            var tag = await TagAsync(api, server, port, cancellationToken).ConfigureAwait(false);
            if (tag is null)
            {
                return Refuse(StatusCodes.Status409Conflict, "policy", L("Ta politique Tailscale laisse ces appareils aller ailleurs que sur Jellyfin : corrige-la d’abord dans Réglages → Foyer.", "Your Tailscale policy lets these devices go beyond Jellyfin: fix it first in Settings → Home."));
            }

            (string Key, string Id, DateTimeOffset Expires) key;
            try
            {
                key = await api.CreateKeyAsync(FriendInvites.Life, cancellationToken, tag).ConfigureAwait(false);
            }
            catch (TailscaleApi.TailscaleException error) when (error.Failure == TailscaleApi.Failure.Scope && tag == HomeTailnet.FriendTag)
            {
                // The home's client may not make keys of friends' tag: the home's own, from now on.
                _logger.LogInformation("Mira: Tailscale refused a key of friends' tag to the home's client; friends' links use the home's tag");
                Friends.FriendTagRefused = true;
                tag = await TagAsync(api, server, port, cancellationToken).ConfigureAwait(false);
                if (tag is null)
                {
                    return Refuse(StatusCodes.Status409Conflict, "policy", L("Ta politique Tailscale laisse ces appareils aller ailleurs que sur Jellyfin : corrige-la d’abord dans Réglages → Foyer.", "Your Tailscale policy lets these devices go beyond Jellyfin: fix it first in Settings → Home."));
                }

                key = await api.CreateKeyAsync(FriendInvites.Life, cancellationToken, tag).ConfigureAwait(false);
            }

            // The key is the invitation too: the link carries nothing else secret, and stays short.
            var link = FriendInvites.Link(server, port, Request.PathBase, key.Key);
            if (link is null || Friends.Invite(user, account.Name, key.Id, tag, key.Expires, key.Key) is not var (_, invite))
            {
                await api.DeleteKeyAsync(key.Id, CancellationToken.None).ConfigureAwait(false);
                return Refuse(StatusCodes.Status409Conflict, "full", L("Trop d’invitations attendent déjà : annules-en une.", "Too many invitations are already waiting: cancel one."));
            }

            Response.Headers.CacheControl = "no-store";
            return Ok(new { link, expires = key.Expires, id = invite.Id, tag });
        }
        catch (TailscaleApi.TailscaleException error)
        {
            var (state, message) = error.Failure switch
            {
                TailscaleApi.Failure.Refused => ("refused", L("Tailscale refuse le client OAuth de Mira : il a été supprimé ou révoqué. Relie de nouveau Mira dans Réglages → Foyer.", "Tailscale refuses Mira’s OAuth client: it was deleted or revoked. Connect Mira again in Settings → Home.")),
                TailscaleApi.Failure.Scope => ("scope", L("Le client OAuth de Mira n’a pas les droits qu’il faut (auth_keys, devices:core, policy_file et l’étiquette tag:mira-home).", "Mira’s OAuth client lacks the rights it needs (auth_keys, devices:core, policy_file and the tag tag:mira-home).")),
                TailscaleApi.Failure.Changed => ("changed", L("Ta politique Tailscale vient de changer : réessaie.", "Your Tailscale policy just changed: try again.")),
                _ => ("unreachable", L("Tailscale n’a pas répondu. Réessaie dans un instant.", "Tailscale didn’t answer. Try again in a moment.")),
            };
            return Refuse(error.Failure == TailscaleApi.Failure.Unreachable ? StatusCodes.Status502BadGateway : StatusCodes.Status409Conflict, state, message);
        }
    }

    /// <summary>An invitation not used yet cancelled: its key revoked on Tailscale.</summary>
    [HttpDelete("invites/{id}")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> Cancel(string id, CancellationToken cancellationToken)
    {
        if (Friends.Cancel(id) is not { } invite)
        {
            return NotFound();
        }

        using var client = _clients.CreateClient();
        await RevokeAsync(client, [invite], cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>A device that used a friend's link let in (« Laisser entrer »): it gets its session at its next ask.</summary>
    [HttpPost("devices/{id}/approve")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult Approve(string id) => Friends.Approve(id) is null ? NotFound() : NoContent();

    /// <summary>A friend's device taken out, or one waiting refused: its session ended, its node deleted from the tailnet.</summary>
    [HttpDelete("devices/{id}")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> RemoveDevice(string id, CancellationToken cancellationToken)
    {
        if (Friends.RemoveDevice(id) is not { } device)
        {
            return NotFound();
        }

        using var client = _clients.CreateClient();
        await OutAsync(client, [device], cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// A friend forgotten, before or after their account is deleted: their invitations' keys revoked, their devices'
    /// sessions ended and nodes deleted.
    /// </summary>
    [HttpDelete("users/{user}")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> Forget(string user, CancellationToken cancellationToken)
    {
        if (HomeFamily.Plain(user) is not { } id)
        {
            return BadRequest();
        }

        var (open, devices) = Friends.Forget(id);
        using var client = _clients.CreateClient();
        await RevokeAsync(client, open, cancellationToken).ConfigureAwait(false);
        await OutAsync(client, devices, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    // ---- Inside ----

    /// <summary>
    /// The tag a friend's key gets that is also the invitation, the policy given friends' tag first when it only lacks it (its owners, its rule to
    /// Jellyfin); null when neither tag is limited to Jellyfin.
    /// </summary>
    private async Task<string?> TagAsync(TailscaleApi api, string server, int port, CancellationToken cancellationToken)
    {
        var (text, version) = await api.PolicyTextAsync(cancellationToken).ConfigureAwait(false);
        var policy = HomePolicyFix.Parse(text);
        if (!Friends.FriendTagRefused)
        {
            var verdict = HomeTailnet.CheckPolicy(policy, server, port, HomeTailnet.FriendTag).Verdict;
            if (verdict == HomeTailnet.Verdict.Ok)
            {
                return HomeTailnet.FriendTag;
            }

            // Only additions: friends' tag and its rule. A policy that needs narrowing is the home's fix, which the administrator reads first.
            if (HomePolicyFix.Make(text, server, port, HomeTailnet.FriendTag) is { } plan && plan.Changes.All(x => x.Kind is "tag" or "grant"))
            {
                try
                {
                    await api.SetPolicyAsync(plan.Text, version, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Mira: friends' tag added to the tailnet's policy ({Changes})", string.Join(", ", plan.Changes.Select(x => x.Kind)));
                    return HomeTailnet.FriendTag;
                }
                catch (TailscaleApi.TailscaleException error) when (error.Failure is TailscaleApi.Failure.Scope or TailscaleApi.Failure.Invalid)
                {
                    _logger.LogWarning("Mira: friends' tag could not be added to the tailnet's policy ({Failure}); the home's tag is used", error.Failure);
                }
            }
        }

        return HomeTailnet.CheckPolicy(policy, server, port).Verdict == HomeTailnet.Verdict.Ok ? HomeTailnet.Tag : null;
    }

    /// <summary>The keys of invitations not used yet revoked, as far as Tailscale can be reached now.</summary>
    private async Task RevokeAsync(HttpClient client, IReadOnlyList<FriendInvite> invites, CancellationToken cancellationToken)
    {
        if (invites.Count == 0 || Plugin.HomeTailnet.Client is not { } owner)
        {
            return;
        }

        var api = new TailscaleApi(client, owner.Id, owner.Secret);
        foreach (var invite in invites.Where(x => x.KeyId.Length > 0))
        {
            try
            {
                await api.DeleteKeyAsync(invite.KeyId, cancellationToken).ConfigureAwait(false);
            }
            catch (TailscaleApi.TailscaleException error)
            {
                // It ends by itself within 48 hours, and the invitation already refuses it.
                _logger.LogWarning("Mira: the key of a cancelled invitation could not be revoked ({Failure})", error.Failure);
            }
        }
    }

    /// <summary>Devices out: their sessions ended, their nodes deleted from the tailnet now or at the next call that reaches it.</summary>
    private async Task OutAsync(HttpClient client, IReadOnlyList<FriendDevice> devices, CancellationToken cancellationToken)
    {
        foreach (var device in devices)
        {
            await EndSessionsAsync(client, FriendInvites.JellyfinDevice(device), GuestAccounts.SessionOf(Request), cancellationToken).ConfigureAwait(false);
            if (device.Tailnet is { } address)
            {
                Plugin.HomeTailnet.Remove(address);
            }
        }

        if (devices.Any(x => x.Tailnet is not null) && Plugin.HomeTailnet.Client is { } owner)
        {
            try
            {
                await HomeController.SweepAsync(new TailscaleApi(client, owner.Id, owner.Secret), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TailscaleApi.TailscaleException error)
            {
                _logger.LogWarning("Mira: a friend's device could not be deleted from the tailnet yet ({Failure})", error.Failure);
            }
        }
    }

    /// <summary>Every Jellyfin session under a device id ended, with the administrator's session or the plugin's API key.</summary>
    private async Task EndSessionsAsync(HttpClient client, string deviceId, IReadOnlyList<(string, string)>? admin, CancellationToken cancellationToken)
    {
        var session = admin ?? (Plugin.Invites.Key is { } key ? GuestAccounts.WithKey(key) : null);
        if (session is null)
        {
            return;
        }

        var id = Uri.EscapeDataString(deviceId);
        var (known, _) = await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Get, $"Devices/Info?id={id}", session, null, cancellationToken).ConfigureAwait(false);
        if (known != (int)HttpStatusCode.OK)
        {
            return;
        }

        var (status, _) = await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Delete, $"Devices?id={id}", session, null, cancellationToken).ConfigureAwait(false);
        if (status is not ((int)HttpStatusCode.NoContent or (int)HttpStatusCode.OK))
        {
            _logger.LogWarning("Mira: Jellyfin did not end the session of a friend's device ({Status})", status);
        }
    }

    /// <summary>One account read with the administrator's session or the plugin's API key: null without either, or without an answer.</summary>
    private async Task<Account?> AccountAsync(HttpClient client, string userId, IReadOnlyList<(string, string)>? admin, CancellationToken cancellationToken)
    {
        var session = admin ?? (Plugin.Invites.Key is { } key ? GuestAccounts.WithKey(key) : null);
        if (session is null)
        {
            return null;
        }

        var (status, text) = await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Get, $"Users/{userId}", session, null, cancellationToken).ConfigureAwait(false);
        if (status == (int)HttpStatusCode.NotFound)
        {
            return new Account("", false, false, false);
        }

        if (status != (int)HttpStatusCode.OK || GuestAccounts.Parse(text) is not JsonObject user)
        {
            return null;
        }

        var policy = user["Policy"] as JsonObject;
        static bool Flag(JsonNode? value) => value is JsonValue v && v.TryGetValue<bool>(out var flag) && flag;
        return new Account(GuestAccounts.Text(user["Name"]) ?? "", true, Flag(policy?["IsAdministrator"]), Flag(policy?["IsDisabled"]));
    }

    private async Task<T?> ReadAsync<T>(CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(Request.Body, JsonWeb, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string ClientAddress() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

    private static bool DeviceRule(string device) =>
        device.Length is >= 8 and <= 128 && device.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private string DeviceName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? L("Appareil", "Device") : clean[..Math.Min(clean.Length, 60)];
    }

    private static string Short(string? text, string fallback)
    {
        var clean = new string((text ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '-' or '_').ToArray()).Trim();
        return clean.Length == 0 ? fallback : clean[..Math.Min(clean.Length, 40)];
    }

    private string L(string french, string english) => French ? french : english;

    private ObjectResult Answer(int status, object body)
    {
        Response.Headers.CacheControl = "no-store";
        return StatusCode(status, body);
    }

    private ObjectResult Refuse(int status, string state, string error) => Answer(status, new { error, state });

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    private sealed record RedeemBody(string? Invite, string? Device, string? Name, string? Client, string? Version);

    private sealed record WaitBody(string? Wait, string? Client, string? Version);

    private sealed record InviteBody(string? User);

    private sealed record Account(string Name, bool Found, bool Admin, bool Disabled);
}
