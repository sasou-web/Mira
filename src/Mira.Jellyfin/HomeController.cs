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
/// « Le foyer », at /Mira/home (<see cref="HomeFamily"/>). The family's side, without a Jellyfin session: whether this
/// server has a home, joining it with its password (a home token for the device, in the <see cref="TokenHeader"/>
/// header after), the profiles, a profile opened (its PIN checked here), closed, the home left. The opened profile is a
/// real session of that Jellyfin account, made by Jellyfin itself (<see cref="ISessionManager.AuthenticateDirect"/>),
/// under a device id of the home's (<see cref="HomeFamily.JellyfinDevice"/>): its history, favourites, parental rating
/// and libraries stay Jellyfin's. An administrator's account is offered and opened only with a PIN (the home's
/// password alone must never open the server), and its PIN is guessed far slower (<see cref="HomeFamily.AdminPinWait"/>). The
/// administrator's side, with their own session: the password, the accounts offered, their PIN, the devices and
/// « remove ». A profile's own session may set its PIN. Never through Tailscale Funnel: <see cref="TunnelGuard"/>
/// lets nothing of /Mira/home in from the Internet.
/// </summary>
[ApiController]
[Route("Mira/home")]
public sealed class HomeController : ControllerBase
{
    /// <summary>The header that carries a device's home token.</summary>
    public const string TokenHeader = "X-Mira-Home";
    // Jellyfin's own claim for the account of a request (its InternalClaimTypes.UserId).
    private const string UserClaim = "Jellyfin-UserId";

    private readonly IHttpClientFactory _clients;
    private readonly IServerApplicationHost _host;
    private readonly ISessionManager _sessions;
    private readonly ILogger<HomeController> _logger;

    public HomeController(IHttpClientFactory clients, IServerApplicationHost host, ISessionManager sessions, ILogger<HomeController> logger)
    {
        _clients = clients;
        _host = host;
        _sessions = sessions;
        _logger = logger;
    }

    private static HomeFamily Home => Plugin.Home;
    private bool French => RelayText.French(Request);
    private IEnumerable<string> Roots() => JellyfinLoopback.Roots(_host, Request.PathBase);

    // ---- The family's side ----

