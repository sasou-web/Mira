using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Mira.Jellyfin;

/// <summary>
/// « Inviter par lien », the host's side, for Jellyfin's administrators only (making an account takes one): the links
/// made, a new one with its account, one taken back with its account, and the address the links start with. Every
/// change on Jellyfin goes through its own API, on its loopback address, with the administrator's own session.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Mira/invites")]
public sealed class InvitesController : ControllerBase
{
    // The name Jellyfin's dashboard shows for the plugin's API key (Tableau de bord → Clés API).
    private const string KeyName = "Mira";
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

        var admin = Admin();
        using var client = _clients.CreateClient();
        var (meStatus, meText) = await Call(client, HttpMethod.Get, "Users/Me", admin, null, cancellationToken).ConfigureAwait(false);
        var (usersStatus, usersText) = await Call(client, HttpMethod.Get, "Users", admin, null, cancellationToken).ConfigureAwait(false);
        var me = meStatus == (int)HttpStatusCode.OK ? Parse(meText) : null;
        var users = usersStatus == (int)HttpStatusCode.OK ? Parse(usersText) as JsonArray : null;
        if (me?["Id"] is null || users is null)
        {
            return Refuse(StatusCodes.Status502BadGateway, "Jellyfin n’a pas répondu. Réessaie dans un instant.");
        }

        var taken = users.Select(x => Text(x?["Name"])).OfType<string>();
        var name = GuestRoutes.GuestName(body?.Name, French ? "Invité" : "Guest", taken);
        if (name is null)
        {
            return Refuse(StatusCodes.Status400BadRequest, "Son prénom ne peut contenir que des lettres, des chiffres, des espaces et ces signes : - _ ' . @ +");
        }

        var password = GuestRoutes.NewPassword();
        var (madeStatus, madeText) = await Call(client, HttpMethod.Post, "Users/New", admin, JsonSerializer.Serialize(new { Name = name, Password = password }), cancellationToken).ConfigureAwait(false);
        var userId = madeStatus == (int)HttpStatusCode.OK ? Text(Parse(madeText)?["Id"]) : null;
        if (userId is null)
        {
            return Refuse(StatusCodes.Status502BadGateway, "Jellyfin n’a pas créé le compte de l’invité.");
        }

        // A new account sees every library and downloads (Jellyfin 12.1): its policy is read, made a guest's, sent back whole.
        var (userStatus, userText) = await Call(client, HttpMethod.Get, $"Users/{userId}", admin, null, cancellationToken).ConfigureAwait(false);
        var policy = userStatus == (int)HttpStatusCode.OK ? Parse(userText)?["Policy"] as JsonObject : null;
        var (policyStatus, _) = policy is null ? ((int)HttpStatusCode.BadGateway, "")
            : await Call(client, HttpMethod.Post, $"Users/{userId}/Policy", admin, GuestRoutes.Policy(policy).ToJsonString(), cancellationToken).ConfigureAwait(false);
        if (policyStatus is not ((int)HttpStatusCode.OK or (int)HttpStatusCode.NoContent))
        {
            await Call(client, HttpMethod.Delete, $"Users/{userId}", admin, null, CancellationToken.None).ConfigureAwait(false);
            return Refuse(StatusCodes.Status502BadGateway, "Jellyfin n’a pas créé le compte de l’invité.");
        }

        await KeepKeyAsync(client, admin, cancellationToken).ConfigureAwait(false);
        var invite = Invites.Add(name, userId, password, Text(me["Id"])!, Text(me["Name"]) ?? "");
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

    /// <summary>The address the links start with (« https://pc.tailnet.ts.net »), or none (null).</summary>
    [HttpPut("address")]
    [RequestSizeLimit(4_096)]
    public async Task<IActionResult> Address(CancellationToken cancellationToken)
    {
        var body = await ReadAsync<AddressBody>(cancellationToken).ConfigureAwait(false);
        var address = string.IsNullOrWhiteSpace(body?.Address) ? null : body.Address;
        return Invites.SetAddress(address)
            ? Answer(Listing())
            : Refuse(StatusCodes.Status400BadRequest, "L’adresse doit commencer par https:// et finir par le nom de l’ordinateur.");
    }

    private object Listing() => new { address = Invites.Address, invites = Invites.All().Select(x => View(x, Invites.Address)) };

