using System.Net;
using MediaBrowser.Common.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mira.Jellyfin;

/// <summary>
/// « Identifier… » (Mira web, Windows, Mac), for Jellyfin's administrators only: the poster of what a metadata source
/// proposes for a title, fetched here, by the server that already speaks to TMDb, so that the phone or the PC never
/// does (Jellyfin gives the address of the picture on TMDb, and has no such relay of its own since 10.9).
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Mira/identify")]
public sealed class IdentifyController : ControllerBase
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    private readonly IHttpClientFactory _clients;

    public IdentifyController(IHttpClientFactory clients)
    {
        _clients = clients;
    }

    [HttpGet("image")]
    public async Task<IActionResult> Image([FromQuery] string? url, CancellationToken cancellationToken)
    {
        if (IdentifyImages.Allowed(url) is not { } uri)
        {
            return BadRequest();
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Limit);
        // Jellyfin's own client for the Internet (its user agent, its proxy settings), as for its metadata.
        using var client = _clients.CreateClient(NamedClient.Default);
        try
        {
            using var response = await client.GetAsync(IdentifyImages.Smaller(uri), HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            var type = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || type is null || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || response.RequestMessage?.RequestUri is not { } reached || IdentifyImages.Allowed(reached.AbsoluteUri) is null
                || response.Content.Headers.ContentLength > IdentifyImages.MaxBytes)
            {
                return StatusCode((int)HttpStatusCode.BadGateway);
            }

            var bytes = await ReadAsync(response, limit.Token).ConfigureAwait(false);
            if (bytes is null)
            {
                return StatusCode((int)HttpStatusCode.BadGateway);
            }

            Response.Headers.CacheControl = "private, max-age=86400";
            return File(bytes, type);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return StatusCode((int)HttpStatusCode.GatewayTimeout);
        }
    }

    /// <summary>The picture, null past <see cref="IdentifyImages.MaxBytes"/> (a length not announced is counted as it comes).</summary>
    private static async Task<byte[]?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (copy.Length + read > IdentifyImages.MaxBytes)
            {
                return null;
            }

            copy.Write(buffer, 0, read);
        }

        return copy.ToArray();
    }
}
