// Trickplay pictures for the stand-in Jellyfin of the iPhone's tests (mock-jellyfin.mjs), as Jellyfin makes them: a
// thumbnail every 2 s, 10 × 10 to a tile image (rows left empty cut), and the TrickplayInfo that describes them. Read
// by AVFoundation on the macOS runner (ios.yml), nothing to install.
//   xcrun swift tools/ios/trickplay.swift <video> <folder> [width]
import AVFoundation
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

let arguments = CommandLine.arguments
guard arguments.count >= 3 else {
    print("Usage: swift trickplay.swift <video> <folder> [width]")
    exit(1)
}
let folder = URL(fileURLWithPath: arguments[2])
let width = arguments.count > 3 ? Int(arguments[3]) ?? 320 : 320
let interval = 2.0, columns = 10, rows = 10

let asset = AVURLAsset(url: URL(fileURLWithPath: arguments[1]))
let duration = try await asset.load(.duration).seconds
guard let track = try await asset.loadTracks(withMediaType: .video).first else {
    print("No video in \(arguments[1])")
    exit(1)
}
let natural = try await track.load(.naturalSize).applying(try await track.load(.preferredTransform))
let height = Int((Double(width) * abs(natural.height) / abs(natural.width) / 2).rounded()) * 2
let generator = AVAssetImageGenerator(asset: asset)
generator.appliesPreferredTrackTransform = true
generator.maximumSize = CGSize(width: width, height: height)
generator.requestedTimeToleranceBefore = CMTime(seconds: 0.5, preferredTimescale: 600)
generator.requestedTimeToleranceAfter = CMTime(seconds: 0.5, preferredTimescale: 600)

let count = max(1, Int(duration / interval))
let perTile = columns * rows
let target = folder.appendingPathComponent(String(width))
try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
for tile in 0..<(count + perTile - 1) / perTile {
    let inTile = min(perTile, count - tile * perTile)
    let usedRows = (inTile + columns - 1) / columns
    let canvas = CGContext(data: nil, width: columns * width, height: usedRows * height, bitsPerComponent: 8, bytesPerRow: 0,
                           space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)!
    canvas.setFillColor(CGColor(gray: 0, alpha: 1))
    canvas.fill(CGRect(x: 0, y: 0, width: columns * width, height: usedRows * height))
    for place in 0..<inTile {
        let index = tile * perTile + place
        let (image, _) = try await generator.image(at: CMTime(seconds: Double(index) * interval + interval / 2, preferredTimescale: 600))
        // Core Graphics counts rows from the bottom.
        let x = place % columns * width, y = (usedRows - 1 - place / columns) * height
        canvas.draw(image, in: CGRect(x: x, y: y, width: width, height: height))
    }
    let file = target.appendingPathComponent("\(tile).jpg") as CFURL
    let destination = CGImageDestinationCreateWithURL(file, UTType.jpeg.identifier as CFString, 1, nil)!
    CGImageDestinationAddImage(destination, canvas.makeImage()!, [kCGImageDestinationLossyCompressionQuality: 0.8] as CFDictionary)
    guard CGImageDestinationFinalize(destination) else {
        print("Could not write \(file)")
        exit(1)
    }
}
let info: [String: Any] = [
    "Width": width, "Height": height, "TileWidth": columns, "TileHeight": rows, "ThumbnailCount": count,
    "Interval": Int(interval * 1000), "Bandwidth": 0,
]
try JSONSerialization.data(withJSONObject: info).write(to: folder.appendingPathComponent("info.json"))
print("\(count) thumbnails of \(width) × \(height), \((count + perTile - 1) / perTile) tile(s), \(Int(duration)) s")
