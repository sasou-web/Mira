using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mira.Core.TorLink;

namespace Mira.Jellyfin;

/// <summary>
/// « Télécharger sur le PC serveur » (see <see cref="TorLinkRelay"/>), for Jellyfin's administrators only: Jellyfin
/// checks the account of each request (its policy « RequiresElevation »), Mira web's and Mira for Windows' alike. An
/// administrator can already run any program through a plugin; nobody else reaches these addresses.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Mira/torlink")]
public sealed class TorLinkController : ControllerBase
{
    // Jellyfin's own claim for the device of a request (its InternalClaimTypes.DeviceId).
    private const string DeviceClaim = "Jellyfin-DeviceId";
    private static JsonSerializerOptions Json => RemoteProtocol.Json;
    private static TorLinkRelay Relay => TorLinkRelay.Shared;

    [HttpGet("")]
    public IActionResult Status() => Answer(Relay.Status());

    [HttpPost("search")]
    [RequestSizeLimit(16_384)]
    public async Task<IActionResult> Search(CancellationToken cancellationToken)
    {
        var body = await ReadAsync<SearchBody>(cancellationToken).ConfigureAwait(false);
        var (id, error) = Relay.Search(body?.Query, body?.Category);
        return id is null ? Refuse(error) : Answer(new { id });
    }

    [HttpGet("search/{id}")]
    public IActionResult SearchState(string id) => Relay.SearchState(id) is { } search ? Answer(search) : NotFound();

    [HttpPost("requests")]
    [RequestSizeLimit(32_768)]
    public async Task<IActionResult> Ask(CancellationToken cancellationToken)
    {
        var body = await ReadAsync<RequestBody>(cancellationToken).ConfigureAwait(false);
        var (id, error) = Relay.Request(body?.Magnet, body?.Search, body?.Result, body?.Name, body?.Size);
        return id is null ? Refuse(error) : Answer(new { id });
    }

    [HttpDelete("requests/{id}")]
    public IActionResult Cancel(string id) => Relay.Cancel(id) switch
    {
        true => NoContent(),
        false => Conflict(),
        null => NotFound(),
    };

    [HttpPost("control")]
    [RequestSizeLimit(4_096)]
    public async Task<IActionResult> Control(CancellationToken cancellationToken)
    {
        var body = await ReadAsync<ControlBody>(cancellationToken).ConfigureAwait(false);
        var (id, error) = Relay.Control(body?.Id, body?.Action);
        return id is null ? Refuse(error) : Answer(new { id });
    }

    /// <summary>Mira for Windows on the PC of Jellyfin: its report, and the work it leaves with.</summary>
    [HttpPost("agent")]
    [RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> Agent(CancellationToken cancellationToken)
    {
        var device = User.FindFirst(DeviceClaim)?.Value;
        if (string.IsNullOrEmpty(device))
        {
            return BadRequest();
        }

        var report = await ReadAsync<AgentReport>(cancellationToken).ConfigureAwait(false);
        return report is null ? BadRequest() : Answer(Relay.Exchange(device, report));
    }

    /// <summary>The body as <typeparamref name="T"/>; null when it is not that JSON.</summary>
    private async Task<T?> ReadAsync<T>(CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(HttpContext.Request.Body, Json, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private ContentResult Answer(object value)
    {
        Response.Headers.CacheControl = "no-store";
        return Content(JsonSerializer.Serialize(value, Json), "application/json; charset=utf-8");
    }

    private ContentResult Refuse(string? error)
    {
        var result = Answer(new { error });
        result.StatusCode = StatusCodes.Status400BadRequest;
        return result;
    }

    private sealed record SearchBody(string? Query, string? Category);
    private sealed record RequestBody(string? Magnet, string? Search, string? Result, string? Name, long? Size);
    private sealed record ControlBody(string? Id, string? Action);
}
