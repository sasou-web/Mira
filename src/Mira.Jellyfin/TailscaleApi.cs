using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mira.Jellyfin;

/// <summary>
/// Tailscale's API (api.tailscale.com), with the home's OAuth client (<see cref="HomeTailnet"/>): an access token
/// (an hour), the tailnet's policy as JSON, a single-use key of <see cref="HomeTailnet.Tag"/>, the tailnet's devices,
/// one deleted. Called only when the administrator connects the home, opens its settings, makes a link or takes a
/// device out. What it sends: the client's id and secret (for the token), then that token; nothing of Jellyfin, its
/// library nor its accounts. The tailnet is « - »: the client's own.
/// </summary>
public sealed class TailscaleApi
{
    /// <summary>Tailscale's API.</summary>
    public const string Address = "https://api.tailscale.com";

    // Tokens by client id: an hour each, kept 55 minutes.
    private static readonly Dictionary<string, (string Token, DateTimeOffset Until)> Tokens = new(StringComparer.Ordinal);

    private readonly HttpClient _http;
    private readonly string _id;
    private readonly string _secret;
    private readonly string _base;
    private readonly Func<DateTimeOffset> _now;

    public TailscaleApi(HttpClient http, string id, string secret, string? baseAddress = null, Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _id = id;
        _secret = secret;
        _base = (baseAddress ?? Address).TrimEnd('/');
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>What went wrong with Tailscale, as the administrator is told.</summary>
    public enum Failure
    {
        /// <summary>The client's id or secret is refused (revoked, mistyped).</summary>
        Refused,
        /// <summary>The client lacks a scope or the tag (403).</summary>
        Scope,
        /// <summary>Tailscale did not answer, or answered something else.</summary>
        Unreachable,
        /// <summary>What was asked for is not there (404).</summary>
        Gone,
    }

    public sealed class TailscaleException(Failure failure, string message) : Exception(message)
    {
        public Failure Failure { get; } = failure;
    }

    /// <summary>Checks the client: an access token, made now.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        lock (Tokens) Tokens.Remove(_id);
        await TokenAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The tailnet's policy, as JSON (Tailscale drops the HuJSON's comments).</summary>
    public async Task<JsonNode?> PolicyAsync(CancellationToken cancellationToken)
    {
        var text = await SendAsync(HttpMethod.Get, "/api/v2/tailnet/-/acl", null, cancellationToken).ConfigureAwait(false);
        return Parse(text);
    }

