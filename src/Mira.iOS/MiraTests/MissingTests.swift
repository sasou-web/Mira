import Foundation
import Testing
@testable import Mira

/// « Télécharger les manquants » of a series' page: tests/fixtures/missing.json, shared with Mira for Windows and Mac
/// (MissingDownloads of Core): the releases put forward for what a click asks, what is searched, how what was asked is going.
@Suite("Télécharger les manquants")
struct MissingTests {
    private final class Bundled {}

    private func fixture() throws -> [String: Any] {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "missing", withExtension: "json"))
        return try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    private func series(_ s: [String: Any]) -> MissingDownloads.Series {
        MissingDownloads.Series(name: s["name"] as? String ?? "", originalTitle: s["originalTitle"] as? String, year: (s["year"] as? NSNumber)?.intValue,
                                before: (s["before"] as? NSNumber)?.intValue ?? 0, query: s["query"] as? String, also: s["also"] as? [String] ?? [])
    }

    @Test("Les releases mises en avant pour chaque cas")
    func choose() throws {
        let cases = try #require(try fixture()["choose"] as? [[String: Any]])
        #expect(!cases.isEmpty)
        for c in cases {
            let name = c["name"] as? String ?? ""
            let a = try #require(c["ask"] as? [String: Any])
            let ask = MissingDownloads.Ask(season: (a["season"] as? NSNumber)?.intValue ?? 0, episodes: (a["episodes"] as? [NSNumber] ?? []).map(\.intValue),
                                           wholeSeason: a["wholeSeason"] as? Bool ?? false)
            let hits: [(search: String, hit: Releases.Hit)] = (c["hits"] as? [[Any]] ?? []).enumerated().map { i, row in
                let gib = (row[1] as? NSNumber)?.doubleValue ?? 0
                return ("s-\(row[4] as? String ?? "")", Releases.Hit(id: "h\(i + 1)", name: row[0] as? String ?? "", size: Int64(gib * 1_073_741_824),
                                                                       seeders: (row[2] as? NSNumber)?.intValue, leechers: 0, source: row[3] as? String, added: nil, category: row[4] as? String))
            }
            let picks = MissingDownloads.choose(ask, series(try #require(c["series"] as? [String: Any])), hits)
            #expect(picks.packs.map(\.entry.hit.name) == c["packs"] as? [String], "\(name)")
            let wanted = (c["episodes"] as? [String: String] ?? [:]).map { (Int($0.key) ?? 0, $0.value) }.sorted { $0.0 < $1.0 }
            let got = picks.episodes.map { ($0.episode, $0.best.entry.hit.name) }
            #expect(got.map(\.0) == wanted.map(\.0) && got.map(\.1) == wanted.map(\.1), "\(name): \(got)")
            #expect(picks.notFound == (c["notFound"] as? [NSNumber] ?? []).map(\.intValue), "\(name)")
            #expect(picks.episodes.allSatisfy { choice in !choice.others.contains(choice.best) && choice.best.search.hasPrefix("s-") }, "\(name)")
        }
    }

    @Test("Trois choix de pack : meilleure qualité, meilleur compromis, le plus léger")
    func packChoices() throws {
        let cases = try #require(try fixture()["packChoices"] as? [[String: Any]])
        #expect(!cases.isEmpty)
        for c in cases {
            let name = c["name"] as? String ?? ""
            let a = try #require(c["ask"] as? [String: Any])
            let ask = MissingDownloads.Ask(season: (a["season"] as? NSNumber)?.intValue ?? 0, episodes: (a["episodes"] as? [NSNumber] ?? []).map(\.intValue),
                                           wholeSeason: a["wholeSeason"] as? Bool ?? false)
            let hits: [(search: String, hit: Releases.Hit)] = (c["hits"] as? [[Any]] ?? []).enumerated().map { i, row in
                let gib = (row[1] as? NSNumber)?.doubleValue ?? 0
                return ("s-\(row[4] as? String ?? "")", Releases.Hit(id: "h\(i + 1)", name: row[0] as? String ?? "", size: Int64(gib * 1_073_741_824),
                                                                       seeders: (row[2] as? NSNumber)?.intValue, leechers: 0, source: row[3] as? String, added: nil, category: row[4] as? String))
            }
            let picks = MissingDownloads.choose(ask, series(try #require(c["series"] as? [String: Any])), hits)
            let got = MissingDownloads.packChoices(picks.packs, episodes: ask.episodes.count)
                .map { "\($0.kind.rawValue) \($0.option.entry.hit.name) \(MissingDownloads.doubtful($0.option, episodes: ask.episodes.count))" }
            let wanted = (c["choices"] as? [[Any]] ?? []).map { "\($0[0] as? String ?? "") \($0[1] as? String ?? "") \($0[2] as? Bool ?? false)" }
            #expect(got == wanted, "\(name): \(got)")
        }
        #expect(MissingDownloads.searchedEnough(MissingDownloads.Picks(packs: [], episodes: [], notFound: [])))
        #expect(MissingDownloads.packKindName(.balanced, french: true) == "Meilleur compromis" && MissingDownloads.packKindName(.smallest, french: false) == "Smallest")
    }

    @Test("Ce qui est cherché, les suites d'épisodes, les textes")
    func words() throws {
        let x = try fixture()
        for q in x["queries"] as? [[String: Any]] ?? [] {
            let got = MissingDownloads.queries(series(q["series"] as? [String: Any] ?? [:]), season: (q["season"] as? NSNumber)?.intValue ?? 0)
            #expect(got == q["queries"] as? [String])
        }
        for r in x["runs"] as? [[Any]] ?? [] {
            let got = MissingDownloads.runs((r[0] as? [NSNumber] ?? []).map(\.intValue))
            let wanted = (r[1] as? [[NSNumber]] ?? []).map { ($0[0].intValue, $0[1].intValue) }
            #expect(got.map(\.from) == wanted.map(\.0) && got.map(\.to) == wanted.map(\.1))
        }
        #expect(MissingDownloads.getEpisodes(6, 9, french: true) == "Télécharger É6–9")
        #expect(MissingDownloads.getEpisodes(6, 6, french: false) == "Get E6")
        #expect(MissingDownloads.getSeason(2, french: true) == "Télécharger la saison 2")
        #expect(MissingDownloads.notFound([9, 11, 12], french: true) == "Rien trouvé pour É9, É11–12.")
        #expect(MissingDownloads.categories(anime: true) == ["anime", "tv"] && MissingDownloads.categories(anime: false) == ["tv"])
        var one = Item(id: "a"); one.parentIndexNumber = 1; one.indexNumber = 12
        var two = Item(id: "b"); two.parentIndexNumber = 2; two.indexNumber = 9
        #expect(MissingDownloads.before([one, two], season: 3) == 21 && MissingDownloads.before([one, two], season: 1) == 0)
    }

    @Test("Une bibliothèque d'animes, un chemin d'un serveur Windows")
    func libraries() {
        #expect(MissingLibraries.animeWords("Animés") && MissingLibraries.animeWords("ANIME") && MissingLibraries.animeWords("アニメ"))
        #expect(!MissingLibraries.animeWords("Dessins animés") && !MissingLibraries.animeWords("Animation"))
        #expect(MissingLibraries.inside(#"C:\Users\Alex\Videos\MEDIAS\ANIME\Frieren"#, #"C:\Users\Alex\Videos\MEDIAS\ANIME"#))
        #expect(!MissingLibraries.inside("/media/animes", "/media/anime"))
    }

    @Test("Où en est ce qui a été demandé")
    func progress() {
        let series = "0123456789abcdef0123456789abcdef"
        func asked(_ id: String, _ episodes: [Int], _ hash: String, state: String = "sent", error: String? = nil) -> RemoteLines.Missing {
            RemoteLines.Missing(id: id, seriesId: "01234567-89ab-cdef-0123-456789abcdef", season: 2, episodes: episodes, name: "x", size: 2_000_000_000, infoHash: hash, state: state, error: error)
        }
        var state = RemoteLines.State(agent: nil, requests: [], controls: [], downloads: [], placed: [], missing: [asked("r1", [6], "aa"), asked("r2", [7], "bb")])
        #expect(MissingDownloads.progress(state, seriesId: series, season: 2, episodes: [6, 7], french: false)?.stage == .waiting)
        #expect(MissingDownloads.progress(state, seriesId: series, season: 3, episodes: nil, french: false) == nil)
        #expect(MissingDownloads.progress(state, seriesId: "other", season: 2, episodes: nil, french: false) == nil)
        state.downloads = [RemoteLines.Download(id: "AA", name: "x", status: "downloading", progress: 80, speed: 1, peers: 1, size: 2_000_000_000, downloaded: 1, eta: 600, error: nil)]
        let going = MissingDownloads.progress(state, seriesId: series, season: 2, episodes: [6, 7], french: false)
        #expect(going?.stage == .downloading && going?.percent == 40)
        #expect(going.map { MissingDownloads.pill($0, french: false) } == "40%")
        state.downloads = []
        state.placed = [RemoteLines.Placed(id: "aa", title: "x", status: "ok", itemId: nil, ok: true), RemoteLines.Placed(id: "bb", title: "x", status: "ok", itemId: nil, ok: true)]
        #expect(MissingDownloads.progress(state, seriesId: series, season: 2, episodes: [6], french: false)?.title == "Adding to your library…")
        state.placed = []
        // Finished and gone from TorLink's list: being placed, never the button again; placed long ago: Jellyfin hasn't listed it.
        var gone = asked("r1", [6], "aa")
        gone.finished = true
        state.missing = [gone]
        #expect(MissingDownloads.progress(state, seriesId: series, season: 2, episodes: [6], french: false)?.stage == .adding)
        gone.placedAt = "2026-11-13T12:00:00.1234567+00:00"
        state.missing = [gone]
        let placedAt = ISO8601DateFormatter().date(from: "2026-11-13T12:00:00Z")!
        let soon = MissingDownloads.progress(state, seriesId: series, season: 2, episodes: [6], now: placedAt.addingTimeInterval(60), french: false)
        #expect(soon?.stage == .placed && soon?.late == false && soon?.line == "Filed. Jellyfin is adding it to the series…")
        #expect(MissingDownloads.progress(state, seriesId: series, season: 2, episodes: [6], now: placedAt.addingTimeInterval(240), french: false)?.late == true)
        #expect(MissingDownloads.lookAgain(sincePlaced: 60) == 10 && MissingDownloads.lookAgain(sincePlaced: 600) == 60)
        state.missing = [asked("r1", [6], "aa", state: "failed", error: "Disk full")]
        let failed = MissingDownloads.progress(state, seriesId: series, season: 2, episodes: nil, french: true)
        #expect(failed?.stage == .failed && failed?.line == "Disk full" && failed?.title == "Le téléchargement a échoué")
    }
}
