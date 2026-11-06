import Foundation

/// Where « Trailer » of a title's page leads: its own trailer on Jellyfin, a page on the web, or nowhere.
enum TrailerSource: String, Sendable {
    case none, jellyfin, web
}

/// « Trailer » in « … » of a title's page, as Windows and Mac (Trailers of Core; cases of tests/fixtures/trailers.json).
/// A trailer kept beside the title (a « trailers » folder, « …-trailer.mkv ») plays in Mira's player, from Jellyfin
/// alone. A trailer on the web (TMDb gives YouTube's to Jellyfin) opens in Safari at the touch, never in Mira: no web
/// view, nothing of it loaded before, and Mira sends it nothing.
enum Trailers {
    static func source(_ item: Item) -> TrailerSource {
        guard item.type == "Movie" || item.type == "Series" else { return .none }
        if (item.localTrailerCount ?? 0) > 0 { return .jellyfin }
        return webLink(item) == nil ? .none : .web
    }

    /// The first trailer on the web that is a plain web address: https, or http made https; never another scheme, a
    /// name and password in the address, or a host that is no name.
    static func webLink(_ item: Item) -> URL? {
        for trailer in item.remoteTrailers ?? [] {
            if let link = webLink(trailer.url) { return link }
        }
        return nil
    }

    static func webLink(_ url: String?) -> URL? {
        guard let raw = url?.trimmingCharacters(in: .whitespacesAndNewlines), !raw.isEmpty,
              var parts = URLComponents(string: raw), let scheme = parts.scheme?.lowercased(), scheme == "https" || scheme == "http",
              parts.user == nil, parts.password == nil, let host = parts.host, host.contains("."),
              !host.contains(":"), !host.allSatisfy({ $0.isNumber || $0 == "." }) else { return nil }
        if scheme == "http", parts.port == 80 { parts.port = nil }
        parts.scheme = "https"
        return parts.url
    }

    /// The site a web trailer is on, as the menu names it: « YouTube », else its host without « www. ».
    static func site(_ link: URL) -> String {
        let host = (link.host() ?? "").lowercased()
        if host == "youtu.be" || host == "youtube.com" || host.hasSuffix(".youtube.com") { return "YouTube" }
        if host == "vimeo.com" || host.hasSuffix(".vimeo.com") { return "Vimeo" }
        return host.hasPrefix("www.") ? String(host.dropFirst(4)) : host
    }
}
