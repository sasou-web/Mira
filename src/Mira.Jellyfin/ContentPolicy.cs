using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Mira.Jellyfin;

/// <summary>
/// The page's security policy: Mira web loads only its own files and talks only to this server. Its inline style
/// (dark from the first frame) is allowed by its hash, computed from the page as it is served, rather than any inline
/// style ('unsafe-inline'): a style element or attribute slipped into the page is refused. The app sets its styles
/// through the CSSOM (element.style), which a policy does not restrict, never through style attributes.
/// </summary>
public static partial class ContentPolicy
{
    // hls.js (on browsers without HLS) runs in a worker.
    private const string Before = "default-src 'self'; script-src 'self'; style-src 'self'";
    private const string After = "; img-src 'self' data: blob:; media-src 'self' blob: data:; connect-src 'self'; worker-src 'self' blob:; " +
        "font-src 'self'; manifest-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'self'";

    /// <summary>The policy for this page, its inline styles allowed by their hashes.</summary>
    public static string For(string page) => Before + string.Concat(StyleHashes(page).Select(hash => " " + hash)) + After;

    /// <summary>
    /// 'sha256-…' for each style element, over its text as the browser hashes it: in UTF-8, with the line breaks the
    /// HTML parser leaves (CR LF and CR become LF), whatever the line endings of the file.
    /// </summary>
    public static IReadOnlyList<string> StyleHashes(string page) => StyleElement().Matches(page)
        .Select(match => match.Groups["css"].Value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
        .Select(css => "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(css))) + "'")
        .Distinct(StringComparer.Ordinal)
        .ToList();

    [GeneratedRegex(@"<style(?:\s[^>]*)?>(?<css>.*?)</style\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex StyleElement();
}
