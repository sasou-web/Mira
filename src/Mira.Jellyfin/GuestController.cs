using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Mira.Jellyfin;

/// <summary>
/// « Inviter par lien », the guest's side: the page of a link (Mira web, #/invite/&lt;code&gt;) opens it here, then
/// talks to Jellyfin only through this gateway, at /Mira/guest, which passes on the routes of
/// <see cref="GuestRoutes"/> and nothing else. Meant for an address on the Internet that reaches only /Mira (Tailscale
/// Funnel): Jellyfin's own pages and API stay out of reach. Each request goes to Jellyfin on its loopback address with
/// the guest's session, the one only this link's device was given, under that device's id; the session's token in it
/// is checked here first, so that no other account goes through. Jellyfin's WebSocket, which SyncPlay needs, answers
/// at any path of the server: the guest's page opens it with that same session.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("Mira/guest")]
public sealed partial class GuestController : ControllerBase
{
    // Jellyfin answers a segment of a conversion once ffmpeg wrote it: a minute at most before its first byte.
    private static readonly TimeSpan HeadersWithin = TimeSpan.FromSeconds(60);
    private const int MaxBody = 65_536;
    private static readonly string[] Passed = ["Accept", "Accept-Language", "Range", "If-Range", "If-None-Match", "If-Modified-Since"];
    private static readonly string[] Returned = ["Content-Range", "Accept-Ranges", "ETag", "Last-Modified", "Cache-Control", "Expires"];
    private static string Version => typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly IHttpClientFactory _clients;
    private readonly IServerApplicationHost _host;

    public GuestController(IHttpClientFactory clients, IServerApplicationHost host)
    {
        _clients = clients;
        _host = host;
    }

    private bool French => RelayText.French(Request);
    private IEnumerable<string> Roots() => JellyfinLoopback.Roots(_host, Request.PathBase);

    /// <summary>
    /// A link opened by a device: Jellyfin is asked for a session of the guest's account for it (by its password, which
    /// never leaves the server), and the link becomes that device's. Refused for a code that is no link, a link another
    /// device took, or too many tries.
    /// </summary>
    [HttpPost("open")]
    [RequestSizeLimit(4_096)]
    public async Task<IActionResult> Open(CancellationToken cancellationToken)
    {
        GuestCleanup.Wake(_clients, _host, Request.PathBase);
        OpenBody? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<OpenBody>(Request.Body, JsonWeb, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            body = null;
        }

        if (body?.Device is not { } device || !DeviceRule().IsMatch(device))
        {
            return BadRequest();
        }

        var (result, invite) = Plugin.Invites.Open(body.Code, device, ClientAddress());
        switch (result)
        {
            case GuestInvites.Opening.TooMany:
                return Refuse(StatusCodes.Status429TooManyRequests, "Trop d’essais : attends une minute.");
            case GuestInvites.Opening.Unknown:
                return Refuse(StatusCodes.Status404NotFound, "Ce lien ne marche plus : demande-en un nouveau à la personne qui t’a invité.");
            case GuestInvites.Opening.OtherDevice:
                return Refuse(StatusCodes.Status403Forbidden, "Ce lien sert déjà sur un autre appareil : demande-en un nouveau à la personne qui t’a invité.");
        }

        var name = DeviceName(body.Name);
        using var client = _clients.CreateClient();
        var (status, text) = await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Post, "Users/AuthenticateByName",
            [("Authorization", Authorization(name, device, null))], JsonSerializer.Serialize(new { Username = invite!.Name, Pw = invite.Password }), cancellationToken).ConfigureAwait(false);
        string? token = null, userId = null, serverId = null;
        if (status == (int)HttpStatusCode.OK)
        {
            try
            {
                var signedIn = JsonNode.Parse(text);
                token = signedIn?["AccessToken"]?.GetValue<string>();
                userId = signedIn?["User"]?["Id"]?.GetValue<string>();
                serverId = signedIn?["ServerId"]?.GetValue<string>();
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
            {
                token = null;
            }
        }

        if (string.IsNullOrEmpty(token) || userId is null)
        {
            return Refuse(StatusCodes.Status502BadGateway, "Jellyfin n’a pas ouvert la séance. Réessaie dans un instant.");
        }

        if (!Plugin.Invites.Opened(invite.Id, device, name, token))
        {
            await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Post, "Sessions/Logout", [("Authorization", Authorization(name, device, token))], null, CancellationToken.None).ConfigureAwait(false);
            return Refuse(StatusCodes.Status404NotFound, "Ce lien ne marche plus : demande-en un nouveau à la personne qui t’a invité.");
        }

