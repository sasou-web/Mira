import CoreTransferable
import Foundation
import UIKit
import UniformTypeIdentifiers

/// Settings → « Share a report »: a text file the person sends by hand (Messages, Discord, mail) when something goes
/// wrong; Mira never sends it. As DiagnosticReport of Mira for Windows and Mac: versions, the kind of network Jellyfin is
/// reached by (never its address), this app's facts and its last playbacks, in English, every line through `redact`
/// (cases shared in tests/fixtures/diagnostics.json).
enum Diagnostics {
    /// The last playbacks of this run of Mira, ten at most, never a title nor a path.
    @MainActor private(set) static var playbacks: [String] = []
    static let kept = 10

    @MainActor static func record(_ item: Item, source: MediaSource?, server: URL?, how: String, error: String? = nil) {
        let video = source?.mediaStreams?.first { $0.type == "Video" }
        let audio = source?.mediaStreams?.first { $0.type == "Audio" }
        var parts = [stamp(Date()) + " " + (item.type ?? "Video")]
        if let container = source?.container { parts.append(container) }
        if let video { parts.append([video.codec, video.height.map { "\($0)p" }, video.videoRangeType].compactMap { $0 }.joined(separator: " ")) }
        if let audio { parts.append([audio.codec, audio.channels.map { "\($0)ch" }].compactMap { $0 }.joined(separator: " ")) }
        parts.append(how)
        if let network = network(server?.absoluteString) { parts.append(network) }
        if let error { parts.append("failed: \(error)") }
        playbacks.append(parts.filter { !$0.isEmpty }.joined(separator: " · "))
        if playbacks.count > kept { playbacks.removeFirst(playbacks.count - kept) }
    }

    static func fileName(_ now: Date) -> String {
        let format = DateFormatter()
        format.locale = Locale(identifier: "en_US_POSIX")
        format.dateFormat = "yyyyMMdd-HHmm"
        return "mira-report-\(format.string(from: now)).txt"
    }

    private static func stamp(_ date: Date) -> String {
        let format = DateFormatter()
        format.locale = Locale(identifier: "en_US_POSIX")
        format.dateFormat = "yyyy-MM-dd HH:mm"
        return format.string(from: date)
    }

    /// « this computer », « home network », « Tailscale » or « Internet »: how Jellyfin is reached, never where.
    static func network(_ server: String?) -> String? {
        guard let server, let url = URL(string: server), var host = url.host?.lowercased(), !host.isEmpty else { return nil }
        host = host.trimmingCharacters(in: CharacterSet(charactersIn: "[]."))
        if host == "localhost" || host.hasPrefix("127.") || host == "::1" { return "this computer" }
        if host.hasSuffix(".ts.net") || host.hasPrefix("fd7a:115c:a1e0:") { return "Tailscale" }
        let parts = host.split(separator: ".").compactMap { Int($0) }
        if parts.count == 4, host.split(separator: ".").count == 4 {
            if parts[0] == 100, (64...127).contains(parts[1]) { return "Tailscale" }
            if parts[0] == 10 || parts[0] == 192 && parts[1] == 168 || parts[0] == 172 && (16...31).contains(parts[1]) || parts[0] == 169 && parts[1] == 254 { return "home network" }
            return "Internet"
        }
        if host.contains(":") { return host.hasPrefix("fe80:") || host.hasPrefix("fc") || host.hasPrefix("fd") ? "home network" : "Internet" }
        let home = [".local", ".lan", ".home", ".home.arpa", ".internal", ".localdomain", ".fritz.box", ".localhost"]
        return !host.contains(".") || home.contains { host.hasSuffix($0) } ? "home network" : "Internet"
    }

    private static let rules: [(String, String)] = [
        (#"\b[a-z][a-z0-9+.-]*://[^\s"'<>]+"#, "[address]"),
        (#"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}"#, "[e-mail]"),
        (#"((?:api_?key|token|password|access_token|x-emby-token)\s*[=:]\s*"?)[^\s",;&]+"#, "$1[hidden]"),
        (#"((?:[A-Z]:)?[\\/](?:Users|home)[\\/])[^\\/\s"']+"#, "$1…"),
        (#"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?\b"#, "[address]"),
        (#"\b(?:[0-9a-f]{1,4}:){3,7}[0-9a-f]{1,4}\b|\b[0-9a-f]{1,4}(?::[0-9a-f]{1,4})*::(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4})*)?"#, "[address]"),
        (#"\b(?=[A-Za-z_\-+/]*\d)[A-Za-z0-9_\-+/]{24,}={0,2}"#, "[hidden]"),
    ]

    /// What may never leave in a report, replaced by its kind: web addresses, e-mails, IP addresses, keys and tokens,
    /// the user's folder. The same rules as DiagnosticReport.Redact of Mira for Windows and Mac.
    static func redact(_ text: String) -> String {
        var text = text
        for (pattern, template) in rules {
            guard let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]) else { continue }
            text = regex.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text), withTemplate: template)
        }
        return text
    }

    /// The report. `names` are words that name the person (the Jellyfin account, this iPhone's name), written « [name] ».
    static func build(version: String, server: String?, jellyfin: String?, facts: [(String, String?)], playbacks: [String], now: Date, names: [String?]) -> String {
        var text = "Mira for iPhone \(version) · diagnostic report\n"
        text += "Written by Mira on this device and sent by nobody but you. No password, token, address, file name nor account name.\n\n"
        func line(_ name: String, _ value: String?) {
            let value = value?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            text += "\(name): \(value.isEmpty ? "-" : value)\n"
        }
        line("Made", stamp(now))
        line("Language", Locale.preferredLanguages.first)
        text += "\n"
        line("Jellyfin", jellyfin)
        line("Reached by", network(server))
        for (name, value) in facts { line(name, value) }
        text += "\nLast playbacks (this run of Mira)\n"
        if playbacks.isEmpty { text += "  none\n" }
        for playback in playbacks { text += "  \(playback)\n" }
        var report = redact(text)
        let words = Set(names.compactMap { $0 }.filter { $0.count >= 2 }.map { $0.lowercased() }).sorted { $0.count > $1.count }
        for word in words {
            let pattern = #"(?<![\p{L}\p{N}])"# + NSRegularExpression.escapedPattern(for: word) + #"(?![\p{L}\p{N}])"#
            guard let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]) else { continue }
            report = regex.stringByReplacingMatches(in: report, range: NSRange(report.startIndex..., in: report), withTemplate: "[name]")
        }
        return report
    }
}

/// The report as a file for ShareLink, written only when the person shares it.
struct DiagnosticFile: Transferable, Sendable {
    let name: String
    let make: @Sendable () async -> String

    static var transferRepresentation: some TransferRepresentation {
        FileRepresentation(exportedContentType: .plainText) { file in
            let url = FileManager.default.temporaryDirectory.appending(path: file.name)
            try await file.make().write(to: url, atomically: true, encoding: .utf8)
            return SentTransferredFile(url)
        }
    }
}
