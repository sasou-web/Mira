using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Mira.Jellyfin;

/// <summary>
/// Serves /Mira (after Jellyfin's base URL, if any): since Mira web is gone, a page that sends to Mira's apps, for the
/// phones that kept it on their Home Screen (their kept copy sees its revision change and reloads into it), and the
/// files of the extension's pages. Public, as the page of a party link is.
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

    /// <summary>What /Mira says now (index.html). /Mira becomes /Mira/, so that its files are found next to it.</summary>
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
        return Serve(files, "index.html", NoCache);
    }

    /// <summary>
    /// Name, colours, icons; also how a Mira for Windows of 0.9.3 or before sees that the extension answers (it installs
    /// it otherwise).
    /// </summary>
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
    private const string NoCache = "no-cache";

    /// <summary>Files of the app that phones cannot get (see <see cref="WebFiles.Unserved"/>), said once per revision.</summary>
    private void ReportUnserved(WebFiles files)
    {
        if (files.Unserved.Count == 0 || Interlocked.Exchange(ref _reported, files.Revision) == files.Revision)
        {
            return;
        }

        _logger.LogWarning("Mira: {Count} file(s) not served, their type unknown to the plugin: {Files}", files.Unserved.Count, string.Join(", ", files.Unserved));
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
