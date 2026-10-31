using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Mira.Jellyfin;

/// <summary>
/// Serves Mira web at /Mira (after Jellyfin's base URL, if any). Every page is public, like Jellyfin's own web
/// client: the app signs in through Jellyfin's API, from the same address, with the person's own account.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("Mira")]
public sealed class WebController : ControllerBase
{
    private static string _reported = "";
    private readonly ILogger<WebController> _logger;

    public WebController(ILogger<WebController> logger)
    {
        _logger = logger;
    }

    /// <summary>The app itself. /Mira becomes /Mira/, so that its files are found next to it.</summary>
    [HttpGet("")]
    public IActionResult Index()
    {
        if (!(Request.Path.Value ?? "").EndsWith('/'))
        {
            return Redirect(Request.PathBase + Request.Path + "/" + Request.QueryString);
        }

        // The policy and the page from the same files: served from a folder (MIRA_WEB_DIR), each request reads them anew.
        var files = WebFiles.Current;
        ReportUnserved(files);
        Response.Headers.ContentSecurityPolicy = files.Policy;
        Response.Headers["Referrer-Policy"] = "no-referrer";
        // The phone keeps the page a month: Mira opens even while the PC is off or Jellyfin is starting, then waits for
        // it. At each start the app asks for the page again and reloads when its revision changed (an update).
        return Serve(files, "index.html", files.Immutable ? PageCache : NoCache);
    }

    /// <summary>What the phone needs to add Mira to its home screen: name, colours, icons.</summary>
    [HttpGet("manifest.webmanifest")]
    public IActionResult Manifest() => Serve(WebFiles.Current, "manifest.webmanifest", NoCache);

    /// <summary>Scripts, styles, fonts and images, kept by the phone until the next version of Mira.</summary>
    [HttpGet("v/{revision}/{**path}")]
    public IActionResult Asset(string revision, string path)
    {
        var files = WebFiles.Current;
        // The page itself is only served at /Mira/, with its security policy.
        return path is "index.html" or "party.html" ? NotFound() : Serve(files, path, files.Immutable && revision == files.Revision ? AssetCache : NoCache);
    }

    private const string AssetCache = "public, max-age=31536000, immutable";
    private const string PageCache = "max-age=2592000";
    private const string NoCache = "no-cache";

    /// <summary>Files of the app that phones cannot get (see <see cref="WebFiles.Unserved"/>), said once per revision.</summary>
    private void ReportUnserved(WebFiles files)
    {
        if (files.Unserved.Count == 0 || Interlocked.Exchange(ref _reported, files.Revision) == files.Revision)
        {
            return;
        }

        _logger.LogWarning("Mira web: {Count} file(s) not served, their type unknown to the plugin: {Files}", files.Unserved.Count, string.Join(", ", files.Unserved));
    }

    private IActionResult Serve(WebFiles files, string path, string cache)
    {
        if (!files.TryGet(path, out var file))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = cache;
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(file.Content, file.ContentType, lastModified: null, new EntityTagHeaderValue(file.ETag));
    }
}