    private static object View(GuestInvite invite, string? address) => new
    {
        id = invite.Id, name = invite.Name, created = invite.Created, used = invite.Used, ends = invite.Used + GuestInvites.Unused,
        opened = invite.Device is not null, device = invite.DeviceName, link = GuestInvites.Link(address, invite.Code),
    };

    /// <summary>
    /// Jellyfin's API key for the plugin, kept to delete the accounts of links gone unused while no administrator is
    /// there: made once, with this administrator's session, and made again if the dashboard deleted it.
    /// </summary>
    private async Task KeepKeyAsync(HttpClient client, IReadOnlyList<(string, string)> admin, CancellationToken cancellationToken)
    {
        static IEnumerable<JsonNode> Keys(string text) => Parse(text)?["Items"] is JsonArray items ? items.OfType<JsonNode>() : [];
        var (status, text) = await Call(client, HttpMethod.Get, "Auth/Keys", admin, null, cancellationToken).ConfigureAwait(false);
        if (status != (int)HttpStatusCode.OK || (Invites.Key is { } kept && Keys(text).Any(x => Text(x["AccessToken"]) == kept)))
        {
            return;
        }

        var before = Keys(text).Select(x => Text(x["AccessToken"])).ToHashSet();
        await Call(client, HttpMethod.Post, $"Auth/Keys?app={KeyName}", admin, null, cancellationToken).ConfigureAwait(false);
        (status, text) = await Call(client, HttpMethod.Get, "Auth/Keys", admin, null, cancellationToken).ConfigureAwait(false);
        var made = status == (int)HttpStatusCode.OK
            ? Keys(text).Where(x => Text(x["AppName"]) == KeyName && !before.Contains(Text(x["AccessToken"]))).Select(x => Text(x["AccessToken"])).FirstOrDefault()
            : null;
        if (made is not null)
        {
            Invites.Key = made;
        }
    }

    /// <summary>The administrator's own session, as this request carried it: what the plugin's calls to Jellyfin use.</summary>
    private IReadOnlyList<(string, string)> Admin()
    {
        var headers = new List<(string, string)>();
        foreach (var name in new[] { "Authorization", "X-Emby-Authorization", "X-Emby-Token", "X-MediaBrowser-Token" })
        {
            if (Request.Headers.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
            {
                headers.Add((name, value.ToString()));
            }
        }

        var key = Request.Query.FirstOrDefault(x => x.Key.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("api_key", StringComparison.OrdinalIgnoreCase)).Value.ToString();
        // Jellyfin 12.1 reads a session from Authorization or the query, no longer from X-Emby-Token.
        if (!string.IsNullOrEmpty(key) && !headers.Any(x => x.Item1 == "Authorization"))
        {
            headers.Add(("Authorization", $"MediaBrowser Token=\"{key}\""));
        }

        return headers;
    }

    private Task<(int Status, string Text)> Call(HttpClient client, HttpMethod method, string path, IEnumerable<(string, string)> headers, string? json, CancellationToken cancellationToken) =>
        JellyfinLoopback.CallAsync(client, Roots(), method, path, headers, json, cancellationToken);

    private static JsonNode? Parse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

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
/// there (their requests), otherwise with the plugin's API key, every half hour once anything of the guests was asked
/// since Jellyfin started.
/// </summary>
internal static class GuestCleanup
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);
    private static readonly object Gate = new();
    private static Timer? _timer;
    private static int _running;

    /// <summary>Starts the half-hourly sweep, once, with what it needs to reach Jellyfin.</summary>
    public static void Wake(IHttpClientFactory clients, IServerApplicationHost host, string pathBase)
    {
        lock (Gate)
        {
            _timer ??= new Timer(_ => _ = SweepWithKeyAsync(clients, host, pathBase), null, TimeSpan.FromMinutes(1), Every);
        }
    }

    private static async Task SweepWithKeyAsync(IHttpClientFactory clients, IServerApplicationHost host, string pathBase)
    {
        if (Plugin.Invites.Key is not { } key)
        {
            return;
        }

        try
        {
            using var client = clients.CreateClient();
            await SweepAsync(client, JellyfinLoopback.Roots(host, pathBase), [("Authorization", $"MediaBrowser Token=\"{key}\"")], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            // Jellyfin stopping, or out of reach: the next half hour.
        }
    }

    /// <summary>Deletes each account still to delete; one Jellyfin no longer has counts as deleted.</summary>
    public static async Task SweepAsync(HttpClient client, IEnumerable<string> roots, IReadOnlyList<(string, string)> admin, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            foreach (var userId in Plugin.Invites.Sweep())
            {
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
