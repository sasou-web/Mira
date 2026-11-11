import Foundation
import Testing
@testable import Mira

@Suite("Sous-titres décalés")
struct SubtitleCuesTests {
    @Test("Les lignes d'un WebVTT de Jellyfin, au bon moment")
    func readsJellyfinWebVTT() {
        let cues = SubtitleCues(webVTT: "WEBVTT\n\n00:00:10.000 --> 00:00:10.900\nLine at <i>10</i> s\n\n01:02.500 --> 01:04.000 line:90%\n{\\an8}Up &amp; there\nsecond\n")
        #expect(cues.cues.count == 2)
        #expect(cues.text(at: 9.9) == nil)
        #expect(cues.text(at: 10.2) == "Line at 10 s")
        #expect(cues.text(at: 10.95) == nil)
        #expect(cues.text(at: 63) == "Up & there\nsecond")
    }

    @Test("Deux lignes à la fois, dans leur ordre ; SRT et fins de ligne Windows")
    func overlappingAndSrt() {
        let cues = SubtitleCues(webVTT: "1\r\n00:00:01,000 --> 00:00:05,000\r\nFirst\r\n\r\n2\r\n00:00:02,000 --> 00:00:03,000\r\nSecond\r\n")
        #expect(cues.text(at: 2.5) == "First\nSecond")
        #expect(cues.text(at: 4) == "First")
    }

    @Test("Une longue ligne (un panneau, une chanson) reste pendant toutes les courtes qui suivent")
    func longLineStaysOn() {
        // As Jellyfin 12.1 writes an ASS sign held 27 s over twelve lines of dialogue: one cue, kept whole.
        var webVTT = "WEBVTT\n\n00:00:01.000 --> 00:00:28.000 line:20%\nTokyo, 2034\n"
        for line in 1...12 {
            let start = line * 2
            webVTT += "\n00:00:\(start < 10 ? "0" : "")\(start).000 --> 00:00:\(start + 1 < 10 ? "0" : "")\(start + 1).500\nLine \(line)\n"
        }
        let cues = SubtitleCues(webVTT: webVTT)
        #expect(cues.cues.count == 13)
        #expect(cues.text(at: 14.5) == "Tokyo, 2034\nLine 7")
        #expect(cues.text(at: 18.5) == "Tokyo, 2034\nLine 9")
        #expect(cues.text(at: 26) == "Tokyo, 2034")
        #expect(cues.text(at: 28.5) == nil)
    }

    @Test("Le décalage dit comme le menu")
    func delayName() {
        #expect(SubtitleCues.delayName(0.5).hasPrefix("+"))
        #expect(SubtitleCues.delayName(-1).hasPrefix("−"))
        #expect(!SubtitleCues.delayName(0).hasPrefix("+"))
    }
}
