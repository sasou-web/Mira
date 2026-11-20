using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mira.Jellyfin;

/// <summary>
/// « Inviter par lien », the host's side, for Jellyfin's administrators only (making an account takes one): the links
/// made, a new one with its account, one taken back with its account, and the address the links start with, tried as
/// a visitor from the Internet would (<see cref="TunnelCheck"/>): no link where Jellyfin answers outside Mira. Every
/// change on Jellyfin goes through its own API, on its loopback address, with the administrator's own session.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Mira/invites")]
public sealed class InvitesController : ControllerBase
{
    // The address tried again after this long when only Mira answered there, after this long otherwise (Funnel's
    // certificate on its way, Funnel opened again); one try at a time.
    private static readonly TimeSpan TunnelFresh = TimeSpan.FromHours(6), TunnelRetry = TimeSpan.FromMinutes(1);
    private static readonly SemaphoreSlim Trying = new(1, 1);
    private readonly IHttpClientFactory _clients;
    private readonly IServerApplicationHost _host;

    public InvitesController(IHttpClientFactory clients, IServerApplicationHost host)
    {
        _clients = clients;
        _host = host;
    }

    private bool French => RelayText.French(Request);
    private IEnumerable<string> Roots() => JellyfinLoopback.Roots(_host, Request.PathBase);
    private static GuestInvites Invites => Plugin.Invites;

