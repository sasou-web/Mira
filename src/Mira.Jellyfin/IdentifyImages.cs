namespace Mira.Jellyfin;

/// <summary>
/// The posters « Identifier… » shows (<see cref="IdentifyController"/>): only those of the image servers of
/// Jellyfin's metadata sources, over HTTPS, so that the extension never becomes a way to reach anything else.
/// </summary>
public static class IdentifyImages
{
    /// <summary>The largest poster passed on: a TMDb poster at w500 is a tenth of it.</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    // TMDb, TVDb (its plugin), OMDb (IMDb's posters, on Amazon's server), AniList, AniDB and MyAnimeList (their plugins).
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "image.tmdb.org",
        "artworks.thetvdb.com",
        "m.media-amazon.com",
        "s4.anilist.co",
        "cdn.anidb.net",
        "cdn.myanimelist.net",
    };

    /// <summary>The address to fetch, when it is one of those; null otherwise (another host, a port, credentials, HTTP).</summary>
    public static Uri? Allowed(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0 || !Hosts.Contains(uri.Host))
        {
            return null;
        }

        return uri;
    }

    /// <summary>A poster TMDb gives in its original size (several megabytes) asked for at the width a proposal shows.</summary>
    public static Uri Smaller(Uri uri) =>
        string.Equals(uri.Host, "image.tmdb.org", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/t/p/original/", StringComparison.Ordinal)
            ? new Uri(uri, "/t/p/w342/" + uri.AbsolutePath["/t/p/original/".Length..])
            : uri;
}
