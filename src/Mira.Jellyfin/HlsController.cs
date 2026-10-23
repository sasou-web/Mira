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
    // Each of this server's addresses gets this long to answer (it answers its own playlists at once): one that does
    // not answer is like one that refuses, and the next is tried, rather than the player waiting 100 s for nothing.
    private static readonly TimeSpan PerAddress = TimeSpan.FromSeconds(8);
    private readonly IHttpClientFactory _clients;
    private readonly IServerApplicationHost _host;

    public HlsController(IHttpClientFactory clients, IServerApplicationHost host)
    {
        _clients = clients;
        _host = host;
    }

    /// <summary>
    /// Jellyfin's master playlist for this conversion (same query), its subtitle playlists here, named as Mira names them
    /// (MiraNames, not passed on to Jellyfin). If this server cannot be reached on its own addresses, the player is sent
    /// to Jellyfin's playlist itself: the video plays, its subtitles as Jellyfin times them.
    /// </summary>
    [HttpGet("{itemId:guid}/master.m3u8")]
    public async Task<IActionResult> Master(Guid itemId, CancellationToken cancellationToken)
    {
        var item = itemId.ToString("N");
        var query = QueryString.Create(Request.Query.Where(pair => !string.Equals(pair.Key, Names, StringComparison.OrdinalIgnoreCase)));
        var (status, text) = await GetAsync($"videos/{item}/master.m3u8{query}", cancellationToken).ConfigureAwait(false);
        if (status == (int)HttpStatusCode.BadGateway)
        {
            return Redirect($"{Request.PathBase}/videos/{item}/master.m3u8{query}");
        }

        return text is null
            ? StatusCode(status)
            : Playlist(HlsPlaylists.Master(text, $"{Request.PathBase}/videos/{item}/", $"{Request.PathBase}/Mira/hls/{item}/", HlsPlaylists.Names(Request.Query[Names])));
    }

    /// <summary>
    /// Jellyfin's playlist of one subtitle stream (same query), its WebVTT segments without the time map. If Jellyfin
    /// refuses it (a title it has not finished reading has no length yet), an empty playlist: Apple's player would
    /// otherwise refuse the whole stream, video included, for one subtitle it cannot have.
    /// </summary>
    [HttpGet("{itemId:guid}/{mediaSourceId:regex(^[[0-9a-fA-F]]{{32}}$)}/Subtitles/{index:int:min(0)}/subtitles.m3u8")]
    public async Task<IActionResult> Subtitles(Guid itemId, string mediaSourceId, int index, CancellationToken cancellationToken)
    {
        var path = $"videos/{itemId:N}/{mediaSourceId}/Subtitles/{index}/";
        var (status, text) = await GetAsync($"{path}subtitles.m3u8{Request.QueryString}", cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return status is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden ? StatusCode(status) : Playlist(HlsPlaylists.EmptySubtitles);
        }

        return Playlist(HlsPlaylists.Subtitles(text, $"{Request.PathBase}/{path}"));
    }

    private ContentResult Playlist(string text)
    {
        Response.Headers.CacheControl = "no-cache";
        return Content(text, PlaylistType, Encoding.UTF8);
    }

    /// <summary>This server's answer to a path below its base URL (see <see cref="JellyfinLoopback"/>).</summary>
    private async Task<(int Status, string? Text)> GetAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        using var client = _clients.CreateClient();
        foreach (var root in Roots())
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(PerAddress);
            try
            {
                using var response = await client.GetAsync(new Uri(root + pathAndQuery), limit.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return ((int)response.StatusCode, null);
                }

                return ((int)HttpStatusCode.OK, await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false));
            }
            catch (HttpRequestException)
            {
                // Not listening there: the next address.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Too slow there (or HttpClient's own time limit): the next address. The player gone, the request ends.
            }
        }

        return ((int)HttpStatusCode.BadGateway, null);
    }

    private IEnumerable<string> Roots() => JellyfinLoopback.Roots(_host, Request.PathBase);
}
