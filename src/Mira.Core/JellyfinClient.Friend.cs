using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Core.Family;

namespace Mira.Core;

// « Inviter un ami » by a link (/Mira/friend of the extension): the friend's side trades the link's invitation for a
// session of their account; the administrator's side makes links, lists them with friends' devices, cancels one, takes
// a device out, forgets a friend. Refusals come back in this Mira's language, as FriendRefusedException.
public sealed partial class JellyfinClient
{
    /// <summary>
    /// The invitation of a friend's link used, under a device id of its own: the wait token to ask with until the
    /// administrator lets the device in. Refused: <see cref="FriendRefusedException"/> (« used », « ended », « wrong »…).
    /// </summary>
    public async Task<string> FriendRedeemAsync(string invite, string device, string name, CancellationToken ct = default)
    {
        var body = new JsonObject { ["invite"] = invite, ["device"] = device, ["name"] = name, ["client"] = "Mira", ["version"] = AppVersion };
        var (status, answer) = await FriendPostAsync("Mira/friend/redeem", body, ct);
        return answer is { Wait: { Length: > 0 } wait } && status == HttpStatusCode.Accepted ? wait : throw new IOException(L("Réponse Jellyfin vide.", "Empty answer from Jellyfin."));
    }

    /// <summary>
    /// Whether the administrator let the device in yet: null while it waits, the connection to sign in with once it is
    /// in. Refused (or taken out): <see cref="FriendRefusedException"/> (« refused », « ended »…).
    /// </summary>
    public async Task<Connection?> FriendWaitAsync(string wait, CancellationToken ct = default)
    {
        var (status, answer) = await FriendPostAsync("Mira/friend/wait", new JsonObject { ["wait"] = wait, ["client"] = "Mira", ["version"] = AppVersion }, ct);
        if (status == HttpStatusCode.Accepted) return null;
        if (answer is not { AccessToken: { Length: > 0 } access, UserId: { Length: > 0 } userId, DeviceId: { Length: > 0 } deviceId })
            throw new IOException(L("Réponse Jellyfin vide.", "Empty answer from Jellyfin."));
        return new Connection(Connection.Server, userId, answer.UserName is { Length: > 0 } user ? user : userId, access, deviceId) { ServerId = answer.ServerId is { Length: > 0 } id ? id : Connection.ServerId };
    }

    /// <summary>A device waiting at a friend's link let in: it gets its session at its next ask.</summary>
    public Task FriendApproveAsync(string deviceId, CancellationToken ct = default) =>
        HomeAdminAsync(HttpMethod.Post, $"Mira/friend/devices/{Uri.EscapeDataString(deviceId)}/approve", null, ct);

    private async Task<(HttpStatusCode Status, FriendAnswer? Answer)> FriendPostAsync(string path, JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        using var response = await _http.SendAsync(request, ct);
        var answer = await FriendAnswerAsync(response, ct);
        if (!response.IsSuccessStatusCode)
        {
            // No extension, or one older than friends' links: Jellyfin's own 404.
            if (answer is null && response.StatusCode == HttpStatusCode.NotFound) throw new FriendRefusedException(FriendText.NoExtension, response.StatusCode, "old");
            throw new FriendRefusedException(answer?.Error is { Length: > 0 } error ? error : StatusMessage(response.StatusCode), response.StatusCode, answer?.State ?? "unreachable");
        }
        return (response.StatusCode, answer);
    }

    /// <summary>Friends' links and devices, as the administrator sees them; null when the extension has none (older than them).</summary>
    public async Task<FriendsState?> FriendsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("Mira/friend", ct);
        if (response.StatusCode == HttpStatusCode.NotFound && response.Content.Headers.ContentType?.MediaType != "application/json") return null;
        await CheckInvitesAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<FriendsState>(HomeJson, ct) ?? throw new IOException(L("Réponse Jellyfin vide.", "Empty answer from Jellyfin."));
    }

    /// <summary>
    /// A link for a friend's account, for one device, for 48 hours. Refused: <see cref="FriendRefusedException"/> with
    /// its state (« disconnected »: the home is not on Tailscale yet; « policy »: the policy lets devices go further).
    /// </summary>
    public async Task<FriendLinkMade> FriendInviteAsync(string userId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "Mira/friend/invite") { Content = new StringContent(new JsonObject { ["user"] = userId }.ToJsonString(), Encoding.UTF8, "application/json") };
        using var response = await _http.SendAsync(request, ct);
        var answer = await FriendAnswerAsync(response, ct);
        if (!response.IsSuccessStatusCode && answer?.Error is { Length: > 0 } error) throw new FriendRefusedException(error, response.StatusCode, answer.State ?? "");
        await CheckInvitesAsync(response, ct);
        return answer is { Link: { Length: > 0 } link, Expires: { } expires, Id: { Length: > 0 } id } ? new FriendLinkMade(link, expires, id) : throw new IOException(L("Réponse Jellyfin vide.", "Empty answer from Jellyfin."));
    }

    /// <summary>A link not used yet cancelled (its key revoked).</summary>
    public Task FriendCancelAsync(string inviteId, CancellationToken ct = default) =>
        HomeAdminAsync(HttpMethod.Delete, $"Mira/friend/invites/{Uri.EscapeDataString(inviteId)}", null, ct);

    /// <summary>A friend's device taken out, or one waiting refused: its session ended, its line to this server cut.</summary>
    public Task FriendRemoveDeviceAsync(string deviceId, CancellationToken ct = default) =>
        HomeAdminAsync(HttpMethod.Delete, $"Mira/friend/devices/{Uri.EscapeDataString(deviceId)}", null, ct);

    /// <summary>Everything of a friend forgotten (before their account is deleted): links cancelled, devices out. Never throws.</summary>
    public async Task<bool> FriendForgetAsync(string userId, CancellationToken ct = default)
    {
        try { await HomeAdminAsync(HttpMethod.Delete, $"Mira/friend/users/{Uri.EscapeDataString(userId)}", null, ct); return true; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { return false; }
    }

    private static async Task<FriendAnswer?> FriendAnswerAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json" || response.StatusCode == HttpStatusCode.NoContent) return null;
        try { return await response.Content.ReadFromJsonAsync<FriendAnswer>(HomeJson, ct); }
        catch (JsonException) { return null; }
    }

    private sealed record FriendAnswer(string? Error, string? State, string? Wait, string? AccessToken, string? UserId, string? UserName, string? ServerId, string? DeviceId, string? Link, DateTimeOffset? Expires, string? Id);
}
