using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Mira.Jellyfin;

/// <summary>
/// « Ta party », the one link of « Regarder ensemble »: each account has its own, the same from one evening to the next,
/// which anyone opens. Its page (public, through Tailscale Funnel too) shows chat apps who invites and what they watch,
/// and offers Mira's apps (mira://party/…) or Mira web, where a guest of its own is made for each device
/// (<see cref="GuestController"/>). The host's own calls (their link, a new one) need their session.
/// </summary>
[ApiController]
[Route("Mira/party")]
public sealed class PartyController : ControllerBase
{
    private readonly IHttpClientFactory _clients;
    private readonly IServerApplicationHost _host;

    public PartyController(IHttpClientFactory clients, IServerApplicationHost host)
    {
        _clients = clients;
        _host = host;
    }

    private bool French => RelayText.French(Request);
    private IEnumerable<string> Roots() => JellyfinLoopback.Roots(_host, Request.PathBase);
    private static GuestInvites Invites => Plugin.Invites;

    /// <summary>This account's party link, made the first time, with where it works and who it let in.</summary>
    [HttpGet("")]
    [Authorize]
    public Task<IActionResult> Mine(CancellationToken cancellationToken) => HostAsync(false, cancellationToken);

    /// <summary>This account's party link made again: the old one stops, its guests go.</summary>
    [HttpPost("new")]
    [Authorize]
    public Task<IActionResult> New(CancellationToken cancellationToken) => HostAsync(true, cancellationToken);

    private async Task<IActionResult> HostAsync(bool again, CancellationToken cancellationToken)
    {
        GuestCleanup.Wake(_clients, _host, Request.PathBase);
        var session = GuestAccounts.SessionOf(Request);
        using var client = _clients.CreateClient();
        var (status, text) = await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Get, "Users/Me", session, null, cancellationToken).ConfigureAwait(false);
        var me = status == (int)HttpStatusCode.OK ? GuestAccounts.Parse(text) : null;
        if (GuestAccounts.Text(me?["Id"]) is not { } id)
        {
            return status == (int)HttpStatusCode.Unauthorized ? Unauthorized() : Refuse(StatusCodes.Status502BadGateway, "Jellyfin n’a pas répondu. Réessaie dans un instant.");
        }

        if (Invites.All().Any(x => SameId(x.UserId, id)))
        {
            return Refuse(StatusCodes.Status403Forbidden, "Un invité ne peut pas inviter.");
        }

        if (me?["Policy"]?["IsAdministrator"]?.GetValue<bool>() == true)
        {
            await GuestAccounts.KeepKeyAsync(client, Roots(), session, cancellationToken).ConfigureAwait(false);
        }

        if (Invites.Key is null)
        {
            return Refuse(StatusCodes.Status409Conflict, "Un administrateur de ce Jellyfin doit d’abord ouvrir Regarder ensemble dans Mira une fois.");
        }

        var name = GuestAccounts.Text(me?["Name"]) ?? "";
        var party = again ? Invites.NewParty(id, name) : Invites.PartyOf(id, name);
        if (again)
        {
            await GuestCleanup.SweepAsync(client, Roots(), GuestAccounts.WithKey(Invites.Key), cancellationToken).ConfigureAwait(false);
        }

        // Through Funnel when its address is set and lets only Mira through; else at this computer's own address.
        var address = Invites.Address;
        var tunnel = await InvitesController.TunnelAsync(Roots().ToList(), address, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        string origin, reach;
        if (address is not null && tunnel?.State is not TunnelCheck.State.Open)
        {
            (origin, reach) = (address, "internet");
        }
        else if (PartyPage.TailnetAddress(OwnAddresses()) is { } tailnet)
        {
            (origin, reach) = ($"http://{tailnet}:{_host.HttpPort}{Request.PathBase}", "tailnet");
        }
        else
        {
            (origin, reach) = (Home(), "home");
        }

        var note = reach switch
        {
            "internet" => "Ce lien ouvre ta party depuis Internet, pour tout le monde.",
            "tailnet" => "Ce lien ne marche que pour ceux qui ont Tailscale sur ton réseau : ouvre Tailscale Funnel sur le PC de Jellyfin (Mira pour Windows le fait) pour qu’il marche partout.",
            _ => "Ce lien ne marche qu’à la maison : ouvre Tailscale Funnel sur le PC de Jellyfin (Mira pour Windows le fait) pour qu’il marche partout.",
        };
        return Answer(new
        {
            code = party.Code,
            link = PartyPage.Link(origin, party.Code),
            appLink = PartyPage.AppLink(origin, party.Code, _host.SystemId),
            reach,
            note = RelayText.For(note, French),
            tunnel = tunnel is null ? null : new { state = tunnel.State.ToString().ToLowerInvariant(), text = TunnelCheck.Text(tunnel, French) },
            guests = Invites.Guests(party.Code).Select(x => new { id = x.Id, name = x.Name, device = x.DeviceName, used = x.Used }),
        });
    }

