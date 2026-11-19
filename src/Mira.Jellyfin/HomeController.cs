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
/// and libraries stay Jellyfin's. An administrator's account is never offered nor opened by the home. The
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

        if (!account.Found || account.Admin || account.Disabled)
        {
            return Refuse(StatusCodes.Status404NotFound, "Ce profil n’est pas dans le foyer.");
        }

        var (pin, left, wait) = Home.CheckPin(userId, body!.Pin);
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
                offered = !x.Admin && offered.ContainsKey(x.Id!),
                pin = offered.TryGetValue(x.Id!, out var p) && p.Pin is not null,
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

        return NoContent();
    }

    /// <summary>An account offered as a profile, or no longer (the devices that had it open lose it). Never an administrator's.</summary>
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

            if (account.Admin)
            {
                return Refuse(StatusCodes.Status409Conflict, "Un compte administrateur n’est jamais ouvert par le mot de passe du foyer.");
            }
        }

        var (done, closed) = Home.Offer(userId, offered);
        if (!done)
        {
            return Refuse(StatusCodes.Status409Conflict, "Le foyer a déjà trop de profils.");
        }

        foreach (var device in closed)
        {
            await EndSessionsAsync(client, device, CancellationToken.None, Admin()).ConfigureAwait(false);
        }

        return NoContent();
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
        return NoContent();
    }

    // ---- Shared ----

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
            .Where(x => x.Account is { Admin: false, Disabled: false })
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

    private sealed record OfferBody(bool? Offered);

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
