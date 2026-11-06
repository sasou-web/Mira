using static Mira.Core.Lang;

namespace Mira.Core;

/// <summary>Where « Bande-annonce » of a title's page leads: its own trailer on Jellyfin, a page on the web, or nowhere.</summary>
public enum TrailerSource { None, Jellyfin, Web }

/// <summary>
/// « Bande-annonce » of a title's page (Windows, Mac; the iPhone keeps its own copy, Home/Trailers.swift, with the cases
/// of tests/fixtures/trailers.json) and « Bande-annonce avant le film » (Windows, Mac). A trailer kept beside the title
/// (a « trailers » folder, « …-trailer.mkv ») plays in Mira, from Jellyfin alone. A trailer on the web (TMDb gives
/// YouTube's to Jellyfin) opens in the person's browser at their click, never in Mira: no web view, nothing of it is
/// loaded before (no picture, no title), and Mira sends it nothing.
/// </summary>
public static class Trailers
{
    /// <summary>What the button does for <paramref name="item"/>, as the single-item request describes it.</summary>
    public static TrailerSource Source(MediaItem item) =>
        item.Type is not ("Movie" or "Series") ? TrailerSource.None
        : item.LocalTrailerCount is > 0 ? TrailerSource.Jellyfin
        : WebLink(item) is not null ? TrailerSource.Web
        : TrailerSource.None;

    /// <summary>
    /// The first trailer on the web that is a plain web address: https, or http made https (YouTube serves both); never
    /// another scheme (javascript:, file:, mira:…), a name and password in the address, or a host that is no name.
    /// </summary>
    public static Uri? WebLink(MediaItem item)
    {
        foreach (var trailer in item.RemoteTrailers ?? [])
            if (WebLink(trailer.Url) is { } link) return link;
        return null;
    }
    public static Uri? WebLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        if (uri.UserInfo.Length > 0 || uri.HostNameType != UriHostNameType.Dns || !uri.Host.Contains('.', StringComparison.Ordinal)) return null;
        if (uri.Scheme == Uri.UriSchemeHttp) uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri;
        return uri;
    }

    /// <summary>The site a web trailer is on, as the button names it: « YouTube », else its host without « www. ».</summary>
    public static string Site(Uri link)
    {
        var host = link.Host.ToLowerInvariant();
        if (host is "youtu.be" or "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal)) return "YouTube";
        if (host is "vimeo.com" || host.EndsWith(".vimeo.com", StringComparison.Ordinal)) return "Vimeo";
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    public static string Label => L("Bande-annonce", "Trailer");
    /// <summary>The button's tooltip and name for screen readers: where it leads, said before the click.</summary>
    public static string Tip(MediaItem item) => Source(item) switch
    {
        TrailerSource.Jellyfin => L("Voir la bande-annonce", "Watch the trailer"),
        TrailerSource.Web when WebLink(item) is { } link => L($"Bande-annonce sur {Site(link)}, dans ton navigateur", $"Trailer on {Site(link)}, in your browser"),
        _ => Label,
    };

    /// <summary>« Bande-annonce avant le film » in Réglages → Lecture, and what it says under it.</summary>
    public static string BeforeFilmLabel => L("Bande-annonce avant le film", "Trailer before the film");
    public static string BeforeFilmHint => L("Quand un film part du début, sa bande-annonce gardée sur ton serveur passe d’abord, comme au cinéma. Jamais celles du web, jamais en reprise ni dans un groupe.",
        "When a film starts from the beginning, its trailer kept on your server plays first, as at the cinema. Never those from the web, never when resuming or in a group.");
    /// <summary>The pill over a trailer played before its film.</summary>
    public static string SkipLabel => L("Passer la bande-annonce", "Skip trailer");
    /// <summary>The player's line under the title while the trailer plays.</summary>
    public static string Playing => L("Bande-annonce", "Trailer");

    /// <summary>
    /// Whether <paramref name="film"/> starts after its trailer, with « Bande-annonce avant le film » on: a film (not an
    /// episode, a series' trailer is for its page), from its first second (<paramref name="startAt"/>, a resume never),
    /// alone (never in a group: each device would play its own), read from Jellyfin (a copy kept on this device may be
    /// played without it), and when Jellyfin did not say it has none (a card's item does not say: asked then).
    /// </summary>
    public static bool PlaysFirst(bool enabled, MediaItem film, double startAt, bool inGroup, bool keptCopy) =>
        enabled && film.Type == "Movie" && film.ExtraType is null && startAt < 1 && !inGroup && !keptCopy && film.LocalTrailerCount is not 0;
}
