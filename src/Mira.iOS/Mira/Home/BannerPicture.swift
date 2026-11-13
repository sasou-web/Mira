import SwiftUI
import UIKit

/// The picture of a banner slide. The slide is taller than wide on an iPhone, so a landscape backdrop fills it by its
/// height and is enlarged far more than on a computer: a thin banner (AniList's are 1900 × 400) or a small backdrop
/// came out blurred, JPEG blocks and all. With the sizes Jellyfin gives for each picture (Items/{id}/Images), the
/// first backdrop that stays sharp is taken; when none does, the sharpest of the backdrops, the episode's own still,
/// the thumb and the poster, a little against the last ones (titles written on them).
enum BannerPicture {
    /// A slide's picture: what to ask Jellyfin, its shape, and its own width in pixels when Jellyfin said it.
    struct Choice: Equatable {
        let art: Artwork
        let aspect: CGFloat
        let pixels: CGFloat?
    }

    /// The picture is drawn this much wider than its slide at least: swiped, it moves slower than the slide (parallax)
    /// and no edge ever shows.
    static let parallax: CGFloat = 1.4
    /// Enlarged up to this much, a picture still looks sharp on an iPhone's screen (a 1920 backdrop on a slide of
    /// 393 × 511 points is enlarged 1.42 times).
    static let sharp: CGFloat = 1.6

    /// The size (points) a picture of `aspect` (width ÷ height) is drawn at to fill a slide of `slide`.
    static func drawn(_ slide: CGSize, aspect: CGFloat) -> CGSize {
        let aspect = max(0.1, aspect)
        let width = max(slide.width * parallax, slide.height * aspect)
        return CGSize(width: width, height: width / aspect)
    }

    /// How much a picture of `width` × `height` pixels is enlarged on a slide of `slide` points at `scale` pixels per
    /// point (1: pixel for pixel).
    static func enlargement(width: Int, height: Int, slide: CGSize, scale: CGFloat) -> CGFloat {
        guard width > 0, height > 0 else { return .infinity }
        return drawn(slide, aspect: CGFloat(width) / CGFloat(height)).width * max(1, scale) / CGFloat(width)
    }

    /// The picture of `entry` on a slide of `slide` points at `scale`. `hidingSpoilers`: an episode not begun never
    /// shows its own still (Spoilers).
    static func choose(_ entry: BannerEntry, slide: CGSize, scale: CGFloat, hidingSpoilers: Bool = Spoilers.on) -> Choice? {
        let item = entry.item
        struct Candidate { let itemId: String; let info: ImageInfo; let weight: CGFloat }
        func of(_ type: String, _ infos: [ImageInfo]) -> [ImageInfo] {
            infos.filter { $0.imageType?.caseInsensitiveCompare(type) == .orderedSame && !($0.imageTag ?? "").isEmpty && ($0.width ?? 0) > 0 && ($0.height ?? 0) > 0 }
                .sorted { ($0.imageIndex ?? 0) < ($1.imageIndex ?? 0) }
        }
        var candidates = of("Backdrop", entry.pictures).map { Candidate(itemId: item.id, info: $0, weight: 1) }
        if let play = entry.play, play.isEpisode, !Spoilers.hides(play, on: hidingSpoilers) {
            candidates += of("Primary", entry.playPictures).map { Candidate(itemId: play.id, info: $0, weight: 1.05) }
        }
        candidates += of("Thumb", entry.pictures).map { Candidate(itemId: item.id, info: $0, weight: 1.15) }
        if !item.isEpisode { candidates += of("Primary", entry.pictures).map { Candidate(itemId: item.id, info: $0, weight: 1.3) } }
        func enlarged(_ candidate: Candidate) -> CGFloat {
            enlargement(width: candidate.info.width ?? 0, height: candidate.info.height ?? 0, slide: slide, scale: scale)
        }
        let picked = candidates.first { enlarged($0) <= sharp } ?? candidates.min { enlarged($0) * $0.weight < enlarged($1) * $1.weight }
        guard let picked, let tag = picked.info.imageTag, let type = picked.info.imageType else {
            // Jellyfin said nothing of its pictures (not asked yet, or refused): its first backdrop, taken as 16:9.
            return Artwork.of(item, .backdrop).map { Choice(art: $0, aspect: 16 / 9, pixels: nil) }
        }
        let width = CGFloat(picked.info.width ?? 0), height = CGFloat(picked.info.height ?? 0)
        let art = Artwork(itemId: picked.itemId, type: type, tag: tag, blurHash: picked.info.blurHash, index: picked.info.imageIndex ?? 0)
        return Choice(art: art, aspect: width / height, pixels: width)
    }

    /// The width (points) a chosen picture is asked at: as drawn, never more than its own pixels, 3840 at most (the
    /// largest Jellyfin keeps).
    static func askedWidth(_ choice: Choice, slide: CGSize, scale: CGFloat) -> CGFloat {
        let drawnWidth = drawn(slide, aspect: choice.aspect).width
        let limit = min(choice.pixels ?? 3840, 3840) / max(1, scale)
        return min(drawnWidth, limit)
    }
}