    /// <summary>A key for one device, preauthorized, of the home's tag, that ends after <paramref name="life"/>: its secret, its id, its end.</summary>
    public async Task<(string Key, string Id, DateTimeOffset Expires)> CreateKeyAsync(TimeSpan life, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["devices"] = new JsonObject
                {
                    ["create"] = new JsonObject
                    {
                        ["reusable"] = false,
                        ["ephemeral"] = false,
                        ["preauthorized"] = true,
                        ["tags"] = new JsonArray(HomeTailnet.Tag),
                    },
                },
            },
            ["expirySeconds"] = (long)life.TotalSeconds,
            ["description"] = "Mira home link",
        };
        var text = await SendAsync(HttpMethod.Post, "/api/v2/tailnet/-/keys", body.ToJsonString(), cancellationToken).ConfigureAwait(false);
        if (Parse(text) is not JsonObject key || Str(key["key"]) is not { Length: > 0 } secret)
        {
            throw new TailscaleException(Failure.Unreachable, "Tailscale did not give the key.");
        }

        var expires = Time(key["expires"]) ?? _now() + life;
        return (secret, Str(key["id"]) ?? "", expires);
    }

    /// <summary>The tailnet's devices.</summary>
    public async Task<IReadOnlyList<TailnetDevice>> DevicesAsync(CancellationToken cancellationToken)
    {
        var text = await SendAsync(HttpMethod.Get, "/api/v2/tailnet/-/devices", null, cancellationToken).ConfigureAwait(false);
        return ReadDevices(Parse(text));
    }

    /// <summary>Tailscale's list of devices read: those without an id are left out.</summary>
    public static IReadOnlyList<TailnetDevice> ReadDevices(JsonNode? root)
    {
        if (root is not JsonObject { } list || list["devices"] is not JsonArray devices)
        {
            return [];
        }

        return devices.OfType<JsonObject>().Select(x => new TailnetDevice(
                Str(x["id"]) ?? "",
                Str(x["hostname"]) ?? Str(x["name"]) ?? "",
                (x["addresses"] as JsonArray)?.Select(a => Str(a)).OfType<string>().ToList() ?? [],
                (x["tags"] as JsonArray)?.Any(t => string.Equals(Str(t), HomeTailnet.Tag, StringComparison.OrdinalIgnoreCase)) == true,
                Time(x["created"]),
                Time(x["lastSeen"])))
            .Where(x => x.Id.Length > 0)
            .ToList();
    }

    /// <summary>A device deleted from the tailnet: true when it is gone (or was already).</summary>
    public async Task<bool> DeleteAsync(TailnetDevice device, CancellationToken cancellationToken)
    {
        // Never a device without the home's tag, whatever the caller asks.
        if (!device.Tagged)
        {
            return false;
        }

        try
        {
            await SendAsync(HttpMethod.Delete, "/api/v2/device/" + Uri.EscapeDataString(device.Id), null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TailscaleException error) when (error.Failure == Failure.Gone)
        {
            return true;
        }
    }

    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        lock (Tokens)
        {
            if (Tokens.TryGetValue(_id, out var kept) && kept.Until > _now())
            {
                return kept.Token;
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _base + "/api/v2/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _id,
                ["client_secret"] = _secret,
            }),
        };
        var (status, text) = await RawAsync(request, cancellationToken).ConfigureAwait(false);
        if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new TailscaleException(Failure.Refused, "The OAuth client is refused.");
        }

        if (status != HttpStatusCode.OK || Parse(text) is not JsonObject answer || Str(answer["access_token"]) is not { Length: > 0 } token)
        {
            throw new TailscaleException(Failure.Unreachable, "Tailscale did not give a token.");
        }

        var seconds = answer["expires_in"] is JsonValue v && v.TryGetValue<long>(out var s) ? s : 3600;
        lock (Tokens) Tokens[_id] = (token, _now() + TimeSpan.FromSeconds(Math.Max(60, seconds - 300)));
        return token;
    }

    private async Task<string> SendAsync(HttpMethod method, string path, string? json, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, _base + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        var (status, text) = await RawAsync(request, cancellationToken).ConfigureAwait(false);
        switch (status)
        {
            case HttpStatusCode.OK or HttpStatusCode.NoContent:
                return text;
            case HttpStatusCode.Unauthorized:
                lock (Tokens) Tokens.Remove(_id);
                throw new TailscaleException(Failure.Refused, "The OAuth client is refused.");
            case HttpStatusCode.Forbidden:
                throw new TailscaleException(Failure.Scope, Message(text) ?? "Forbidden.");
            case HttpStatusCode.NotFound:
                throw new TailscaleException(Failure.Gone, "Not found.");
            default:
                throw new TailscaleException(Failure.Unreachable, Message(text) ?? ((int)status).ToString(CultureInfo.InvariantCulture));
        }
    }

    private async Task<(HttpStatusCode Status, string Text)> RawAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "Mira-Jellyfin");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return (response.StatusCode, text.Length > 1_000_000 ? "" : text);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new TailscaleException(Failure.Unreachable, "Tailscale did not answer.");
        }
    }

    private static JsonNode? Parse(string text)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Message(string text) => Parse(text) is JsonObject o ? Str(o["message"]) : null;

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static DateTimeOffset? Time(JsonNode? node) =>
        Str(node) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;
}
