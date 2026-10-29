using System.Net;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>
/// A title's summary as plain text: Jellyfin passes on the HTML of some metadata providers (AniList's « &lt;br&gt; »),
/// which a TextBlock would show as is. As overview.js of Mira web and the iPhone app (cases shared in
/// tests/fixtures/overview.json).
/// </summary>
public static partial class OverviewText
{
    /// <summary>
    /// &lt;br&gt; = a new line, a paragraph (or two &lt;br&gt;) = an empty line, other tags taken out, entities read, spaces and
    /// empty lines collapsed. <paramref name="flat"/>: on one line (a summary cut at two or four lines).
    /// </summary>
    public static string Plain(string? text, bool flat = false)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text.Replace("\r\n", "\n").Replace('\r', '\n');
        s = Break().Replace(s, "\n");
        s = Block().Replace(s, "\n\n");
        s = Tag().Replace(s, "");
        s = string.Join('\n', WebUtility.HtmlDecode(s).Split('\n').Select(line => Spaces().Replace(line, " ").Trim(' ')));
        s = EmptyLines().Replace(s, "\n\n").Trim('\n');
        return flat ? NewLines().Replace(s, " ") : s;
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)] private static partial Regex Break();
    [GeneratedRegex(@"</?(?:p|div|ul|ol|li|h[1-6]|blockquote)\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex Block();
    [GeneratedRegex(@"</?[a-z][^>]*>", RegexOptions.IgnoreCase)] private static partial Regex Tag();
    [GeneratedRegex(@"[ \t]+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex EmptyLines();
    [GeneratedRegex(@"\n+")] private static partial Regex NewLines();
}
