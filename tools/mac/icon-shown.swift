// The icon macOS shows for an app, as Finder and the Dock draw it, written to a PNG at 256 points (512 px):
//   swift tools/mac/icon-shown.swift <Mira.app> <file.png>
// mac.yml runs it on macOS 15 and macOS 26: the grey tile of macOS 26, if Mira's icon got one, shows here.
import AppKit

let arguments = CommandLine.arguments
guard arguments.count == 3 else { FileHandle.standardError.write("usage: icon-shown.swift <app> <file.png>\n".data(using: .utf8)!); exit(2) }
let icon = NSWorkspace.shared.icon(forFile: arguments[1])
var rect = NSRect(x: 0, y: 0, width: 512, height: 512)
guard let image = icon.cgImage(forProposedRect: &rect, context: nil, hints: nil),
      let png = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else { exit(1) }
try png.write(to: URL(fileURLWithPath: arguments[2]))
print("\(arguments[2]): \(image.width) × \(image.height)")