    /// <summary>
    /// The page of a party link, for anyone who has it: who invites and what they watch (Open Graph, for the card of a
    /// chat app), then « Open in Mira » or Mira web. JSON for Mira's apps (Accept: application/json).
    /// </summary>
    [HttpGet("{code}")]
    [AllowAnonymous]
    public async Task<IActionResult> Page(string code, CancellationToken cancellationToken)
    {
        var party = Invites.Party(code);
        var watching = party is null ? null : await WatchingAsync(party, cancellationToken).ConfigureAwait(false);
        Response.Headers.CacheControl = "no-store";
        if (Request.Headers.Accept.Any(x => x?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true))
        {
            return party is null ? NotFound() : Answer(new { host = party.HostName, serverId = _host.SystemId, watching = watching?.Title });
        }

        var files = WebFiles.Current;
        if (!files.TryGet("party.html", out var file))
        {
            return NotFound();
        }

        var origin = Origin();
        var image = party is not null && watching is not null ? $"{origin}/Mira/party/{party.Code}/image" : $"{origin}/Mira/v/{files.Revision}/img/apple-touch-icon.png";
        var html = PartyPage.Render(Encoding.UTF8.GetString(file.Content), new Dictionary<string, string?>
        {
            ["title"] = party is null ? "Mira" : PartyPage.Title,
            ["description"] = party is null ? "This party link no longer works." : PartyPage.Description(party.HostName, watching),
            ["image"] = image,
            // The page's own picture from its own address: Funnel's or not, never another origin than the page's.
            ["picture"] = new Uri(image).PathAndQuery,
            ["url"] = party is null ? "" : PartyPage.Link(origin, party.Code),
            ["host"] = party?.HostName,
            ["watching"] = watching?.Title,
            ["app"] = party is null ? "" : PartyPage.AppLink(origin, party.Code, _host.SystemId),
            ["web"] = party is null ? "" : $"{origin}/Mira/#/party/{party.Code}",
            ["state"] = party is null ? "gone" : "open",
        });
        Response.Headers.ContentSecurityPolicy = ContentPolicy.For(html);
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.StatusCode = party is null ? StatusCodes.Status404NotFound : StatusCodes.Status200OK;
        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>The poster of what the host is watching, for the card; 404 when they watch nothing.</summary>
    [HttpGet("{code}/image")]
    [AllowAnonymous]
    public async Task<IActionResult> Image(string code, CancellationToken cancellationToken)
    {
        if (Invites.Party(code) is not { } party || await WatchingAsync(party, cancellationToken).ConfigureAwait(false) is not { } watching)
        {
            return NotFound();
        }

        using var client = _clients.CreateClient();
        foreach (var root in Roots())
        {
            try
            {
                using var response = await client.GetAsync(new Uri($"{root}Items/{watching.ImageItemId}/Images/Primary?maxWidth=600&quality=85"), cancellationToken).ConfigureAwait(false);
                var type = response.Content.Headers.ContentType?.MediaType;
                if (!response.IsSuccessStatusCode || type is null || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    return NotFound();
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                Response.Headers.CacheControl = "public, max-age=300";
                Response.Headers.XContentTypeOptions = "nosniff";
                return File(bytes, type);
            }
            catch (HttpRequestException)
            {
                // Not listening there: the next address.
            }
        }

        return NotFound();
    }

    /// <summary>What the host is watching now, read with the plugin's key (no session of theirs is at hand).</summary>
    private async Task<PartyPage.Watching?> WatchingAsync(GuestParty party, CancellationToken cancellationToken)
    {
        if (Invites.Key is not { } key)
        {
            return null;
        }

        using var client = _clients.CreateClient();
        var (status, text) = await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Get, "Sessions?activeWithinSeconds=600", GuestAccounts.WithKey(key), null, cancellationToken, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return status == (int)HttpStatusCode.OK ? PartyPage.WatchingOf(GuestAccounts.Parse(text), party.HostId) : null;
    }

    /// <summary>
    /// Where this page was opened: Funnel's address when the request came through it (Funnel speaks to Jellyfin in
    /// plain HTTP on this computer), else the address the request names.
    /// </summary>
    private string Origin() => Request.Headers.ContainsKey(GuestRoutes.FunnelHeader) && Invites.Address is { } address
        ? address
        : $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

    /// <summary>This computer's address on its own network, as Jellyfin gives it.</summary>
    private string Home()
    {
        try
        {
            if (_host.GetApiUrlForLocalAccess(null, false) is { Length: > 0 } local)
            {
                return local.TrimEnd('/');
            }
        }
        catch (InvalidOperationException)
        {
            // No address for its own network.
        }

        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
    }

    private static IEnumerable<IPAddress> OwnAddresses()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up).SelectMany(x => x.GetIPProperties().UnicastAddresses).Select(x => x.Address)];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static bool SameId(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Replace("-", "", StringComparison.Ordinal), b.Replace("-", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

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
}