/// The tint of a banner, drawn from its picture: its most present colour, lifted towards white so that it reads on
/// black and behind the dark Play glyph (ArtworkPalette of Mira.Core, the same numbers, as Windows does).
enum ArtworkAccent {
    struct Tint: Sendable, Hashable {
        let red: UInt8, green: UInt8, blue: UInt8
        var color: Color { Color(.sRGB, red: Double(red) / 255, green: Double(green) / 255, blue: Double(blue) / 255) }
    }

    /// No colour in the picture (black, white, grey): Mira's own light grey.
    static let neutral = Tint(red: 245, green: 245, blue: 247)

    /// The tint of pixels in BGRA order (ArtworkPalette.Extract).
    static func extract(_ bgra: [UInt8]) -> Tint {
        var bins: [Int: (weight: Double, r: Double, g: Double, b: Double)] = [:]
        var order: [Int] = []
        var i = 0
        while i + 3 < bgra.count {
            defer { i += 4 }
            if bgra[i + 3] < 200 { continue }
            let r = Double(bgra[i + 2]), g = Double(bgra[i + 1]), b = Double(bgra[i])
            let high = max(r, g, b), low = min(r, g, b)
            if high < 32 || low > 225 || high - low < 14 { continue }
            let weight = 0.25 + (high - low) / max(1, high)
            let key = (Int(r) / 32) << 6 | (Int(g) / 32) << 3 | Int(b) / 32
            let bin = bins[key] ?? (0, 0, 0, 0)
            if bins[key] == nil { order.append(key) }
            bins[key] = (bin.weight + weight, bin.r + r * weight, bin.g + g * weight, bin.b + b * weight)
        }
        // The first of the heaviest, in the order they came (MaxBy of .NET over a dictionary filled in that order).
        var dominant: (weight: Double, r: Double, g: Double, b: Double)?
        for key in order where dominant == nil || bins[key]!.weight > dominant!.weight { dominant = bins[key] }
        guard let dominant else { return neutral }
        let channels = [dominant.r / dominant.weight, dominant.g / dominant.weight, dominant.b / dominant.weight]
        let lowest = channels.min() ?? 0
        let lift = max(0.34, (176 - lowest) / max(1, 255 - lowest))
        func tint(_ channel: Double) -> UInt8 { UInt8(min(255, max(0, (channel + (255 - channel) * lift).rounded(.toNearestOrEven)))) }
        return Tint(red: tint(channels[0]), green: tint(channels[1]), blue: tint(channels[2]))
    }

    /// The tint of a picture, read from it drawn at 64 × 48.
    static func of(_ image: UIImage) -> Tint {
        guard let picture = image.cgImage else { return neutral }
        let width = 64, height = 48
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        let drawn = pixels.withUnsafeMutableBytes { buffer -> Bool in
            guard let context = CGContext(data: buffer.baseAddress, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4,
                                          space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
                                          bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) else { return false }
            context.interpolationQuality = .medium
            context.draw(picture, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        return drawn ? extract(pixels) : neutral
    }
}

/// What a banner slide says, as Windows's banner (UpdateHeroCopy): of the episode to resume or watch next when there
/// is one, of the film or series otherwise; never the series' own year or count of seasons over an episode (a series
/// identified as another one, its 1993 OVA, said « 1993 · 1 season » over « Watch S6 · E9 »).
enum BannerText {
    /// What the facts and the summary speak of: the episode, else the title.
    static func shown(_ entry: BannerEntry) -> Item {
        if let play = entry.play, play.isEpisode { return play }
        return entry.item
    }

    /// The pills: year, « S6 · E9 » or Series or Movie, length (not for a series), Jellyfin's rating (not for an episode).
    static func facts(_ entry: BannerEntry) -> [String] {
        let item = shown(entry)
        var facts: [String] = []
        if let year = item.productionYear { facts.append(String(year)) }
        if item.isEpisode {
            let code = ItemText.episodeCode(item)
            if !code.isEmpty { facts.append(code) }
        } else {
            facts.append(item.isSeries ? String(localized: "Series") : String(localized: "Movie"))
        }
        if !item.isSeries, let ticks = item.runTimeTicks, ticks > 0 { facts.append(ItemText.duration(seconds: ticks / ItemText.ticksPerSecond)) }
        if !item.isEpisode, let rating = item.communityRating, rating > 0 {
            facts.append("★ " + rating.formatted(.number.precision(.fractionLength(1))))
        }
        return facts
    }

    /// Two lines of summary: the episode's, unless empty or hidden (Hide spoilers), then its series'.
    static func overview(_ entry: BannerEntry, hidingSpoilers: Bool = Spoilers.on) -> String {
        let item = shown(entry)
        let own = OverviewText.plain(item.overview, flat: true)
        if !own.isEmpty, !Spoilers.hides(item, on: hidingSpoilers) { return own }
        return item.id == entry.item.id ? "" : OverviewText.plain(entry.item.overview, flat: true)
    }

    /// Play's words, as on Windows: Resume when begun, Watch otherwise.
    static func playLabel(_ play: Item) -> String {
        ItemText.progress(play) > 0 ? String(localized: "Resume") : String(localized: "Watch")
    }
}
