using System.Net;
using System.Text.RegularExpressions;

namespace Mira.Jellyfin;

/// <summary>
/// Every session token a request carries, read in every place and every way Jellyfin reads one, so that a guest's
/// session is known wherever Jellyfin would take it (<see cref="TunnelGuard"/>, <see cref="GuestController"/>).
/// Jellyfin's reading (its AuthorizationContext, the same in 10.9, 10.10 and 12.x): the first value of Authorization
/// (12.x; X-Emby-Authorization with the legacy setting on) or of X-Emby-Authorization (10.9, 10.10; then
/// Authorization), « MediaBrowser » or « Emby » then its parts (<see cref="Parts"/>: the name trimmed, « Token = »
/// as good as « Token= », the value URL-decoded, « %61b… » as good as « ab… »), else X-Emby-Token, X-MediaBrowser-Token,
/// ApiKey and api_key in the query (a name of any case; two values joined by a comma). This reads more, never less:
/// both headers, every value of each, whatever the scheme, a « token » of any case, every Token of a header, and the
/// plain reading of before (a Token=… as it is written) too. Free of ASP.NET, for the tests.
/// </summary>
public static partial class SessionTokens
{
    private static readonly string[] AuthorizationHeaders = ["Authorization", "X-Emby-Authorization"];
    private static readonly string[] TokenHeaders = ["X-Emby-Token", "X-MediaBrowser-Token"];
    private static readonly string[] QueryNames = ["ApiKey", "api_key"];

    /// <summary>
    /// The tokens of a request, given its headers' values by name (<paramref name="header"/>, a name of any case, as
    /// ASP.NET reads them) and its query's (<paramref name="query"/>, the same). Empty ones left out; may repeat.
    /// </summary>
    public static IEnumerable<string> Of(Func<string, IReadOnlyList<string?>> header, Func<string, IReadOnlyList<string?>> query)
    {
        foreach (var name in AuthorizationHeaders)
        {
            foreach (var value in header(name))
            {
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                foreach (var token in FromAuthorization(value))
                {
                    yield return token;
                }
            }
        }

        foreach (var name in TokenHeaders)
        {
            foreach (var token in Plain(header(name)))
            {
                yield return token;
            }
        }

        foreach (var name in QueryNames)
        {
            foreach (var token in Plain(query(name)))
            {
                yield return token;
            }
        }
    }

    /// <summary>The tokens of one Authorization value: Jellyfin's reading of it, then the plain one of before.</summary>
    public static IEnumerable<string> FromAuthorization(string value)
    {
        // Jellyfin drops the scheme up to the first space (none: no parts at all). Any scheme here, not only theirs.
        var space = value.IndexOf(' ', StringComparison.Ordinal);
        foreach (var (key, part) in Parts(space < 0 ? value : value[(space + 1)..]))
        {
            if (key.Equals("Token", StringComparison.OrdinalIgnoreCase) && part.Length > 0)
            {
                yield return part;
            }
        }

        foreach (Match match in TokenParameter().Matches(value))
        {
            if (match.Groups[1].Value.Length > 0)
            {
                yield return match.Groups[1].Value;
            }
        }
    }

    /// <summary>
    /// The parts of an Authorization value after its scheme, exactly as Jellyfin cuts them (its GetParts): a quote
    /// opens or closes a value, a comma outside quotes ends one, an equals sign outside quotes ends a name. A name is
    /// trimmed of its spaces; a value only of its quotes, then URL-decoded (WebUtility.UrlDecode: « %61 », « + »). An
    /// empty value adds nothing and keeps the name for the next one (« Token=,"ab" » is Token = ab). Every part, in
    /// order: Jellyfin keeps the last of a name.
    /// </summary>
    public static IEnumerable<(string Key, string Value)> Parts(string parameters)
    {
        var quoted = false;
        var start = 0;
        var key = string.Empty;
        for (var i = 0; i < parameters.Length; i++)
        {
            var c = parameters[i];
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                if (start < i)
                {
                    yield return (key, Value(parameters[start..i]));
                    key = string.Empty;
                }

                start = i + 1;
            }
            else if (c == '=' && !quoted)
            {
                key = parameters[start..i].Trim();
                start = i + 1;
            }
        }

        if (start < parameters.Length)
        {
            yield return (key, Value(parameters[start..]));
        }
    }

    private static string Value(string raw) => WebUtility.UrlDecode(raw.Trim('"'));

    /// <summary>Each value of a header or a query parameter, and all of them joined by a comma as Jellyfin reads several.</summary>
    private static IEnumerable<string> Plain(IReadOnlyList<string?> values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrEmpty(value))
            {
                yield return value;
            }
        }

        if (values.Count > 1)
        {
            yield return string.Join(',', values);
        }
    }

    // The reading of before (2026-11-19, #662): a Token=… as it is written, kept beside Jellyfin's.
    [GeneratedRegex("Token=\"?([^\",\\s]*)\"?", RegexOptions.IgnoreCase)]
    private static partial Regex TokenParameter();
}
