using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Mira.Jellyfin;

/// <summary>
/// The HLS playlists of a conversion for Apple's player, with its subtitles in time (see <see cref="HlsPlaylists"/>).
/// Mira asks here for Jellyfin's master playlist and its subtitle playlists: they are fetched from this server itself,
/// on its own address, then rewritten; video, sound and subtitle files still come straight from Jellyfin. Like
/// Jellyfin's own playlists, every address carries the person's ApiKey, which Jellyfin checks.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("Mira/hls")]
public sealed class HlsController : ControllerBase
{
    private const string PlaylistType = "application/vnd.apple.mpegurl";
    private const string Names = "MiraNames";
    private readonly IHttpClientFactory _clients;
    private readonly IServerApplicationHost _host;

    public HlsController(IHttpClientFactory clients, IServerApplicationHost host)
    {
        _clients = clients;
        _host = host;
    }

    /// <summary>
    /// Jellyfin's master playlist for this conversion (same query), its subtitle playlists here, named as Mira names them
    /// (MiraNames, not passed on to Jellyfin).
    /// </summary>
    [HttpGet("{itemId:guid}/master.m3u8")]
    public async Task<IActionResult> Master(Guid itemId, CancellationToken cancellationToken)
    {
        var item = itemId.ToString("N");
        var query = QueryString.Create(Request.Query.Where(pair => !string.Equals(pair.Key, Names, StringComparison.OrdinalIgnoreCase)));
        var (status, text) = await GetAsync($"videos/{item}/master.m3u8{query}", cancellationToken).ConfigureAwait(false);
        return text is null
            ? StatusCode(status)
            : Playlist(HlsPlaylists.Master(text, $"{Request.PathBase}/videos/{item}/", $"{Request.PathBase}/Mira/hls/{item}/", HlsPlaylists.Names(Request.Query[Names])));
    }

    /// <summary>Jellyfin's playlist of one subtitle stream (same query), its WebVTT segments without the time map.</summary>
    [HttpGet("{itemId:guid}/{mediaSourceId:regex(^[[0-9a-fA-F]]{{32}}$)}/Subtitles/{index:int:min(0)}/subtitles.m3u8")]
    public async Task<IActionResult> Subtitles(Guid itemId, string mediaSourceId, int index, CancellationToken cancellationToken)
    {
        var path = $"videos/{itemId:N}/{mediaSourceId}/Subtitles/{index}/";
        var (status, text) = await GetAsync($"{path}subtitles.m3u8{Request.QueryString}", cancellationToken).ConfigureAwait(false);
        return text is null ? StatusCode(status) : Playlist(HlsPlaylists.Subtitles(text, $"{Request.PathBase}/{path}"));
    }

    private ContentResult Playlist(string text)
    {
        Response.Headers.CacheControl = "no-cache";
        return Content(text, PlaylistType, Encoding.UTF8);
    }

    /// <summary>
    /// This server's answer to a path below its base URL: on the loopback address first, then on the address it gives
    /// for its own network. Both come from Jellyfin's settings, never from the request.
    /// </summary>
    private async Task<(int Status, string? Text)> GetAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        using var client = _clients.CreateClient();
        foreach (var root in Roots())
        {
            try
            {
                using var response = await client.GetAsync(new Uri(root + pathAndQuery), cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return ((int)response.StatusCode, null);
                }

                return ((int)HttpStatusCode.OK, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (HttpRequestException)
            {
                // Not listening there: the next address.
            }
        }

        return ((int)HttpStatusCode.BadGateway, null);
    }

    private IEnumerable<string> Roots()
    {
        yield return $"http://127.0.0.1:{_host.HttpPort}{Request.PathBase}/";
        string? local = null;
        try
        {
            local = _host.GetApiUrlForLocalAccess(null, false);
        }
        catch (InvalidOperationException)
        {
            // No address for its own network.
        }

        if (!string.IsNullOrEmpty(local))
        {
            yield return local.TrimEnd('/') + "/";
        }
    }
}
