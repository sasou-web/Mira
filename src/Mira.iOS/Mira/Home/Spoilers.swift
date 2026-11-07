import Foundation

/// « Hide spoilers » (Settings → Playback, off by default), as Spoilers of Mira.Core on Windows and Mac (cases shared
/// in tests/fixtures/spoilers.json): an episode not watched yet, not even started, shows neither its own picture nor its
/// summary; its name and number stay. Its picture becomes Jellyfin's BlurHash of it, the picture itself never fetched.
enum Spoilers {
    /// This iPhone's setting (Preferences.hideSpoilers), set by Preferences; read by Artwork.of, which has no settings.
    nonisolated(unsafe) static var on = false

    static func hides(_ item: Item, on: Bool = Spoilers.on) -> Bool {
        on && item.isEpisode && item.userData?.played != true && (item.userData?.playbackPositionTicks ?? 0) <= 0
    }
}

extension Spoilers {
    /// The BlurHash of the episode's own still (Primary, else Thumb), as Spoilers.Still of Mira.Core.
    static func stillBlurHash(_ episode: Item) -> String? {
        func hash(_ type: String) -> String? {
            guard let tag = episode.imageTags?.first(where: { $0.key.caseInsensitiveCompare(type) == .orderedSame })?.value else { return nil }
            return episode.imageBlurHashes?.first { $0.key.caseInsensitiveCompare(type) == .orderedSame }?.value[tag]
        }
        let primary = episode.imageTags?.keys.contains { $0.caseInsensitiveCompare("Primary") == .orderedSame } == true
        return primary ? hash("Primary") : hash("Thumb")
    }
}
