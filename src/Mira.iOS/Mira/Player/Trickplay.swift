import Foundation
import UIKit

/// One size of Jellyfin's trickplay pictures (TrickplayInfo): a thumbnail every `interval` ms, `tileWidth` ×
/// `tileHeight` of them in each tile image.
struct TrickplayInfo: Codable, Sendable, Hashable {
    var width: Int?
    var height: Int?
    var tileWidth: Int?
    var tileHeight: Int?
    var thumbnailCount: Int?
    var interval: Int?

    var usable: Bool {
        (width ?? 0) > 0 && (height ?? 0) > 0 && (tileWidth ?? 0) > 0 && (tileHeight ?? 0) > 0 && (thumbnailCount ?? 0) > 0 && (interval ?? 0) > 0
    }
}

/// Where a thumbnail is in its tile image, in that image's pixels.
struct TrickplayCell: Sendable, Hashable {
    let tile: Int
    let x: Int
    let y: Int
    let width: Int
    let height: Int
}

/// The trickplay pictures of a title, shown over the timeline while the finger moves on it, as Mira web, Windows and
/// Mac show them (Trickplay.cs of Core): which tile image holds the moment pointed at, and where in it. Jellyfin makes
/// them when its library option asks for them (« Extract trickplay images »).
struct TrickplaySet: Sendable, Hashable {
    /// The thumbnails' width Mira asks for: the smallest at least this wide, else the widest.
    static let wanted = 240

    let itemId: String
    let sourceId: String
    let info: TrickplayInfo

    /// The set of the file played (else the first), at the size closest above `wanted`; nil without one.
    static func pick(_ itemId: String, _ trickplay: [String: [String: TrickplayInfo]]?, sourceId: String?) -> TrickplaySet? {
        guard let trickplay, !trickplay.isEmpty else { return nil }
        let played = sourceId.flatMap { id in trickplay[id].map { (id, $0) } }
        let first = trickplay.keys.sorted().first.map { ($0, trickplay[$0] ?? [:]) }
        guard let (source, sizes) = played ?? first else { return nil }
        let usable = sizes.values.filter(\.usable)
        let info = usable.filter { ($0.width ?? 0) >= wanted }.min { ($0.width ?? 0) < ($1.width ?? 0) }
            ?? usable.max { ($0.width ?? 0) < ($1.width ?? 0) }
        return info.map { TrickplaySet(itemId: itemId, sourceId: source, info: $0) }
    }

    func cell(_ seconds: Double) -> TrickplayCell {
        let width = info.width ?? 1, height = info.height ?? 1, columns = info.tileWidth ?? 1, rows = info.tileHeight ?? 1
        let at = max(0, seconds.isFinite ? seconds : 0) * 1000 / Double(info.interval ?? 1000)
        let index = Int(min(Double((info.thumbnailCount ?? 1) - 1), max(0, at.rounded(.down))))
        let perTile = columns * rows, place = index % perTile
        return TrickplayCell(tile: index / perTile, x: place % columns * width, y: place / columns * height, width: width, height: height)
    }

    /// The address of a tile image under the server's, and its query.
    func tilePath(_ tile: Int) -> (path: String, query: [String: String?]) {
        ("Videos/\(itemId)/Trickplay/\(info.width ?? 0)/\(tile).jpg", ["mediaSourceId": sourceId])
    }
}

/// The tile images of one title while it plays: fetched when a moment in them is first pointed at, decoded off the
/// main thread, the last few kept (a whole tile is a few megabytes once decoded).
@MainActor
final class TrickplayTiles {
    let set: TrickplaySet
    private let client: JellyfinClient
    private var ready: [(tile: Int, image: CGImage)] = []
    private var loading: Set<Int> = []
    private var failed: Set<Int> = []
    /// A tile asked for is ready: what points at it can be drawn again.
    var loaded: (() -> Void)?

    init(set: TrickplaySet, client: JellyfinClient) {
        self.set = set
        self.client = client
    }

    /// The thumbnail of `seconds`; nil while its tile comes (it is asked for then), or when it could not be had.
    func thumbnail(_ seconds: Double) -> UIImage? {
        let cell = set.cell(seconds)
        if let index = ready.firstIndex(where: { $0.tile == cell.tile }) {
            let hit = ready.remove(at: index)
            ready.append(hit)
            let box = CGRect(x: cell.x, y: cell.y, width: cell.width, height: cell.height)
            return hit.image.cropping(to: box.intersection(CGRect(x: 0, y: 0, width: hit.image.width, height: hit.image.height))).map { UIImage(cgImage: $0) }
        }
        if !failed.contains(cell.tile), !loading.contains(cell.tile) {
            loading.insert(cell.tile)
            load(cell.tile)
        }
        return nil
    }

    private func load(_ tile: Int) {
        let client = client, (path, query) = set.tilePath(tile)
        Task {
            let image = await Task.detached(priority: .userInitiated) { () -> CGImage? in
                guard let data = try? await client.send("GET", path, query, timeout: 10), let source = UIImage(data: data) else { return nil }
                return await source.byPreparingForDisplay()?.cgImage ?? source.cgImage
            }.value
            loading.remove(tile)
            guard let image else {
                failed.insert(tile)
                return
            }
            ready.append((tile, image))
            while ready.count > 2 { ready.removeFirst() }
            loaded?()
        }
    }
}
