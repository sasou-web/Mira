using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mira.Core.Family;
using Mira.Core.TorLink;

namespace Mira.Core;

// « Security » (an administrator's): the accounts and devices of the server read at once, an account's password set,
// a device signed out (out of the home, or cut from a friend's link, too).
public sealed partial class JellyfinClient
{
    /// <summary>
    /// Everything « Security » shows, read as this administrator; null when this account may not read the accounts. The
    /// extension's word on passwords, the home's profiles and friends' accounts are read when there are some: an older
    /// extension, or none, leaves Jellyfin's own word.
    /// </summary>
    public async Task<SecurityReport?> SecurityAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("Users", ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
        await CheckAsync(response);
        using var users = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var devices = await GetAsync<JsonElement>("Devices", ct);
        var sessions = await GetAsync<JsonElement>("Sessions?activeWithinSeconds=960", ct);
        IReadOnlyCollection<string>? open = null; IReadOnlyCollection<string> guests = [];
        try
        {
            using var said = await _http.GetAsync("Mira/home/security", ct);
            if (said.IsSuccessStatusCode && said.Content.Headers.ContentType?.MediaType == "application/json"
                && await said.Content.ReadFromJsonAsync<SecurityAnswer>(Json.Options, ct) is { Known: true } answer)
            {
                open = [.. (answer.Open ?? []).Select(AccountSecurity.Plain)];
                guests = [.. (answer.Guests ?? []).Select(AccountSecurity.Plain)];
            }
        }
        catch (JsonException) { }
        IReadOnlyCollection<string> home = [], friends = [];
        try
        {
            if (await HomeSettingsAsync(ct) is { } settings) home = [.. settings.Profiles.Where(x => x.Offered).Select(x => AccountSecurity.Plain(x.Id))];
            if (await FriendsAsync(ct) is { } state)
                friends = [.. (state.Devices ?? []).Select(x => x.User).Concat((state.Invites ?? []).Select(x => x.User)).Select(AccountSecurity.Plain)];
        }
        catch (Exception ex) when (ex is RelayRefusedException or JsonException or HttpRequestException or IOException) { }
        return AccountSecurity.Read(users.RootElement, devices, sessions, open, Connection, home, friends, guests, DateTimeOffset.UtcNow);
    }

    private sealed record SecurityAnswer(bool Known, IReadOnlyList<string>? Open, IReadOnlyList<string>? Guests);

    /// <summary>
    /// Another account's password set by this administrator (« Protect »): every session of that account ends, as Jellyfin
    /// does on a new password. Asked at Users/Password (10.10 and later), else at Users/{id}/Password (10.9).
    /// </summary>
    public Task SetPasswordAsync(string userId, string password, CancellationToken ct = default) => PasswordAsync(userId, new { NewPw = password }, ct);

    /// <summary>
    /// This account's own password changed (its current one checked by Jellyfin; empty when it has none): its other
    /// devices are signed out, this one stays. Its new length is kept for « Security ». A wrong current password:
    /// <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    public async Task ChangeOwnPasswordAsync(string current, string password, CancellationToken ct = default)
    {
        await PasswordAsync(Connection.UserId, new { CurrentPw = current, NewPw = password }, ct);
        Connection = Connection with { PasswordLength = password.Length };
    }

    private async Task PasswordAsync(string userId, object body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"Users/Password?userId={Uri.EscapeDataString(userId)}", body, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            using var older = await _http.PostAsJsonAsync($"Users/{Uri.EscapeDataString(userId)}/Password", body, ct);
            await Refused(older);
            return;
        }
        await Refused(response);

        static async Task Refused(HttpResponseMessage response)
        {
            // Jellyfin refuses a wrong current password with 403 (checked on 12.1).
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException(SecurityText.WrongCurrent);
            await CheckAsync(response);
        }
    }

    /// <summary>
    /// A device signed out: a home's device leaves the home (its home password asked again), a friend's device is cut from
    /// its link, then Jellyfin forgets the device and ends its sessions. Never this one.
    /// </summary>
    public async Task SignOutDeviceAsync(SecurityDevice device, CancellationToken ct = default)
    {
        if (device.This || device.Id == Connection.DeviceId) return;
        try
        {
            if (device.Way == DeviceWay.Home) await HomeRemoveDeviceAsync(device.WayId, ct);
            else if (device.Way == DeviceWay.Friend) await FriendRemoveDeviceAsync(device.WayId, ct);
        }
        // Not (or no longer) in the home or among a friend's devices: Jellyfin's own sign-out still follows.
        catch (RelayRefusedException) { }
        using var response = await _http.DeleteAsync($"Devices?id={Uri.EscapeDataString(device.Id)}", ct);
        // A device Jellyfin no longer knows: 400 on 12.1 (404 elsewhere), already signed out.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return;
        await CheckAsync(response);
    }
}
