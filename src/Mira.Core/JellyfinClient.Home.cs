using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Core.Family;

namespace Mira.Core;

// « Le foyer » (/Mira/home of the extension): the family's side without a Jellyfin session, its home token in the
// X-Mira-Home header, then the administrator's side with their own session. The extension says its refusals in this
// Mira's language (Accept-Language); they come back as HomeRefusedException with what they mean.
public sealed partial class JellyfinClient
{
    /// <summary>The header of the home token (HomeController.TokenHeader of the extension).</summary>
    public const string HomeHeader = "X-Mira-Home";
    private static readonly JsonSerializerOptions HomeJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Whether this Jellyfin has a home (GET Mira/home): true or false; null when its extension has none (not installed,
    /// older than the home) or it does not answer as one.
    /// </summary>
    public async Task<bool?> HomeOnAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("Mira/home", ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json") return null;
        try { return (await response.Content.ReadFromJsonAsync<HomeAnswer>(HomeJson, ct))?.Home; }
        catch (JsonException) { return null; }
    }

    /// <summary>This device joins the home with its password: its home token, the profiles, the device id of its sessions.</summary>
    public async Task<HomeJoined> HomeJoinAsync(string password, string device, string name, CancellationToken ct = default)
    {
        var body = new JsonObject { ["password"] = password, ["device"] = device, ["name"] = name };
        var answer = await HomeAsync(HttpMethod.Post, "Mira/home/join", null, body, ct);
        if (answer?.Token is not { Length: > 0 } token || answer.DeviceId is not { Length: > 0 } deviceId) throw new IOException(L("Réponse Jellyfin vide.", "Empty answer from Jellyfin."));
        return new HomeJoined(token, answer.Profiles ?? [], deviceId);
    }

    /// <summary>The profiles this device may open now, and the one it has open.</summary>
    public async Task<HomeProfiles> HomeProfilesAsync(string token, CancellationToken ct = default)
    {
        var answer = await HomeAsync(HttpMethod.Get, "Mira/home/profiles", token, null, ct);
        if (answer?.DeviceId is not { Length: > 0 } deviceId) throw new IOException(L("Réponse Jellyfin vide.", "Empty answer from Jellyfin."));
        return new HomeProfiles(answer.Profiles ?? [], answer.Current, deviceId);
    }

    /// <summary>
    /// A profile opened on this device (its PIN when it has one): a real session of its Jellyfin account, under the
    /// home's device id, as the connection to sign in with. The session this device had open ends on the server.
    /// </summary>
    public async Task<Connection> HomeOpenAsync(string token, string profile, string? pin, CancellationToken ct = default)
    {
        var body = new JsonObject { ["profile"] = profile, ["pin"] = pin, ["client"] = "Mira", ["version"] = AppVersion };
        var answer = await HomeAsync(HttpMethod.Post, "Mira/home/open", token, body, ct, pinSent: !string.IsNullOrEmpty(pin));
        if (answer is not { AccessToken: { Length: > 0 } access, UserId: { Length: > 0 } userId, DeviceId: { Length: > 0 } deviceId })
            throw new IOException(L("Réponse Jellyfin vide.", "Empty answer from Jellyfin."));
        return new Connection(Connection.Server, userId, answer.UserName is { Length: > 0 } user ? user : userId, access, deviceId) { ServerId = answer.ServerId is { Length: > 0 } id ? id : Connection.ServerId };
    }

    /// <summary>The profile open on this device closed; the device stays in the home. Never throws.</summary>
    public async Task<bool> HomeCloseAsync(string token, CancellationToken ct = default)
    {
        try { await HomeAsync(HttpMethod.Post, "Mira/home/close", token, null, ct); return true; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { return false; }
    }

    /// <summary>This device leaves the home: its token stops working, its profile's session ends, its node leaves the tailnet. Never throws.</summary>
    public async Task<bool> HomeLeaveAsync(string token, CancellationToken ct = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(8));
        try { await HomeAsync(HttpMethod.Post, "Mira/home/leave", token, null, limit.Token); return true; }
        catch (HomeRefusedException ex) when (ex.Kind == HomeRefusal.OutOfHome) { return true; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { return false; }
    }

    /// <summary>
    /// A profile's picture for the hall (Jellyfin serves an account's picture without a session, by its tag): null when
    /// it has none or it cannot be read.
    /// </summary>
    public async Task<byte[]?> ProfilePictureAsync(string userId, string? tag, int width, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        try
        {
            using var response = await _http.GetAsync($"Users/{Uri.EscapeDataString(userId)}/Images/Primary?maxWidth={width}&tag={Uri.EscapeDataString(tag)}&quality=90", ct);
            return response.IsSuccessStatusCode && response.Content.Headers.ContentLength is null or < 4_000_000 ? await response.Content.ReadAsByteArrayAsync(ct) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { return null; }
    }

    // ---- The administrator's side ----

    /// <summary>The home as its administrator sees it; null when the extension has no home (older than it, or none).</summary>
    public async Task<HomeSettings?> HomeSettingsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("Mira/home/settings", ct);
        if (response.StatusCode == HttpStatusCode.NotFound && response.Content.Headers.ContentType?.MediaType != "application/json") return null;
        await CheckInvitesAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<HomeSettings>(HomeJson, ct) ?? throw new IOException(L("Réponse Jellyfin vide.", "Empty answer from Jellyfin."));
    }

    /// <summary>The home's password set or changed (every device out), or null to turn the home off.</summary>
    public Task HomeSetPasswordAsync(string? password, CancellationToken ct = default) =>
        HomeAdminAsync(HttpMethod.Put, "Mira/home/password", new JsonObject { ["password"] = password }, ct);

    /// <summary>An account offered as a profile of the home, or no longer.</summary>
    public Task HomeOfferAsync(string userId, bool offered, CancellationToken ct = default) =>
        HomeAdminAsync(HttpMethod.Put, $"Mira/home/profiles/{Uri.EscapeDataString(userId)}", new JsonObject { ["offered"] = offered }, ct);

    /// <summary>A profile's PIN (four digits) set, or taken off (null), by the administrator.</summary>
    public Task HomeSetPinAsync(string userId, string? pin, CancellationToken ct = default) =>
        HomeAdminAsync(HttpMethod.Put, $"Mira/home/profiles/{Uri.EscapeDataString(userId)}/pin", new JsonObject { ["pin"] = pin }, ct);

    /// <summary>A device out of the home.</summary>
    public Task HomeRemoveDeviceAsync(string id, CancellationToken ct = default) =>
        HomeAdminAsync(HttpMethod.Delete, $"Mira/home/devices/{Uri.EscapeDataString(id)}", null, ct);

    /// <summary>The home on the administrator's tailnet: the setup to write once, the OAuth client, the policy's verdict.</summary>
    public async Task<HomeTailnetState> HomeTailnetAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("Mira/home/tailnet", ct);
        await CheckInvitesAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<HomeTailnetState>(HomeJson, ct) ?? throw new IOException(L("Réponse Jellyfin vide.", "Empty answer from Jellyfin."));
    }

    /// <summary>
    /// The home connected to the tailnet with an OAuth client (Tailscale checks it first: <see cref="HomeTailnetRefusedException"/>
    /// otherwise), or disconnected (both empty). <paramref name="clientId"/> may be null: the secret carries it.
    /// </summary>
    public Task HomeConnectTailnetAsync(string? clientId, string? clientSecret, CancellationToken ct = default) =>
        HomeTailnetCallAsync(HttpMethod.Put, "Mira/home/tailnet", new JsonObject { ["clientId"] = clientId, ["clientSecret"] = clientSecret }, ct);

    /// <summary>The tailnet's policy put right by the extension, as <see cref="HomeTailnetState.Fix"/> said (needs the client's policy_file scope).</summary>
    public Task HomeFixPolicyAsync(CancellationToken ct = default) =>
        HomeTailnetCallAsync(HttpMethod.Post, "Mira/home/tailnet/fix", null, ct);

    /// <summary>Mira's fix of the policy taken back (<see cref="HomeTailnetState.UndoUntil"/>).</summary>
    public Task HomeUndoPolicyAsync(CancellationToken ct = default) =>
        HomeTailnetCallAsync(HttpMethod.Post, "Mira/home/tailnet/undo", null, ct);

    private async Task HomeTailnetCallAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            HomeAnswer? refused = null;
            try { refused = await response.Content.ReadFromJsonAsync<HomeAnswer>(HomeJson, ct); }
            catch (JsonException) { }
            if (refused?.Error is { Length: > 0 } error) throw new HomeTailnetRefusedException(error, response.StatusCode, refused.Policy, refused.Missing ?? [], refused.NeedsId);
        }
        await CheckInvitesAsync(response, ct);
    }

    /// <summary>
    /// A home link: a key of the home's tag for one device, for 48 hours. Refused while the tailnet's policy lets the
    /// home's devices go further than Jellyfin: <see cref="HomeLinkRefusedException"/> with its verdict.
    /// </summary>
    public async Task<HomeLinkMade> HomeLinkAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("Mira/home/link", null, ct);
        if (!response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            HomeAnswer? refused = null;
            try { refused = await response.Content.ReadFromJsonAsync<HomeAnswer>(HomeJson, ct); }
            catch (JsonException) { }
            if (refused?.Error is { Length: > 0 } error) throw new HomeLinkRefusedException(error, response.StatusCode, refused.Policy, refused.Open ?? []);
        }
        await CheckInvitesAsync(response, ct);
        var answer = await response.Content.ReadFromJsonAsync<HomeAnswer>(HomeJson, ct);
        return answer is { Link: { Length: > 0 } link, Expires: { } expires } ? new HomeLinkMade(link, expires) : throw new IOException(L("Réponse Jellyfin vide.", "Empty answer from Jellyfin."));
    }

    private async Task HomeAdminAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        await CheckInvitesAsync(response, ct);
    }

    /// <summary>A request of the family's side: its answer, or its refusal as a <see cref="HomeRefusedException"/>.</summary>
    private async Task<HomeAnswer?> HomeAsync(HttpMethod method, string path, string? token, JsonObject? body, CancellationToken ct, bool pinSent = false)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.TryAddWithoutValidation(HomeHeader, token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        var json = response.Content.Headers.ContentType?.MediaType == "application/json";
        HomeAnswer? answer = null;
        if (json && response.StatusCode != HttpStatusCode.NoContent)
            try { answer = await response.Content.ReadFromJsonAsync<HomeAnswer>(HomeJson, ct); }
            catch (JsonException) { answer = null; }
        if (response.IsSuccessStatusCode) return answer;
        // No extension, or one older than the home: Jellyfin's own 404, not a JSON one.
        if (!json && response.StatusCode == HttpStatusCode.NotFound) throw new HomeRefusedException(HomeText.NoExtension, response.StatusCode, HomeRefusal.NoHome);
        if (!json) await CheckAsync(response);
        throw Refusal(path, response.StatusCode, answer, pinSent);
    }

    /// <summary>What a refusal of the family's side means, by its route and status (HomeController of the extension).</summary>
    internal static HomeRefusedException Refusal(string path, HttpStatusCode status, HomeAnswer? answer, bool pinSent = false)
    {
        var said = answer?.Error is { Length: > 0 } error ? error : StatusMessage(status);
        var join = path.EndsWith("/join", StringComparison.Ordinal);
        var kind = (int)status switch
        {
            404 when join => HomeRefusal.NoHome,
            404 => HomeRefusal.NotInHome,
            401 when join => HomeRefusal.Wrong,
            401 => HomeRefusal.OutOfHome,
            409 when join => HomeRefusal.Full,
            // The same 403 for a PIN asked and a wrong one: the app knows whether it sent one.
            403 when answer?.Pin == true => pinSent ? HomeRefusal.PinWrong : HomeRefusal.PinAsked,
            429 when answer?.Pin == true => HomeRefusal.PinWait,
            429 => HomeRefusal.TooMany,
            502 => HomeRefusal.NotReady,
            _ => HomeRefusal.Other,
        };
        return new HomeRefusedException(said, status, kind, answer?.Left, answer?.Wait);
    }
}
