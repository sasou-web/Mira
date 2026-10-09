using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    // The app loads only its own files and talks only to this server; hls.js (on browsers without HLS) runs in a worker.
    private const string Policy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        "media-src 'self' blob: data:; connect-src 'self'; worker-src 'self' blob:; font-src 'self'; manifest-src 'self'; " +
        "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'self'";

    /// <summary>The app itself. /Mira becomes /Mira/, so that its files are found next to it.</summary>
    [HttpGet("")]
    public IActionResult Index()
    {
        if (!(Request.Path.Value ?? "").EndsWith('/'))
        {
            return Redirect(Request.PathBase + Request.Path + "/" + Request.QueryString);
        }

        Response.Headers.ContentSecurityPolicy = Policy;
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return Serve("index.html", cache: false);
    }

    /// <summary>What the phone needs to add Mira to its home screen: name, colours, icons.</summary>
    [HttpGet("manifest.webmanifest")]
    public IActionResult Manifest() => Serve("manifest.webmanifest", cache: false);

    /// <summary>Scripts, styles, fonts and images, kept by the phone until the next version of Mira.</summary>
    [HttpGet("v/{revision}/{**path}")]
    public IActionResult Asset(string revision, string path)
    {
        var files = WebFiles.Current;
        // The page itself is only served at /Mira/, with its security policy.
        return path == "index.html" ? NotFound() : Serve(path, cache: files.Immutable && revision == files.Revision);
    }

    private IActionResult Serve(string path, bool cache)
    {
        if (!WebFiles.Current.TryGet(path, out var file))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = cache ? "public, max-age=31536000, immutable" : "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(file.Content, file.ContentType, lastModified: null, new EntityTagHeaderValue(file.ETag));
    }
}
