using System.Net;
using System.Text;
using System.Text.Json;

namespace Mira.Core;

/// <summary>
/// Jellyfin open to the whole Internet, as « Security » shows it (<see cref="JellyfinExposure"/>): <paramref name="Outside"/>,
/// the devices seen lately from an address that would be turned away once closed; <paramref name="ThisOutside"/>, this
/// device among them (closing from here would cut it: not offered).
/// </summary>
public sealed record InternetOpening(bool Open, IReadOnlyList<OutsideDevice> Outside, bool ThisOutside);

// « Your server » of « Security »: Jellyfin's access from the Internet read and closed by an administrator.
public sealed partial class JellyfinClient
{
    /// <summary>
    /// Whether Jellyfin lets the whole Internet in, and who would be cut once closed; null when this account may not read
    /// its network settings (not an administrator).
    /// </summary>
    public async Task<InternetOpening?> InternetOpeningAsync(CancellationToken ct = default)
    {
        if (await NetworkSettingsAsync(ct) is not { } network) return null;
        if (!JellyfinExposure.OpenToInternet(network)) return new(false, [], false);
        var local = JellyfinExposure.LocalOnceClosed(network);
        var sessions = await GetAsync<JsonElement>("Sessions", ct);
        var outside = JellyfinExposure.Outside(sessions, local);
        var self = outside.Any(x => x.Id == Connection.DeviceId);
        return new(true, [.. outside.Where(x => x.Id != Connection.DeviceId)], self);
    }

    /// <summary>
    /// Jellyfin closed to the Internet (<see cref="JellyfinExposure.Close"/>), read again just before: false when this
    /// account may not change it, or when this device itself comes from outside and would be cut.
    /// </summary>
    public async Task<bool> CloseToInternetAsync(CancellationToken ct = default)
    {
        if (await InternetOpeningAsync(ct) is not { } opening || opening.ThisOutside) return false;
        if (!opening.Open) return true;
        if (await NetworkSettingsAsync(ct) is not { } network) return false;
        using var content = new StringContent(JellyfinExposure.Close(network).ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("System/Configuration/network", content, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return false;
        await CheckAsync(response);
        return true;
    }
}