    /// <summary>The links, after those gone unused were taken away with their accounts.</summary>
    [HttpGet("")]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        using var client = _clients.CreateClient();
        await GuestCleanup.SweepAsync(client, Roots(), Admin(), cancellationToken).ConfigureAwait(false);
        // Said with the list as it was last tried; tried again meanwhile, when due, for the next look.
        var roots = Roots().ToList();
        var address = Invites.Address;
        _ = Task.Run(() => TunnelAsync(roots, address, TunnelFresh, TunnelRetry, CancellationToken.None), CancellationToken.None);
        return Answer(Listing());
    }

    /// <summary>A new link: its account made on Jellyfin (hidden, its policy a guest's), then kept here.</summary>
    [HttpPost("")]
    [RequestSizeLimit(4_096)]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var body = await ReadAsync<CreateBody>(cancellationToken).ConfigureAwait(false);
        if (Invites.Count >= GuestInvites.MaxInvites)
        {
            return Refuse(StatusCodes.Status409Conflict, "Trop de liens en cours : retire ceux qui ne servent plus.");
        }

        // Never a link where the tunnel lets Jellyfin through: tried again unless only Mira answered there 10 minutes ago.
        var tunnel = await TunnelAsync(Roots().ToList(), Invites.Address, TimeSpan.FromMinutes(10), TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        if (tunnel?.State == TunnelCheck.State.Open)
        {
            return Refuse(StatusCodes.Status409Conflict, TunnelCheck.Text(tunnel, French));
        }

        var admin = Admin();
        using var client = _clients.CreateClient();
        var (meStatus, meText) = await Call(client, HttpMethod.Get, "Users/Me", admin, null, cancellationToken).ConfigureAwait(false);
        var me = meStatus == (int)HttpStatusCode.OK ? Parse(meText) : null;
        if (me?["Id"] is null)
        {
            return Refuse(StatusCodes.Status502BadGateway, "Jellyfin n’a pas répondu. Réessaie dans un instant.");
        }

        var (made, userId, name) = await GuestAccounts.CreateAsync(client, Roots(), admin, body?.Name, French ? "Invité" : "Guest", cancellationToken).ConfigureAwait(false);
        switch (made)
        {
            case GuestAccounts.Made.NoAnswer:
                return Refuse(StatusCodes.Status502BadGateway, "Jellyfin n’a pas répondu. Réessaie dans un instant.");
            case GuestAccounts.Made.BadName:
                return Refuse(StatusCodes.Status400BadRequest, "Son prénom ne peut contenir que des lettres, des chiffres, des espaces et ces signes : - _ ' . @ +");
            case GuestAccounts.Made.Failed:
                return Refuse(StatusCodes.Status502BadGateway, "Jellyfin n’a pas créé le compte de l’invité.");
        }

        await GuestAccounts.KeepKeyAsync(client, Roots(), admin, cancellationToken).ConfigureAwait(false);
        var invite = Invites.Add(name!, userId!, GuestAccounts.Text(me["Id"])!, GuestAccounts.Text(me["Name"]) ?? "");
        return Answer(View(invite, Invites.Address));
    }

    /// <summary>A link taken back: it stops at once, and its account is deleted from Jellyfin, with its sessions.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Remove(string id, CancellationToken cancellationToken)
    {
        var invite = Invites.Remove(id);
        if (invite is null)
        {
            return NotFound();
        }

        using var client = _clients.CreateClient();
        await GuestCleanup.SweepAsync(client, Roots(), Admin(), cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>The address the links start with (« https://pc.tailnet.ts.net »), or none (null): tried at once.</summary>
    [HttpPut("address")]
    [RequestSizeLimit(4_096)]
    public async Task<IActionResult> Address(CancellationToken cancellationToken)
    {
        var body = await ReadAsync<AddressBody>(cancellationToken).ConfigureAwait(false);
        var address = string.IsNullOrWhiteSpace(body?.Address) ? null : body.Address;
        if (!Invites.SetAddress(address))
        {
            return Refuse(StatusCodes.Status400BadRequest, "L’adresse doit commencer par https:// et finir par le nom de l’ordinateur.");
        }

        await TunnelAsync(Roots().ToList(), Invites.Address, TimeSpan.Zero, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        return Answer(Listing());
    }

    private object Listing() => new
    {
        address = Invites.Address,
        invites = Invites.All().Select(x => View(x, Invites.Address)),
        tunnel = TunnelCheck.Last(Invites.Address) is { } tunnel
            ? new { state = tunnel.State.ToString().ToLowerInvariant(), at = tunnel.At, text = TunnelCheck.Text(tunnel, French) }
            : null,
    };

    private static object View(GuestInvite invite, string? address) => new
    {
        id = invite.Id, name = invite.Name, created = invite.Created, used = invite.Used, ends = invite.Used + GuestInvites.Unused,
        opened = invite.Device is not null, device = invite.DeviceName, link = invite.Party is null ? GuestInvites.Link(address, invite.Code) : null,
        party = invite.Party is not null,
    };

    /// <summary>
    /// The address of the links tried as a visitor from the Internet would (<see cref="TunnelCheck"/>), unless its last
    /// try still holds (<see cref="TunnelCheck.Stale"/>): what it gave. Null without an address, or before any try when
    /// this Jellyfin's own card cannot be read.
    /// </summary>
    internal static async Task<TunnelCheck.Result?> TunnelAsync(IReadOnlyList<string> roots, string? address, TimeSpan fresh, TimeSpan retry, CancellationToken cancellationToken)
    {
        if (address is null || !TunnelCheck.Stale(TunnelCheck.Last(address), DateTimeOffset.UtcNow, fresh, retry))
        {
            return TunnelCheck.Last(address);
        }

        await Trying.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Tried meanwhile by another request.
            var last = TunnelCheck.Last(address);
            if (!TunnelCheck.Stale(last, DateTimeOffset.UtcNow, fresh, retry))
            {
                return last;
            }

            // A redirection is no answer from this server: never followed.
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
            var (status, text) = await JellyfinLoopback.CallAsync(client, roots, HttpMethod.Get, "System/Info/Public", [], null, cancellationToken).ConfigureAwait(false);
            if ((status == (int)HttpStatusCode.OK ? TunnelCheck.ServerId(text) : null) is not { } id)
            {
                return last;
            }

            var result = await TunnelCheck.RunAsync(client, address, id, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            TunnelCheck.Keep(result);
            return result;
        }
        finally
        {
            Trying.Release();
        }
    }

    /// <summary>The administrator's own session, as this request carried it: what the plugin's calls to Jellyfin use.</summary>
    private IReadOnlyList<(string, string)> Admin() => GuestAccounts.SessionOf(Request);

    private Task<(int Status, string Text)> Call(HttpClient client, HttpMethod method, string path, IEnumerable<(string, string)> headers, string? json, CancellationToken cancellationToken) =>
        JellyfinLoopback.CallAsync(client, Roots(), method, path, headers, json, cancellationToken);

    private static JsonNode? Parse(string text) => GuestAccounts.Parse(text);

    private async Task<T?> ReadAsync<T>(CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(Request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private ContentResult Answer(object value)
    {
        Response.Headers.CacheControl = "no-store";
        return Content(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "application/json; charset=utf-8");
    }

    private ObjectResult Refuse(int status, string error)
    {
        Response.Headers.CacheControl = "no-store";
        return StatusCode(status, new { error = RelayText.For(error, French) });
    }

    private sealed record CreateBody(string? Name);
    private sealed record AddressBody(string? Address);
}

/// <summary>
/// The accounts of links taken back or gone unused, deleted from Jellyfin: with an administrator's session when one is
/// there (their requests), otherwise with the plugin's API key (<see cref="GuestCleanupService"/>, from Jellyfin's
/// start). An account Jellyfin has, marked as a guest's, that no link holds goes too (its link lost with an unreadable
/// file). Never an administrator's account, whatever the links say.
/// </summary>
internal static class GuestCleanup
{
    private static int _running;

    /// <summary>Deletes each account still to delete; one Jellyfin no longer has counts as deleted.</summary>
    public static async Task SweepAsync(HttpClient client, IEnumerable<string> roots, IReadOnlyList<(string, string)> admin, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            var (usersStatus, usersText) = await JellyfinLoopback.CallAsync(client, roots, HttpMethod.Get, "Users", admin, null, cancellationToken).ConfigureAwait(false);
            if (usersStatus != (int)HttpStatusCode.OK || GuestAccounts.Parse(usersText) is not JsonArray users)
            {
                return;
            }

            var byId = users.OfType<JsonNode>().Where(x => GuestAccounts.Text(x["Id"]) is not null)
                .ToDictionary(x => GuestAccounts.Text(x["Id"])!.Replace("-", "", StringComparison.Ordinal), StringComparer.OrdinalIgnoreCase);
            Plugin.Invites.Orphans(byId.Values.Where(GuestRoutes.IsGuestAccount).Select(x => GuestAccounts.Text(x["Id"])!));
            foreach (var userId in Plugin.Invites.Sweep())
            {
                if (!byId.TryGetValue(userId.Replace("-", "", StringComparison.Ordinal), out var user) || GuestRoutes.IsAdministrator(user))
                {
                    Plugin.Invites.Deleted(userId);
                    continue;
                }

                var (status, _) = await JellyfinLoopback.CallAsync(client, roots, HttpMethod.Delete, $"Users/{userId}", admin, null, cancellationToken).ConfigureAwait(false);
                if (status is (int)HttpStatusCode.NoContent or (int)HttpStatusCode.OK or (int)HttpStatusCode.NotFound)
                {
                    Plugin.Invites.Deleted(userId);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}

/// <summary>
/// The guests' accounts swept with the plugin's API key a minute after Jellyfin starts, then every half hour, whether
/// or not anyone asks anything of the guests (<see cref="GuestCleanup"/>).
/// </summary>
internal sealed class GuestCleanupService : BackgroundService
{
    private static readonly TimeSpan First = TimeSpan.FromMinutes(1), Every = TimeSpan.FromMinutes(30);
    private readonly IHttpClientFactory _clients;
    private readonly IServerApplicationHost _host;
    private readonly IConfigurationManager _config;
    private readonly ILogger<GuestCleanupService> _logger;

    public GuestCleanupService(IHttpClientFactory clients, IServerApplicationHost host, IConfigurationManager config, ILogger<GuestCleanupService> logger)
    {
        _clients = clients;
        _host = host;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(First, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Every, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Jellyfin stopping.
        }
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        if (!Plugin.InvitesReady || Plugin.Invites.Key is not { } key)
        {
            return;
        }

        try
        {
            using var client = _clients.CreateClient();
            await GuestCleanup.SweepAsync(client, JellyfinLoopback.Roots(_host, JellyfinLoopback.BaseUrl(_config)), GuestAccounts.WithKey(key), stoppingToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException || (error is OperationCanceledException && !stoppingToken.IsCancellationRequested))
        {
            // Jellyfin out of reach: the next half hour.
            _logger.LogDebug(error, "Mira: the guests' accounts were not swept");
        }
    }
}
