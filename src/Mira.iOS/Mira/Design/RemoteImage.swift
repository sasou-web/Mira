import SwiftUI
import UIKit

/// Pictures of Jellyfin, decoded off the main thread at the size they are shown, and kept in memory a while (on disk
/// too, by the URL cache of `JellyfinClient.session`).
actor ImagePipeline {
    static let shared = ImagePipeline()
    private let memory = NSCache<NSURL, UIImage>()
    private var running: [URL: Task<UIImage?, Never>] = [:]

    init() { memory.totalCostLimit = 160 << 20 }

    func cached(_ url: URL) -> UIImage? { memory.object(forKey: url as NSURL) }

    /// The picture at `url`, made `pixels` wide (the picture's own width when smaller).
    func image(_ url: URL, pixels: CGFloat) async -> UIImage? {
        if let image = memory.object(forKey: url as NSURL) { return image }
        if let task = running[url] { return await task.value }
        let task = Task<UIImage?, Never> {
            guard let (data, response) = try? await JellyfinClient.session.data(from: url),
                  (response as? HTTPURLResponse)?.statusCode == 200, let image = UIImage(data: data) else { return nil }
            guard image.size.width > pixels else { return image.preparingForDisplay() ?? image }
            let target = CGSize(width: pixels, height: pixels * image.size.height / max(1, image.size.width))
            return image.preparingThumbnail(of: target) ?? image
        }
        running[url] = task
        let image = await task.value
        running[url] = nil
        if let image { memory.setObject(image, forKey: url as NSURL, cost: Int(image.size.width * image.size.height * image.scale * image.scale * 4)) }
        return image
    }
}

/// A picture box: the blurred placeholder at once, the picture fading in once loaded (at once when already in
/// memory), or the title's name when Jellyfin has no picture.
struct RemoteImage: View {
    let url: URL?
    var blurHash: String?
    var label: String = ""
    var width: CGFloat

    @State private var image: UIImage?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.displayScale) private var displayScale

    var body: some View {
        ZStack {
            Palette.raised
            if let placeholder = blurHash.flatMap(BlurHash.placeholder) {
                Image(uiImage: placeholder).resizable()
            } else if url == nil, !label.isEmpty {
                Text(label)
                    .font(MiraFont.subheadline)
                    .foregroundStyle(Palette.muted)
                    .multilineTextAlignment(.center)
                    .padding(10)
            }
            if let image {
                Image(uiImage: image).resizable().scaledToFill().transition(.opacity)
            }
        }
        .clipped()
        .task(id: url) {
            guard let url else { image = nil; return }
            if let cached = await ImagePipeline.shared.cached(url) { image = cached; return }
            let loaded = await ImagePipeline.shared.image(url, pixels: width * displayScale)
            withAnimation(reduceMotion ? nil : .easeOut(duration: 0.24)) { image = loaded }
        }
    }
}

/// BlurHash (blurha.sh, the placeholders Jellyfin keeps for each picture), decoded as the reference implementation does.
enum BlurHash {
    @MainActor private static var placeholders: [String: UIImage] = [:]

    /// A small picture of the hash, made once (a row scrolled back shows it again at once).
    @MainActor static func placeholder(_ hash: String) -> UIImage? {
        if let kept = placeholders[hash] { return kept }
        guard let made = Self.image(hash, size: CGSize(width: 24, height: 24)) else { return nil }
        if placeholders.count > 600 { placeholders.removeAll() }
        placeholders[hash] = made
        return made
    }

    static func image(_ hash: String, size: CGSize, punch: Float = 1) -> UIImage? {
        let characters = Array(hash)
        guard characters.count >= 6, let sizeFlag = decode83(characters[0...0]) else { return nil }
        let numY = sizeFlag / 9 + 1, numX = sizeFlag % 9 + 1
        guard characters.count == 4 + 2 * numX * numY, let quantisedMax = decode83(characters[1...1]) else { return nil }
        let maximum = Float(quantisedMax + 1) / 166
        var colours: [(Float, Float, Float)] = []
        for i in 0..<(numX * numY) {
            if i == 0 {
                guard let value = decode83(characters[2..<6]) else { return nil }
                colours.append((linear(value >> 16), linear((value >> 8) & 255), linear(value & 255)))
            } else {
                guard let value = decode83(characters[(4 + i * 2)..<(6 + i * 2)]) else { return nil }
                func ac(_ v: Int) -> Float { signPow((Float(v) - 9) / 9, 2) * maximum * punch }
                colours.append((ac(value / (19 * 19)), ac((value / 19) % 19), ac(value % 19)))
            }
        }
        let width = Int(size.width), height = Int(size.height)
        var pixels = [UInt8](repeating: 255, count: width * height * 4)
        for y in 0..<height {
            for x in 0..<width {
                var (r, g, b): (Float, Float, Float) = (0, 0, 0)
                for j in 0..<numY {
                    for i in 0..<numX {
                        let basis = cos(Float.pi * Float(x) * Float(i) / Float(width)) * cos(Float.pi * Float(y) * Float(j) / Float(height))
                        let colour = colours[i + j * numX]
                        r += colour.0 * basis; g += colour.1 * basis; b += colour.2 * basis
                    }
                }
                let offset = (y * width + x) * 4
                pixels[offset] = sRGB(r); pixels[offset + 1] = sRGB(g); pixels[offset + 2] = sRGB(b)
            }
        }
        guard let provider = CGDataProvider(data: Data(pixels) as CFData),
              let cgImage = CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                                    space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue),
                                    provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent) else { return nil }
        return UIImage(cgImage: cgImage)
    }

    private static let alphabet = Array("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~")

    static func decode83(_ part: ArraySlice<Character>) -> Int? {
        var value = 0
        for character in part {
            guard let digit = alphabet.firstIndex(of: character) else { return nil }
            value = value * 83 + digit
        }
        return value
    }

    private static func linear(_ value: Int) -> Float {
        let v = Float(value) / 255
        return v <= 0.04045 ? v / 12.92 : pow((v + 0.055) / 1.055, 2.4)
    }

    private static func sRGB(_ value: Float) -> UInt8 {
        let v = max(0, min(1, value))
        return v <= 0.0031308 ? UInt8(v * 12.92 * 255 + 0.5) : UInt8((1.055 * pow(v, 1 / 2.4) - 0.055) * 255 + 0.5)
    }

    private static func signPow(_ value: Float, _ exponent: Float) -> Float { copysign(pow(abs(value), exponent), value) }
}