    /// <summary>Whether this server has a home: what Mira's sign-in asks before it shows the home's password.</summary>
    [HttpGet("")]
    [AllowAnonymous]
    public IActionResult State()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new { home = Home.On });
    }

    /// <summary>A device joins with the home's password: its home token, once, and the profiles.</summary>
    [HttpPost("join")]
    [AllowAnonymous]
    [RequestSizeLimit(4_096)]
    public async Task<IActionResult> Join(CancellationToken cancellationToken)
    {
        var body = await ReadAsync<JoinBody>(cancellationToken).ConfigureAwait(false);
        if (body?.Device is not { } device || !DeviceRule(device))
        {
            return BadRequest();
        }

        var (result, token, entry, replaced) = Home.Join(body.Password, device, DeviceName(body.Name), ClientAddress());
        switch (result)
        {
            case HomeFamily.Joining.NoHome:
                return Refuse(StatusCodes.Status404NotFound, "Ce Jellyfin n’a pas de foyer.");
            case HomeFamily.Joining.TooMany:
                return Refuse(StatusCodes.Status429TooManyRequests, "Trop d’essais : attends quelques minutes.");
            case HomeFamily.Joining.Wrong:
                return Refuse(StatusCodes.Status401Unauthorized, "Ce n’est pas le mot de passe du foyer.");
            case HomeFamily.Joining.Full:
                return Refuse(StatusCodes.Status409Conflict, "Le foyer a déjà trop d’appareils : demande d’en retirer un.");
        }

        using var client = _clients.CreateClient();
        if (replaced is not null)
        {
            await EndSessionsAsync(client, replaced, cancellationToken).ConfigureAwait(false);
        }

        var profiles = await FamilyProfilesAsync(client, cancellationToken).ConfigureAwait(false);
        Response.Headers.CacheControl = "no-store";
        return Ok(new { token, profiles = profiles ?? [], current = (string?)null, deviceId = HomeFamily.JellyfinDevice(entry!) });
    }

    /// <summary>The profiles this device may open, and the one it has open.</summary>
    [HttpGet("profiles")]
    [AllowAnonymous]
    public async Task<IActionResult> FamilyProfiles(CancellationToken cancellationToken)
    {
        if (Device() is not { } device)
        {
            return OutOfHome();
        }

        using var client = _clients.CreateClient();
        var profiles = await FamilyProfilesAsync(client, cancellationToken).ConfigureAwait(false);
        if (profiles is null)
        {
            return Refuse(StatusCodes.Status502BadGateway, "Un administrateur de ce Jellyfin doit d’abord ouvrir les réglages du foyer dans Mira.");
        }

        Response.Headers.CacheControl = "no-store";
        return Ok(new { profiles, current = device.Profile, deviceId = HomeFamily.JellyfinDevice(device) });
    }

    /// <summary>
    /// A profile opened on this device, with its PIN when it has one: the session this device had open ends first,
    /// then Jellyfin opens one for the account.
    /// </summary>
    [HttpPost("open")]
    [AllowAnonymous]
    [RequestSizeLimit(4_096)]
    public async Task<IActionResult> Open(CancellationToken cancellationToken)
    {
        if (Device() is not { } device)
        {
            return OutOfHome();
        }

        var body = await ReadAsync<OpenBody>(cancellationToken).ConfigureAwait(false);
        if (HomeFamily.Plain(body?.Profile) is not { } userId || Home.Profile(userId) is null)
        {
            return Refuse(StatusCodes.Status404NotFound, "Ce profil n’est pas dans le foyer.");
        }

        using var client = _clients.CreateClient();
        var account = await AccountAsync(client, userId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Refuse(StatusCodes.Status502BadGateway, "Un administrateur de ce Jellyfin doit d’abord ouvrir les réglages du foyer dans Mira.");
        }

        if (!account.Found || account.Disabled || (account.Admin && !Home.HasPin(userId)))
        {
            return Refuse(StatusCodes.Status404NotFound, "Ce profil n’est pas dans le foyer.");
        }

        var (pin, left, wait) = Home.CheckPin(userId, body!.Pin, account.Admin);
        switch (pin)
        {
            case HomeFamily.Pin.Asked:
                return Answer(StatusCodes.Status403Forbidden, new { error = Text("Ce profil a un code."), pin = true, left });
            case HomeFamily.Pin.Wrong:
                return Answer(StatusCodes.Status403Forbidden, new { error = Text("Ce n’est pas le bon code."), pin = true, left });
            case HomeFamily.Pin.Wait:
                Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Answer(StatusCodes.Status429TooManyRequests, new { error = Text("Trop d’essais : attends quelques minutes."), pin = true, wait = (int)Math.Ceiling(wait.TotalSeconds) });
        }

        // One profile at a time on a device: Jellyfin keeps the session of another account under the same device id
        // (12.1), so it is ended here first.
        await EndSessionsAsync(client, device, cancellationToken).ConfigureAwait(false);
        string? token;
        string? serverId;
        try
        {
            var result = await _sessions.AuthenticateDirect(new AuthenticationRequest
            {
                UserId = Guid.ParseExact(userId, "N"),
                Username = account.Name,
                App = Short(body.Client, "Mira"),
                AppVersion = Short(body.Version, "0"),
                DeviceId = HomeFamily.JellyfinDevice(device),
                DeviceName = device.Name,
                RemoteEndPoint = ClientAddress(),
            }).ConfigureAwait(false);
            token = result.AccessToken;
            serverId = result.ServerId;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Jellyfin's own refusals (too many sessions for the account, a device it refuses) and any change of its API.
            _logger.LogWarning(error, "Mira: Jellyfin did not open the session of a profile of the home");
            return Refuse(StatusCodes.Status502BadGateway, "Jellyfin n’a pas ouvert la séance. Réessaie dans un instant.");
        }

        if (string.IsNullOrEmpty(token) || !Home.Opened(device.Id, userId))
        {
            // Removed from the home meanwhile: nothing stays open.
            await EndSessionsAsync(client, device, CancellationToken.None).ConfigureAwait(false);
            return OutOfHome();
        }

        Response.Headers.CacheControl = "no-store";
        return Ok(new { accessToken = token, userId, userName = account.Name, serverId, deviceId = HomeFamily.JellyfinDevice(device) });
    }

    /// <summary>The profile open on this device closed; the device stays in the home.</summary>
    [HttpPost("close")]
    [AllowAnonymous]
    public async Task<IActionResult> Close(CancellationToken cancellationToken)
    {
        if (Device() is not { } device)
        {
            return OutOfHome();
        }

        using var client = _clients.CreateClient();
        await EndSessionsAsync(client, device, cancellationToken).ConfigureAwait(false);
        Home.Opened(device.Id, null);
        return NoContent();
    }

    /// <summary>This device leaves the home: its token stops working, its profile's session ends.</summary>
    [HttpPost("leave")]
    [AllowAnonymous]
    public async Task<IActionResult> Leave(CancellationToken cancellationToken)
    {
        if (Device() is not { } device)
        {
            return OutOfHome();
        }

        Home.Remove(device.Id);
        using var client = _clients.CreateClient();
        await EndSessionsAsync(client, device, cancellationToken).ConfigureAwait(false);
        await LeaveTailnetAsync(client, device, CancellationToken.None).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// A profile's own session sets or takes off its PIN: the current one asked when there is one (the home's password
    /// alone opens a profile without a PIN, never one with it).
    /// </summary>
    [HttpPut("pin")]
    [Authorize]
    [RequestSizeLimit(1_024)]
    public async Task<IActionResult> OwnPin(CancellationToken cancellationToken)
    {
        if (HomeFamily.Plain(User.FindFirst(UserClaim)?.Value) is not { } userId)
        {
            return Unauthorized();
        }

        var body = await ReadAsync<PinBody>(cancellationToken).ConfigureAwait(false);
        if (Home.Profile(userId) is not { } profile)
        {
            return Refuse(StatusCodes.Status404NotFound, "Ce profil n’est pas dans le foyer.");
        }

        if (body?.Pin is { } wanted && !HomeFamily.IsPin(wanted))
        {
            return Refuse(StatusCodes.Status400BadRequest, "Le code fait quatre chiffres.");
        }

        using var client = _clients.CreateClient();
        if (body?.Pin is null && (await AccountAsync(client, userId, cancellationToken).ConfigureAwait(false))?.Admin != false)
        {
            // An administrator's profile keeps its PIN (and an account Mira cannot read is treated as one).
            return Refuse(StatusCodes.Status409Conflict, "Un profil administrateur garde toujours son code.");
        }

        if (profile.Pin is not null)
        {
            var (pin, left, wait) = Home.CheckPin(userId, body?.Current);
            if (pin == HomeFamily.Pin.Wait)
            {
                return Answer(StatusCodes.Status429TooManyRequests, new { error = Text("Trop d’essais : attends quelques minutes."), wait = (int)Math.Ceiling(wait.TotalSeconds) });
            }

            if (pin != HomeFamily.Pin.Open)
            {
                return Answer(StatusCodes.Status403Forbidden, new { error = Text("Ce n’est pas le bon code."), left });
            }
        }

        Home.SetPin(userId, body?.Pin);
        return NoContent();
    }

    // ---- The administrator's side ----

    /// <summary>The home as its administrator sees it: on or off, every account (offered or not), the devices.</summary>
    [HttpGet("settings")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> Settings(CancellationToken cancellationToken)
    {
        using var client = _clients.CreateClient();
        // The plugin's API key, which the family's side reads the accounts with: made now if it is missing.
        await GuestAccounts.KeepKeyAsync(client, Roots(), Admin(), cancellationToken).ConfigureAwait(false);
        var (status, text) = await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Get, "Users", Admin(), null, cancellationToken).ConfigureAwait(false);
        if (status != (int)HttpStatusCode.OK || GuestAccounts.Parse(text) is not JsonArray users)
        {
            return Refuse(StatusCodes.Status502BadGateway, "Jellyfin n’a pas répondu. Réessaie dans un instant.");
        }

        var offered = Home.Profiles().ToDictionary(x => x.User, StringComparer.Ordinal);
        var accounts = users.OfType<JsonObject>().Select(Account.Of).Where(x => x.Id is not null && !Plugin.Invites.IsGuestAccount(x.Id))
            .Select(x => new
            {
                id = x.Id,
                name = x.Name,
                imageTag = x.ImageTag,
                admin = x.Admin,
                disabled = x.Disabled,
                offered = offered.ContainsKey(x.Id!),
                pin = offered.TryGetValue(x.Id!, out var p) && p.Pin is not null,
                download = x.Admin || Home.CanDownload(x.Id),
                allLibraries = x.AllLibraries,
                maxRating = x.MaxRating,
            }).ToList();
        var names = accounts.ToDictionary(x => x.id!, x => x.name, StringComparer.Ordinal);
        var devices = Home.Devices().Select(x => new
        {
            id = x.Id,
            name = x.Name,
            joined = x.Joined,
            seen = x.Seen,
            profile = x.Profile,
            profileName = x.Profile is { } user && names.TryGetValue(user, out var name) ? name : null,
        });
        Response.Headers.CacheControl = "no-store";
        return Ok(new { home = Home.On, changed = Home.Changed, minPassword = HomeFamily.MinPassword, profiles = accounts, devices });
    }

    /// <summary>The home's password set or changed (every device out), or null to turn the home off.</summary>
    [HttpPut("password")]
    [Authorize(Policy = "RequiresElevation")]
    [RequestSizeLimit(1_024)]
    public async Task<IActionResult> Password(CancellationToken cancellationToken)
    {
        var body = await ReadAsync<PasswordBody>(cancellationToken).ConfigureAwait(false);
        var password = string.IsNullOrEmpty(body?.Password) ? null : body.Password;
        if (password is not null && !HomeFamily.GoodPassword(password))
        {
            return Refuse(StatusCodes.Status400BadRequest, "Le mot de passe du foyer fait au moins 8 caractères.");
        }

        using var client = _clients.CreateClient();
        await GuestAccounts.KeepKeyAsync(client, Roots(), Admin(), cancellationToken).ConfigureAwait(false);
        var gone = Home.SetPassword(password) ?? [];
        foreach (var device in gone)
        {
            await EndSessionsAsync(client, device, CancellationToken.None, Admin()).ConfigureAwait(false);
        }

        // The home turned off: its devices leave the tailnet too. A new password keeps them on it (they type it again).
        if (password is null)
        {
            Plugin.HomeTailnet.RemoveAll();
            await SweepTailnetAsync(client, CancellationToken.None).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>
    /// An account offered as a profile, or no longer (the devices that had it open lose it). An administrator's only with
    /// a PIN: the one it has, or the one sent with it.
    /// </summary>
    [HttpPut("profiles/{user}")]
    [Authorize(Policy = "RequiresElevation")]
    [RequestSizeLimit(1_024)]
    public async Task<IActionResult> Offer(string user, CancellationToken cancellationToken)
    {
        var body = await ReadAsync<OfferBody>(cancellationToken).ConfigureAwait(false);
        if (HomeFamily.Plain(user) is not { } userId || body?.Offered is not { } offered)
        {
            return BadRequest();
        }

        using var client = _clients.CreateClient();
        if (offered)
        {
            var (status, text) = await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Get, $"Users/{userId}", Admin(), null, cancellationToken).ConfigureAwait(false);
            var account = status == (int)HttpStatusCode.OK && GuestAccounts.Parse(text) is JsonObject found ? Account.Of(found) : null;
            if (account?.Id is null || Plugin.Invites.IsGuestAccount(account.Id))
            {
                return Refuse(StatusCodes.Status404NotFound, "Ce compte n’existe pas sur ce Jellyfin.");
            }

            if (body.Pin is { } wanted && !HomeFamily.IsPin(wanted))
            {
                return Refuse(StatusCodes.Status400BadRequest, "Le code fait quatre chiffres.");
            }

            if (account.Admin && body.Pin is null && !Home.HasPin(userId))
            {
                return Refuse(StatusCodes.Status409Conflict, "Un profil administrateur a besoin d’un code à quatre chiffres.");
            }
        }

        var (done, closed) = Home.Offer(userId, offered);
        if (!done)
        {
            return Refuse(StatusCodes.Status409Conflict, "Le foyer a déjà trop de profils.");
        }

        if (offered && body.Pin is { } pin)
        {
            Home.SetPin(userId, pin);
        }

        foreach (var device in closed)
        {
            await EndSessionsAsync(client, device, CancellationToken.None, Admin()).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>
    /// « Can download »: an account let use TorLink without administering Jellyfin, or no longer
    /// (<see cref="TorLinkAccessAttribute"/>). Never a guest's; an administrator always may.
    /// </summary>
    [HttpPut("profiles/{user}/download")]
    [Authorize(Policy = "RequiresElevation")]
    [RequestSizeLimit(1_024)]
    public async Task<IActionResult> Download(string user, CancellationToken cancellationToken)
    {
        var body = await ReadAsync<DownloadBody>(cancellationToken).ConfigureAwait(false);
        if (HomeFamily.Plain(user) is not { } userId || body?.Download is not { } allowed)
        {
            return BadRequest();
        }

        if (allowed)
        {
            using var client = _clients.CreateClient();
            var (status, text) = await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Get, $"Users/{userId}", Admin(), null, cancellationToken).ConfigureAwait(false);
            var account = status == (int)HttpStatusCode.OK && GuestAccounts.Parse(text) is JsonObject found ? Account.Of(found) : null;
            if (account?.Id is null || Plugin.Invites.IsGuestAccount(account.Id))
            {
                return Refuse(StatusCodes.Status404NotFound, "Ce compte n’existe pas sur ce Jellyfin.");
            }
        }

        return Home.SetDownload(userId, allowed) ? NoContent() : BadRequest();
    }

    /// <summary>A profile's PIN set or taken off by the administrator, without the current one.</summary>
    [HttpPut("profiles/{user}/pin")]
    [Authorize(Policy = "RequiresElevation")]
    [RequestSizeLimit(1_024)]
    public async Task<IActionResult> Pin(string user, CancellationToken cancellationToken)
    {
        var body = await ReadAsync<PinBody>(cancellationToken).ConfigureAwait(false);
        if (body?.Pin is { } wanted && !HomeFamily.IsPin(wanted))
        {
            return Refuse(StatusCodes.Status400BadRequest, "Le code fait quatre chiffres.");
        }

        if (body?.Pin is null && HomeFamily.Plain(user) is { } userId)
        {
            using var client = _clients.CreateClient();
            var (status, text) = await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Get, $"Users/{userId}", Admin(), null, cancellationToken).ConfigureAwait(false);
            if (status != (int)HttpStatusCode.OK || GuestAccounts.Parse(text) is not JsonObject found || Account.Of(found).Admin)
            {
                return Refuse(StatusCodes.Status409Conflict, "Un profil administrateur garde toujours son code.");
            }
        }

        return Home.SetPin(user, body?.Pin) ? NoContent() : Refuse(StatusCodes.Status404NotFound, "Ce profil n’est pas dans le foyer.");
    }

    /// <summary>A device out of the home: its token stops working, its profile's session ends.</summary>
    [HttpDelete("devices/{id}")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> RemoveDevice(string id, CancellationToken cancellationToken)
    {
        if (Home.Remove(id) is not { } device)
        {
            return NotFound();
        }

        using var client = _clients.CreateClient();
        await EndSessionsAsync(client, device, cancellationToken, Admin()).ConfigureAwait(false);
        await LeaveTailnetAsync(client, device, CancellationToken.None).ConfigureAwait(false);
        return NoContent();
    }

    // ---- Away from home: Tailscale (HomeTailnet) ----

    /// <summary>
    /// The home on the tailnet, as its administrator sees it: connected or not, this PC's Tailscale address, what to add
    /// to the policy, whether the policy limits the home's devices to Jellyfin, and the tailnet's devices of the home's
    /// tag. Nodes to delete are deleted now.
    /// </summary>
    [HttpGet("tailnet")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> Tailnet(CancellationToken cancellationToken)
    {
        var server = HomeTailnet.OwnAddress();
        var port = _host.HttpPort;
        var (tagOwners, grant) = HomeTailnet.Setup(server ?? "100.x.y.z", port);
        var setup = new { tag = HomeTailnet.Tag, tagOwners, grant, scopes = HomeTailnet.Scopes };
        if (Plugin.HomeTailnet.Client is not { } owner)
        {
            return Answer(StatusCodes.Status200OK, new { connected = false, server, port, setup, policy = (string?)null });
        }

        using var client = _clients.CreateClient();
        var api = new TailscaleApi(client, owner.Id, owner.Secret);
        string policy;
        IReadOnlyList<HomeTailnet.OpenRule> open = [];
        IReadOnlyList<TailnetDevice> nodes = [];
        HomePolicyFix.Plan? fix = null;
        var key = false;
        try
        {
            (policy, open, fix) = await PolicyAsync(api, server, port, cancellationToken).ConfigureAwait(false);
            nodes = await SweepAsync(api, cancellationToken).ConfigureAwait(false);
            // This PC's key, made to never expire by the same click: offered while Tailscale says it still expires.
            key = KeyToDisable(nodes, server) is not null;
            if (key && policy == "ok")
            {
                policy = "harden";
            }
        }
        catch (TailscaleApi.TailscaleException error)
        {
            policy = Failure(error);
        }

        var home = Home.Devices().Where(x => x.Tailnet is not null).ToDictionary(x => x.Tailnet!, StringComparer.OrdinalIgnoreCase);
        // Friends' devices are listed with each friend (FriendController), not as the home's.
        var friends = Plugin.Friends.Devices().Select(x => x.Tailnet).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var devices = nodes.Where(x => x.Tagged && !x.Friend && !x.Addresses.Any(friends.Contains)).Select(x =>
        {
            var inHome = x.Addresses.Select(a => home.GetValueOrDefault(a)).FirstOrDefault(d => d is not null);
            return new { id = x.Id, name = x.Name, addresses = x.Addresses, created = x.Created, lastSeen = x.LastSeen, device = inHome?.Id, deviceName = inHome?.Name };
        });
        return Answer(StatusCodes.Status200OK, new
        {
            connected = true,
            connectedAt = Plugin.HomeTailnet.Connected,
            clientId = Mask(owner.Id),
            server,
            port,
            setup,
            policy,
            open = open.Select(x => new { section = x.Section, number = x.Number, sources = x.Sources }),
            fix = fix is null && !key ? null : (fix?.Changes ?? []).Concat(key ? [new HomePolicyFix.Change("key")] : [])
                .Select(x => new { kind = x.Kind, section = x.Section, number = x.Number, sources = x.Sources }),
            broad = TailscaleApi.TooBroad(api.Scopes),
            missing = HomeTailnet.Missing(api.Scopes),
            tag = policy is not ("no-tag" or "refused" or "scope" or "unreachable"),
            lines = fix?.Lines,
            undoUntil = Plugin.HomeTailnet.Undo?.Until,
            devices,
        });
    }

    /// <summary>
    /// The tailnet's policy put right by Mira (<see cref="HomePolicyFix"/>), as the administrator saw it in the settings:
    /// written only if it has not changed since it was read, comments kept. Needs the OAuth client's policy_file scope.
    /// </summary>
    [HttpPost("tailnet/fix")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> FixPolicy(CancellationToken cancellationToken)
    {
        if (Plugin.HomeTailnet.Client is not { } owner)
        {
            return Answer(StatusCodes.Status409Conflict, new { error = Text("Relie d’abord le foyer à ton réseau Tailscale."), policy = "disconnected" });
        }

        if (HomeTailnet.OwnAddress() is not { } server)
        {
            return Answer(StatusCodes.Status409Conflict, new { error = Text("Tailscale n’est pas allumé sur le PC de Jellyfin."), policy = "no-tailscale" });
        }

        using var client = _clients.CreateClient();
        var api = new TailscaleApi(client, owner.Id, owner.Secret);
        try
        {
            var (text, version) = await api.PolicyTextAsync(cancellationToken).ConfigureAwait(false);
            var verdict = HomeTailnet.CheckPolicy(HomePolicyFix.Parse(text), server, _host.HttpPort).Verdict;
            var plan = HomePolicyFix.Make(text, server, _host.HttpPort, harden: true, funnel: !Plugin.HomeTailnet.FunnelRefused);
            if (plan is null && verdict != HomeTailnet.Verdict.Ok)
            {
                return Answer(StatusCodes.Status409Conflict, new { error = Text("Mira ne sait pas corriger ces règles seule : restreins-les dans la politique Tailscale."), policy = "too-open" });
            }

            if (plan is not null)
            {
                try
                {
                    await api.SetPolicyAsync(plan.Text, version, cancellationToken).ConfigureAwait(false);
                }
                catch (TailscaleApi.TailscaleException error) when (error.Failure == TailscaleApi.Failure.Invalid && plan.Changes.Any(x => x.Kind == "funnel")
                    && HomePolicyFix.Make(text, server, _host.HttpPort, harden: true, funnel: false) is var without)
                {
                    // Funnel's node attribute refused (this tailnet's plan, or a target Tailscale won't take): the rest still goes.
                    _logger.LogWarning("Mira: Tailscale refused Funnel's node attribute ({Message}); the policy is put right without it", error.Message);
                    Plugin.HomeTailnet.FunnelRefused = true;
                    plan = without;
                    if (plan is not null)
                    {
                        await api.SetPolicyAsync(plan.Text, version, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (plan is not null)
                {
                    Plugin.HomeTailnet.Fixed(text, plan.Text);
                    _logger.LogInformation("Mira: the tailnet's policy was put right for the home ({Changes})", string.Join(", ", plan.Changes.Select(x => x.Kind)));
                }
            }

            await DisableKeyExpiryAsync(api, server, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (TailscaleApi.TailscaleException error)
        {
            if (error.Failure == TailscaleApi.Failure.Invalid)
            {
                _logger.LogWarning("Mira: Tailscale refused the policy put right for the home: {Message}", error.Message);
            }

            var (policy, message) = error.Failure switch
            {
                TailscaleApi.Failure.Scope => ("no-write", "Le client OAuth du foyer ne peut pas écrire la politique : crée-en un avec le droit policy_file, puis relie de nouveau le foyer."),
                TailscaleApi.Failure.Changed => ("changed", "Ta politique Tailscale vient de changer : vérifie à nouveau, puis corrige."),
                TailscaleApi.Failure.Invalid => ("invalid", "Tailscale a refusé la politique corrigée : rien n’a changé. Corrige-la dans la console de Tailscale."),
                _ => (Failure(error), FailureText(error)),
            };
            return Answer(error.Failure == TailscaleApi.Failure.Unreachable ? StatusCodes.Status502BadGateway : StatusCodes.Status409Conflict, new { error = Text(message), policy });
        }
    }

    /// <summary>
    /// Mira's fix of the policy taken back, within <see cref="HomeTailnet.UndoFor"/>, and only while the policy is still
    /// exactly what Mira wrote (nobody changed it since): the policy as it was before.
    /// </summary>
    [HttpPost("tailnet/undo")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> UndoFix(CancellationToken cancellationToken)
    {
        if (Plugin.HomeTailnet.Client is not { } owner || Plugin.HomeTailnet.Undo is not { } undo)
        {
            return Answer(StatusCodes.Status409Conflict, new { error = Text("Il est trop tard pour annuler : change ta politique dans la console de Tailscale."), policy = "no-undo" });
        }

        using var client = _clients.CreateClient();
        var api = new TailscaleApi(client, owner.Id, owner.Secret);
        try
        {
            var (text, version) = await api.PolicyTextAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(text.Trim(), undo.Written.Trim(), StringComparison.Ordinal))
            {
                Plugin.HomeTailnet.Undone();
                return Answer(StatusCodes.Status409Conflict, new { error = Text("Ta politique a changé depuis : Mira ne l’annule pas. Change-la dans la console de Tailscale."), policy = "no-undo" });
            }

            await api.SetPolicyAsync(undo.Before, version, cancellationToken).ConfigureAwait(false);
            Plugin.HomeTailnet.Undone();
            _logger.LogInformation("Mira: the home's fix of the tailnet's policy was undone");
            return NoContent();
        }
        catch (TailscaleApi.TailscaleException error)
        {
            return Answer(error.Failure == TailscaleApi.Failure.Unreachable ? StatusCodes.Status502BadGateway : StatusCodes.Status409Conflict, new { error = Text(FailureText(error)), policy = Failure(error) });
        }
    }

    /// <summary>The home connected to the tailnet with an OAuth client (checked with Tailscale first), or disconnected (both null).</summary>
    [HttpPut("tailnet")]
    [Authorize(Policy = "RequiresElevation")]
    [RequestSizeLimit(2_048)]
    public async Task<IActionResult> SetTailnet(CancellationToken cancellationToken)
    {
        var body = await ReadAsync<TailnetBody>(cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return BadRequest();
        }

        if (string.IsNullOrWhiteSpace(body.ClientId) && string.IsNullOrWhiteSpace(body.ClientSecret))
        {
            Plugin.HomeTailnet.SetClient(null, null);
            return NoContent();
        }

        var secret = body.ClientSecret?.Trim();
        // One box: the secret carries the client's id.
        var id = string.IsNullOrWhiteSpace(body.ClientId) ? HomeTailnet.IdOfSecret(secret) : body.ClientId.Trim();
        if (!HomeTailnet.GoodClient(id, secret))
        {
            // A secret the id can't be read from: the client's ID asked too.
            return Answer(StatusCodes.Status400BadRequest, new { error = Text("Colle l’identifiant et le secret du client OAuth que Tailscale vient de te donner."), needsId = string.IsNullOrWhiteSpace(body.ClientId) && !string.IsNullOrWhiteSpace(secret) });
        }

        using var client = _clients.CreateClient();
        try
        {
            // A client that can do everything is refused: its secret stays on this server, in the clear.
            var granted = await new TailscaleApi(client, id!, secret!).CheckAsync(cancellationToken).ConfigureAwait(false);
            if (TailscaleApi.TooBroad(granted))
            {
                return Answer(StatusCodes.Status400BadRequest, new { error = Text("Ce client OAuth a plus de droits qu’il n’en faut : supprime-le, puis crée-en un avec seulement Policy File, Devices Core et Auth Keys (étiquette tag:mira-home)."), policy = "too-broad" });
            }

            // Tailscale can't change a client once made: one without a scope the home needs is of no use.
            if (HomeTailnet.Missing(granted) is { Count: > 0 } missing)
            {
                return Answer(StatusCodes.Status400BadRequest, new { error = Text("Il manque un droit à ce client OAuth : crée-en un autre avec Policy File, Devices Core et Auth Keys (étiquette tag:mira-home)."), policy = "missing", missing });
            }
        }
        catch (TailscaleApi.TailscaleException error)
        {
            return error.Failure == TailscaleApi.Failure.Refused
                ? Answer(StatusCodes.Status400BadRequest, new { error = Text("Tailscale refuse ce client OAuth : vérifie l’identifiant et le secret."), policy = "refused", needsId = string.IsNullOrWhiteSpace(body.ClientId) })
                : Refuse(StatusCodes.Status502BadGateway, "Tailscale n’a pas répondu. Réessaie dans un instant.");
        }

        Plugin.HomeTailnet.SetClient(id, secret);
        return NoContent();
    }

    /// <summary>
    /// A home link: a key of the home's tag for one device, that ends in <see cref="HomeTailnet.LinkLife"/>, and this
    /// PC's Tailscale address. Only when the tailnet's policy limits the home's devices to this Jellyfin.
    /// </summary>
    [HttpPost("link")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> Link(CancellationToken cancellationToken)
    {
        if (!Home.On)
        {
            return Refuse(StatusCodes.Status409Conflict, "Choisis d’abord le mot de passe du foyer.");
        }

        if (Plugin.HomeTailnet.Client is not { } owner)
        {
            return Answer(StatusCodes.Status409Conflict, new { error = Text("Relie d’abord le foyer à ton réseau Tailscale."), policy = "disconnected" });
        }

        if (HomeTailnet.OwnAddress() is not { } server)
        {
            return Answer(StatusCodes.Status409Conflict, new { error = Text("Tailscale n’est pas allumé sur le PC de Jellyfin."), policy = "no-tailscale" });
        }

        using var client = _clients.CreateClient();
        var api = new TailscaleApi(client, owner.Id, owner.Secret);
        try
        {
            var (policy, open, _) = await PolicyAsync(api, server, _host.HttpPort, cancellationToken).ConfigureAwait(false);
            if (policy is not ("ok" or "harden"))
            {
                return Answer(StatusCodes.Status409Conflict, new
                {
                    error = Text("Ta politique Tailscale laisse les appareils du foyer aller ailleurs que sur Jellyfin : corrige-la d’abord."),
                    policy,
                    open = open.Select(x => new { section = x.Section, number = x.Number, sources = x.Sources }),
                });
            }

            var (key, _, expires) = await api.CreateKeyAsync(HomeTailnet.LinkLife, cancellationToken).ConfigureAwait(false);
            // The short link (miracinema.app/join#…, ogsas 2026-11-20 « moins effrayant »), as a friend's: the apps read
            // the server's name and id once they reach it. An address it cannot hold (IPv6): the JSON link of before.
            var link = (System.Net.IPAddress.TryParse(server, out var address) ? Mira.Core.Family.JoinLink.Make(Mira.Core.Family.JoinLink.HomeKind, address, _host.HttpPort, Request.PathBase, key) : null)
                ?? HomeTailnet.Link(new HomeTailnet.LinkData(
                    1, _host.FriendlyName ?? "Jellyfin", _host.SystemId ?? "", $"http://{server}:{_host.HttpPort}{Request.PathBase}", key, expires.ToUnixTimeSeconds()));
            Response.Headers.CacheControl = "no-store";
            return Ok(new { link, expires });
        }
        catch (TailscaleApi.TailscaleException error)
        {
            return Answer(StatusCodes.Status502BadGateway, new { error = Text(FailureText(error)), policy = Failure(error) });
        }
    }

    // ---- Shared ----

    /// <summary>A device out of the home leaves the tailnet too, when it joined through it: now, or at the next call that reaches Tailscale.</summary>
    private async Task LeaveTailnetAsync(HttpClient client, HomeDevice device, CancellationToken cancellationToken)
    {
        if (device.Tailnet is not { } address)
        {
            return;
        }

        Plugin.HomeTailnet.Remove(address);
        await SweepTailnetAsync(client, cancellationToken).ConfigureAwait(false);
    }

    private async Task SweepTailnetAsync(HttpClient client, CancellationToken cancellationToken)
    {
        if (Plugin.HomeTailnet.Client is not { } owner)
        {
            return;
        }

        try
        {
            await SweepAsync(new TailscaleApi(client, owner.Id, owner.Secret), cancellationToken).ConfigureAwait(false);
        }
        catch (TailscaleApi.TailscaleException error)
        {
            _logger.LogWarning("Mira: a device of the home could not be deleted from the tailnet yet ({Failure})", error.Failure);
        }
    }

    /// <summary>The nodes of the home's tag to delete deleted (<see cref="HomeTailnet.ToDelete"/>); the tailnet's devices that remain.</summary>
    internal static async Task<IReadOnlyList<TailnetDevice>> SweepAsync(TailscaleApi api, CancellationToken cancellationToken)
    {
        var tailnet = Plugin.HomeTailnet;
        var nodes = await api.DevicesAsync(cancellationToken).ConfigureAwait(false);
        var gone = new List<TailnetDevice>();
        // Friends' devices (FriendInvites) joined too: their nodes stay.
        var joined = Home.Devices().Select(x => x.Tailnet).Concat(Plugin.Friends.Devices().Select(x => x.Tailnet)).OfType<string>();
        foreach (var node in tailnet.ToDelete(nodes, joined))
        {
            if (await api.DeleteAsync(node, cancellationToken).ConfigureAwait(false))
            {
                gone.Add(node);
            }
        }

        var left = nodes.Except(gone).ToList();
        var (leaving, all) = tailnet.Leaving();
        // An address no node of the tag has any more is done with; all of them when none is left.
        tailnet.Left(leaving.Where(a => !left.Any(n => n.Tagged && n.Addresses.Contains(a, StringComparer.OrdinalIgnoreCase))), all && !left.Any(n => n.Tagged));
        return left;
    }

    /// <summary>This PC's node when Tailscale says its key still expires (and has not refused to change it): null otherwise.</summary>
    private static TailnetDevice? KeyToDisable(IEnumerable<TailnetDevice> nodes, string? server) =>
        server is null || Plugin.HomeTailnet.KeyRefused ? null
            : nodes.FirstOrDefault(x => x.KeyExpiryDisabled == false && x.Addresses.Contains(server, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// This PC's key made to never expire, with « Make the changes »: without it, Jellyfin's PC leaves the tailnet after
    /// Tailscale's 180 days, and every home device and friend with it. A refusal is logged, never the fix's failure.
    /// </summary>
    private async Task DisableKeyExpiryAsync(TailscaleApi api, string server, CancellationToken cancellationToken)
    {
        try
        {
            if (KeyToDisable(await api.DevicesAsync(cancellationToken).ConfigureAwait(false), server) is { } self)
            {
                await api.DisableKeyExpiryAsync(self.Id, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Mira: this PC's Tailscale key no longer expires");
            }
        }
        catch (TailscaleApi.TailscaleException error) when (error.Failure is not TailscaleApi.Failure.Unreachable)
        {
            Plugin.HomeTailnet.KeyRefused = true;
            _logger.LogWarning("Mira: Tailscale refused to stop this PC's key from expiring ({Failure}: {Message})", error.Failure, error.Message);
        }
        catch (TailscaleApi.TailscaleException error)
        {
            _logger.LogWarning("Mira: this PC's Tailscale key could not be changed yet ({Message})", error.Message);
        }
    }

    private static async Task<(string Policy, IReadOnlyList<HomeTailnet.OpenRule> Open, HomePolicyFix.Plan? Fix)> PolicyAsync(TailscaleApi api, string? server, int port, CancellationToken cancellationToken)
    {
        if (server is null)
        {
            return ("no-tailscale", [], null);
        }

        // As the administrator wrote it: what Mira would change is planned on the same text.
        var (text, _) = await api.PolicyTextAsync(cancellationToken).ConfigureAwait(false);
        var (verdict, open) = HomeTailnet.CheckPolicy(HomePolicyFix.Parse(text), server, port);
        // Hardened (« Make the changes »): also the people this PC is shared with, and Funnel; « harden » when only those are left.
        var fix = HomePolicyFix.Make(text, server, port, harden: true, funnel: !Plugin.HomeTailnet.FunnelRefused);
        return (verdict switch
        {
            HomeTailnet.Verdict.Ok when fix is not null => "harden",
            HomeTailnet.Verdict.Ok => "ok",
            HomeTailnet.Verdict.NoTag => "no-tag",
            HomeTailnet.Verdict.TooOpen => "too-open",
            _ => "no-access",
        }, open, fix);
    }

    private static string Failure(TailscaleApi.TailscaleException error) => error.Failure switch
    {
        TailscaleApi.Failure.Refused => "refused",
        TailscaleApi.Failure.Scope => "scope",
        _ => "unreachable",
    };

    private static string FailureText(TailscaleApi.TailscaleException error) => error.Failure switch
    {
        TailscaleApi.Failure.Refused => "Tailscale refuse le client OAuth du foyer : il a été supprimé ou révoqué. Relie de nouveau le foyer.",
        TailscaleApi.Failure.Scope => "Le client OAuth du foyer n’a pas les droits qu’il faut (auth_keys, devices:core, policy_file et l’étiquette tag:mira-home).",
        _ => "Tailscale n’a pas répondu. Réessaie dans un instant.",
    };

    private static string Mask(string id) => id.Length <= 6 ? id : id[..6] + "…";

    /// <summary>The device of this request's home token, or null.</summary>
    private HomeDevice? Device() =>
        Request.Headers.TryGetValue(TokenHeader, out var token) ? Home.ByToken(token.ToString()) : null;

    /// <summary>
    /// Every Jellyfin session under the device id of a device of the home ended (Jellyfin's « Devices » removes them
    /// all), with the administrator's session or the plugin's API key.
    /// </summary>
    private async Task EndSessionsAsync(HttpClient client, HomeDevice device, CancellationToken cancellationToken, IReadOnlyList<(string, string)>? admin = null)
    {
        var session = admin ?? (Plugin.Invites.Key is { } key ? GuestAccounts.WithKey(key) : null);
        if (session is null)
        {
            return;
        }

        // Only a device Jellyfin knows: it refuses an unknown one with a 404 (10.9) or a 400 (12.1).
        var id = Uri.EscapeDataString(HomeFamily.JellyfinDevice(device));
        var (known, _) = await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Get, $"Devices/Info?id={id}", session, null, cancellationToken).ConfigureAwait(false);
        if (known != (int)HttpStatusCode.OK)
        {
            return;
        }

        var (status, _) = await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Delete, $"Devices?id={id}", session, null, cancellationToken).ConfigureAwait(false);
        if (status is not ((int)HttpStatusCode.NoContent or (int)HttpStatusCode.OK))
        {
            _logger.LogWarning("Mira: Jellyfin did not end the session of a device of the home ({Status})", status);
        }
    }

    /// <summary>The profiles offered that Jellyfin still has, neither administrator nor disabled; null without the plugin's API key.</summary>
    private async Task<List<object>?> FamilyProfilesAsync(HttpClient client, CancellationToken cancellationToken)
    {
        if (Plugin.Invites.Key is not { } key)
        {
            return null;
        }

        var (status, text) = await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Get, "Users", GuestAccounts.WithKey(key), null, cancellationToken).ConfigureAwait(false);
        if (status != (int)HttpStatusCode.OK || GuestAccounts.Parse(text) is not JsonArray users)
        {
            return null;
        }

        var accounts = users.OfType<JsonObject>().Select(Account.Of).Where(x => x.Id is not null).ToDictionary(x => x.Id!, StringComparer.Ordinal);
        return Home.Profiles()
            .Select(p => (Profile: p, Account: accounts.GetValueOrDefault(p.User)))
            .Where(x => x.Account is { Disabled: false } && (!x.Account.Admin || x.Profile.Pin is not null))
            .OrderBy(x => x.Account!.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => (object)new { id = x.Profile.User, name = x.Account!.Name, imageTag = x.Account.ImageTag, pin = x.Profile.Pin is not null })
            .ToList();
    }

    /// <summary>One account read with the plugin's API key: null without the key or an answer.</summary>
    private async Task<Account?> AccountAsync(HttpClient client, string userId, CancellationToken cancellationToken)
    {
        if (Plugin.Invites.Key is not { } key)
        {
            return null;
        }

        var (status, text) = await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Get, $"Users/{userId}", GuestAccounts.WithKey(key), null, cancellationToken).ConfigureAwait(false);
        return status switch
        {
            (int)HttpStatusCode.OK when GuestAccounts.Parse(text) is JsonObject found => Account.Of(found),
            (int)HttpStatusCode.NotFound => new Account(null, "", null, false, false, true, null) { Found = false },
            _ => null,
        };
    }

    private IReadOnlyList<(string, string)> Admin() => GuestAccounts.SessionOf(Request);

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

    private string ClientAddress()
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        return remote?.ToString() ?? "";
    }

    private static bool DeviceRule(string device) =>
        device.Length is >= 8 and <= 128 && device.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>The device's name as its app gives it: printable, at most 60 characters.</summary>
    private string DeviceName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? (French ? "Appareil" : "Device") : clean[..Math.Min(clean.Length, 60)];
    }

    private static string Short(string? text, string fallback)
    {
        var clean = new string((text ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '-' or '_').ToArray()).Trim();
        return clean.Length == 0 ? fallback : clean[..Math.Min(clean.Length, 40)];
    }

    private string? Text(string french) => RelayText.For(french, French);

    private ObjectResult Answer(int status, object body)
    {
        Response.Headers.CacheControl = "no-store";
        return StatusCode(status, body);
    }

    private ObjectResult Refuse(int status, string error) => Answer(status, new { error = Text(error) });

    private ObjectResult OutOfHome() => Refuse(StatusCodes.Status401Unauthorized, "Cet appareil n’est plus dans le foyer : entre de nouveau son mot de passe.");

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    private sealed record JoinBody(string? Password, string? Device, string? Name);

    private sealed record OpenBody(string? Profile, string? Pin, string? Client, string? Version);

    private sealed record PinBody(string? Pin, string? Current);

    private sealed record PasswordBody(string? Password);

    private sealed record OfferBody(bool? Offered, string? Pin);

    private sealed record DownloadBody(bool? Download);

    private sealed record TailnetBody(string? ClientId, string? ClientSecret);

    /// <summary>What the home reads of a Jellyfin account (its UserDto).</summary>
    private sealed record Account(string? Id, string Name, string? ImageTag, bool Admin, bool Disabled, bool AllLibraries, string? MaxRating)
    {
        public bool Found { get; init; } = true;

        public static Account Of(JsonObject user)
        {
            var policy = user["Policy"] as JsonObject;
            static bool Flag(JsonNode? value) => value is JsonValue v && v.TryGetValue<bool>(out var flag) && flag;
            var rating = policy?["MaxParentalRating"] is JsonValue r ? r.ToString() : null;
            return new Account(HomeFamily.Plain(GuestAccounts.Text(user["Id"])), GuestAccounts.Text(user["Name"]) ?? "", GuestAccounts.Text(user["PrimaryImageTag"]),
                Flag(policy?["IsAdministrator"]), Flag(policy?["IsDisabled"]), policy?["EnableAllFolders"] is not JsonValue all || Flag(all), rating);
        }
    }
}