        Response.Headers.CacheControl = "no-store";
        return new JsonResult(new { token, userId, userName = invite.Name, serverId, host = invite.HostName });
    }

    /// <summary>Jellyfin, for the guest's page: a route of <see cref="GuestRoutes"/>, or 404.</summary>
    [AcceptVerbs("GET", "HEAD", "POST", "DELETE")]
    [Route("{**path}")]
    public async Task<IActionResult> Relay(string? path, CancellationToken cancellationToken)
    {
        GuestCleanup.Wake(_clients, _host, Request.PathBase);
        var rest = path ?? "";
        var access = GuestRoutes.For(Request.Method, rest, name => Request.Query.TryGetValue(name, out var value) ? value.ToString() : null);
        if (access == GuestRoutes.Access.None)
        {
            return NotFound();
        }

        GuestInvite? invite = null;
        string? token = null;
        if (access != GuestRoutes.Access.Public)
        {
            var tokens = Tokens(Request).Distinct(StringComparer.Ordinal).ToList();
            invite = tokens.Count == 1 ? Plugin.Invites.ByToken(tokens[0]) : null;
            if (invite?.Device is null)
            {
                return Unauthorized();
            }

            token = tokens[0];
        }

        byte[]? body = null;
        if (Request.ContentLength > MaxBody)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (HttpMethods.IsPost(Request.Method))
        {
            using var copy = new MemoryStream();
            await Request.Body.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            if (copy.Length > MaxBody)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            body = copy.ToArray();
        }

        var auth = invite is null ? null : Authorization(invite.DeviceName ?? "Mira", invite.Device!, token);
        var query = Query(invite?.Device, token);
        using var client = _clients.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        switch (access)
        {
            case GuestRoutes.Access.Groups:
            {
                var (status, text) = await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Get, "SyncPlay/List", [("Authorization", auth!)], null, cancellationToken).ConfigureAwait(false);
                if (status != (int)HttpStatusCode.OK)
                {
                    return StatusCode(status);
                }

                var mine = new JsonArray([.. HostGroups(text, invite!.HostName).Select(x => x.DeepClone())]);
                return Content(mine.ToJsonString(), "application/json; charset=utf-8");
            }
            case GuestRoutes.Access.Group:
            {
                var (status, text) = await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Get, rest + query, [("Authorization", auth!)], null, cancellationToken).ConfigureAwait(false);
                JsonNode? group = null;
                try { group = status == (int)HttpStatusCode.OK ? JsonNode.Parse(text) : null; }
                catch (JsonException) { group = null; }
                if (!GuestRoutes.HostsGroup(group, invite!.HostName))
                {
                    return status is (int)HttpStatusCode.OK or (int)HttpStatusCode.NotFound ? NotFound() : StatusCode(status);
                }

                return Content(text, "application/json; charset=utf-8");
            }
            case GuestRoutes.Access.Join:
            {
                string? wanted = null;
                try { wanted = JsonNode.Parse(body ?? [])?["GroupId"]?.GetValue<string>(); }
                catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { wanted = null; }
                var (status, text) = await JellyfinLoopback.CallAsync(client, Roots(), HttpMethod.Get, "SyncPlay/List", [("Authorization", auth!)], null, cancellationToken).ConfigureAwait(false);
                if (status != (int)HttpStatusCode.OK)
                {
                    return StatusCode(status);
                }

                if (wanted is null || !HostGroups(text, invite!.HostName).Any(x => SameId(x["GroupId"]?.GetValue<string>(), wanted)))
                {
                    return NotFound();
                }

                break;
            }
        }

        return await ForwardAsync(client, rest + query, auth, body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The request passed on to Jellyfin, its answer streamed back (a playlist rewritten for the gateway).</summary>
    private async Task<IActionResult> ForwardAsync(HttpClient client, string pathAndQuery, string? auth, byte[]? body, CancellationToken cancellationToken)
    {
        var method = new HttpMethod(Request.Method);
        foreach (var root in Roots())
        {
            using var message = new HttpRequestMessage(method, new Uri(root + pathAndQuery));
            if (auth is not null)
            {
                message.Headers.TryAddWithoutValidation("Authorization", auth);
            }

            foreach (var name in Passed)
            {
                if (Request.Headers.TryGetValue(name, out var value))
                {
                    message.Headers.TryAddWithoutValidation(name, value.ToString());
                }
            }

            if (body is not null)
            {
                message.Content = new ByteArrayContent(body);
                if (Request.ContentType is { } type)
                {
                    message.Content.Headers.TryAddWithoutValidation("Content-Type", type);
                }
            }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(HeadersWithin);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                continue;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            using (response)
            {
                // The answer started: it streams as long as the guest reads it.
                limit.CancelAfter(Timeout.InfiniteTimeSpan);
                Response.StatusCode = (int)response.StatusCode;
                foreach (var name in Returned)
                {
                    if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                    {
                        Response.Headers[name] = values.ToArray();
                    }
                }

                Response.Headers.XContentTypeOptions = "nosniff";
                var type = response.Content.Headers.ContentType?.ToString();
                if (type is not null)
                {
                    Response.ContentType = type;
                }

                if (type is not null && type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
                {
                    var text = GuestRoutes.Playlist(await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false), Request.PathBase);
                    var bytes = Encoding.UTF8.GetBytes(text);
                    Response.ContentLength = bytes.Length;
                    await Response.Body.WriteAsync(bytes, limit.Token).ConfigureAwait(false);
                    return new EmptyResult();
                }

                if (response.Content.Headers.ContentLength is { } length)
                {
                    Response.ContentLength = length;
                }

                if (!HttpMethods.IsHead(Request.Method))
                {
                    try
                    {
                        await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
                        await stream.CopyToAsync(Response.Body, limit.Token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is IOException or HttpRequestException || (error is OperationCanceledException && cancellationToken.IsCancellationRequested))
                    {
                        // The guest went (a seek, the player closed), or Jellyfin stopped the stream: the answer ends here.
                        HttpContext.Abort();
                    }
                }

                return new EmptyResult();
            }
        }

        return StatusCode(StatusCodes.Status502BadGateway);
    }

    /// <summary>The host's groups among Jellyfin's list (a JSON array): those named after them, or with them in it.</summary>
    private static IEnumerable<JsonNode> HostGroups(string text, string host)
    {
        JsonNode? list;
        try { list = JsonNode.Parse(text); }
        catch (JsonException) { list = null; }
        return list is JsonArray groups ? groups.Where(x => GuestRoutes.HostsGroup(x, host)).Select(x => x!) : [];
    }

    private static bool SameId(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Replace("-", "", StringComparison.Ordinal), b.Replace("-", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The query as it goes on: its session once, as ApiKey (Jellyfin writes it into the addresses of its playlists,
    /// which the player asks for next), and the device of the link wherever a device is named, so that no request
    /// speaks for another device (its conversions, its sessions).
    /// </summary>
    private string Query(string? device, string? token)
    {
        var pairs = Request.Query
            .Where(x => !x.Key.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) && !x.Key.Equals("api_key", StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.Value.Select(value => new KeyValuePair<string, string?>(x.Key, device is not null && x.Key.Equals("deviceId", StringComparison.OrdinalIgnoreCase) ? device : value)));
        if (token is not null)
        {
            pairs = pairs.Append(new KeyValuePair<string, string?>("ApiKey", token));
        }

        return QueryString.Create(pairs).ToString();
    }

    /// <summary>Every session token the request carries, in any of the places Jellyfin reads one.</summary>
    internal static IEnumerable<string> Tokens(HttpRequest request)
    {
        foreach (var header in new[] { "Authorization", "X-Emby-Authorization" })
        {
            foreach (var value in request.Headers[header])
            {
                foreach (Match match in TokenParameter().Matches(value ?? ""))
                {
                    yield return match.Groups[1].Value;
                }
            }
        }

        foreach (var header in new[] { "X-Emby-Token", "X-MediaBrowser-Token" })
        {
            foreach (var value in request.Headers[header])
            {
                if (!string.IsNullOrEmpty(value))
                {
                    yield return value;
                }
            }
        }

        foreach (var pair in request.Query.Where(x => x.Key.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("api_key", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var value in pair.Value)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    yield return value;
                }
            }
        }
    }

    [GeneratedRegex("Token=\"?([^\",\\s]*)\"?", RegexOptions.IgnoreCase)]
    private static partial Regex TokenParameter();

    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")]
    private static partial Regex DeviceRule();

    /// <summary>The device as the host's lists name it: « iPhone », « Chrome »… (Mira web's names), never more.</summary>
    private static string DeviceName(string? name)
    {
        var clean = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '.').Take(30).ToArray()).Trim();
        return clean.Length == 0 ? "Navigateur" : clean;
    }

    private static string Authorization(string device, string deviceId, string? token) =>
        $"MediaBrowser Client=\"Mira\", Device=\"{device}\", DeviceId=\"{deviceId}\", Version=\"{Version}\"" + (token is null ? "" : $", Token=\"{token}\"");

    /// <summary>
    /// Where the request comes from, for the tries counted: the visitor's address that Tailscale Funnel (or any proxy
    /// on this computer) gives, else the connection's.
    /// </summary>
    private string ClientAddress()
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        if (remote is not null && IPAddress.IsLoopback(remote) && Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded))
        {
            var first = forwarded.ToString().Split(',')[0].Trim();
            if (first.Length > 0)
            {
                return first;
            }
        }

        return remote?.ToString() ?? "";
    }

    private ObjectResult Refuse(int status, string error)
    {
        Response.Headers.CacheControl = "no-store";
        return StatusCode(status, new { error = RelayText.For(error, French) });
    }

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    private sealed record OpenBody(string? Code, string? Device, string? Name);
}
