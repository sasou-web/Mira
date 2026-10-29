import Foundation
import Testing
import UIKit
@testable import Mira

@Suite("Adresse du serveur")
struct ServerAddressTests {
    @Test("Une adresse sans schéma : HTTPS, puis le port de Jellyfin, puis HTTP")
    func bareAddress() throws {
        #expect(try ServerAddress.candidates("192.168.1.20").map(\.absoluteString) == ["https://192.168.1.20", "http://192.168.1.20:8096", "http://192.168.1.20"])
        #expect(try ServerAddress.candidates("  jellyfin.example.com ").map(\.absoluteString) == ["https://jellyfin.example.com", "http://jellyfin.example.com:8096", "http://jellyfin.example.com"])
    }

    @Test("Un port écrit : HTTP d'abord, sauf 443 et 8920")
    func explicitPort() throws {
        #expect(try ServerAddress.candidates("nas:8096").map(\.absoluteString) == ["http://nas:8096", "https://nas:8096"])
        #expect(try ServerAddress.candidates("nas:8920").map(\.absoluteString) == ["https://nas:8920", "http://nas:8920"])
        #expect(ServerAddress.explicitPort("[::1]") == nil)
        #expect(ServerAddress.explicitPort("pc:99999") == nil)
    }

    @Test("Une adresse copiée de Jellyfin ou de Mira web mène au serveur")
    func copiedPages() throws {
        #expect(try ServerAddress.candidates("http://100.114.247.13:8096/web/#/home.html").map(\.absoluteString) == ["http://100.114.247.13:8096"])
        #expect(try ServerAddress.candidates("http://100.114.247.13:8096/Mira/#/titre/abc").map(\.absoluteString) == ["http://100.114.247.13:8096"])
        #expect(try ServerAddress.candidates("https://example.com/jellyfin/web/index.html").map(\.absoluteString) == ["https://example.com/jellyfin"])
        #expect(try ServerAddress.candidates("HTTP://Example.com:8096/").map(\.absoluteString) == ["http://Example.com:8096"])
    }

    @Test("Adresses refusées")
    func refused() {
        #expect(throws: ServerAddress.Problem.empty) { try ServerAddress.candidates("   ") }
        #expect(throws: ServerAddress.Problem.invalid) { try ServerAddress.candidates("ftp://nas") }
        #expect(throws: ServerAddress.Problem.invalid) { try ServerAddress.candidates("http://user:pass@nas:8096") }
    }

    @Test("Mot de passe en clair sur Internet : seulement en HTTP hors de la maison et de Tailscale")
    func plainPassword() throws {
        let exposed = { (text: String) in ServerAddress.exposesPassword(URL(string: text)!) }
        #expect(exposed("http://jellyfin.example.com:8096"))
        #expect(exposed("http://203.0.113.9:8096"))
        #expect(!exposed("https://jellyfin.example.com"))
        #expect(!exposed("http://192.168.1.198:8096"))
        #expect(!exposed("http://10.0.0.2:8096"))
        #expect(!exposed("http://172.20.1.1:8096"))
        #expect(!exposed("http://100.114.247.13:8096"))
        #expect(!exposed("http://desktop-invefqh.tail1234.ts.net:8096"))
        #expect(!exposed("http://nas:8096"))
        #expect(!exposed("http://nas.local:8096"))
        #expect(exposed("http://100.200.1.1:8096"))
    }

    @Test("Mira web à une route")
    func miraWeb() {
        let server = URL(string: "http://100.114.247.13:8096")!
        #expect(ServerAddress.miraWeb(server)?.absoluteString == "http://100.114.247.13:8096/Mira/")
        #expect(ServerAddress.miraWeb(server, route: "lecture/abc")?.absoluteString == "http://100.114.247.13:8096/Mira/#/lecture/abc")
    }
}

@Suite("Jellyfin")
struct JellyfinTests {
    let client = JellyfinClient(server: URL(string: "http://nas:8096")!, device: DeviceIdentity(id: "d\"1", name: "iPhone", version: "0.8.6"), token: "t0k", userId: "u1")

    @Test("L'en-tête d'autorisation porte l'appareil et le jeton, sans guillemet de trop")
    func authorization() {
        #expect(client.authorization == "MediaBrowser Client=\"Mira\", Device=\"iPhone\", DeviceId=\"d1\", Version=\"0.8.6\", Token=\"t0k\"")
        var anonymous = client
        anonymous.token = nil
        #expect(!anonymous.authorization.contains("Token"))
    }

    @Test("Adresse d'une image : largeur en pixels de l'écran par pas de 80, étiquette, sans jeton")
    func imageAddress() {
        let url = client.imageURL(Artwork(itemId: "abc", type: "Primary", tag: "t1"), width: 301, scale: 1)
        #expect(url.absoluteString == "http://nas:8096/Items/abc/Images/Primary?maxWidth=320&quality=90&tag=t1")
        // A 120-point poster on a 3× screen is 360 pixels wide.
        let poster = client.imageURL(Artwork(itemId: "abc", type: "Primary", tag: "t1"), width: 120, scale: 3)
        #expect(poster.absoluteString == "http://nas:8096/Items/abc/Images/Primary?maxWidth=400&quality=90&tag=t1")
        let backdrop = client.imageURL(Artwork(itemId: "abc", type: "Backdrop", tag: "t2"), width: 5000, scale: 3, quality: 90)
        #expect(backdrop.absoluteString == "http://nas:8096/Items/abc/Images/Backdrop/0?maxWidth=3840&quality=90&tag=t2")
    }

    @Test("Les clés de Jellyfin en PascalCase ou en camelCase")
    func decoding() throws {
        let pascal = #"{"Id":"e1","Name":"Pilote","Type":"Episode","SeriesId":"s1","SeriesName":"Pioneer One","ParentIndexNumber":1,"IndexNumber":2,"RunTimeTicks":27000000000,"ImageTags":{"Primary":"p1"},"ImageBlurHashes":{"Primary":{"p1":"LEHV6nWB2yk8pyo0adR*.7kCMdnj"}},"UserData":{"PlaybackPositionTicks":9000000000,"Played":false,"LastPlayedDate":"2026-10-27T21:14:03.1234567Z"}}"#
        let item = try JSON.decoder.decode(Item.self, from: Data(pascal.utf8))
        #expect(item.id == "e1" && item.isEpisode && item.seriesName == "Pioneer One")
        #expect(item.userData?.playbackPositionTicks == 9_000_000_000)
        let art = try #require(Artwork.of(item, .poster))
        #expect(art.itemId == "e1" && art.tag == "p1" && art.blurHash == "LEHV6nWB2yk8pyo0adR*.7kCMdnj")
        let camel = #"{"id":"x","serverName":"Maison","version":"12.1.0","productName":"Jellyfin Server"}"#
        let info = try JSON.decoder.decode(PublicInfo.self, from: Data(camel.utf8))
        #expect(info.serverName == "Maison" && info.id == "x")
    }

    @Test("Jellyfin 10.9 ou plus")
    func versions() throws {
        let at = { (version: String) in ServerInfo(address: URL(string: "http://nas:8096")!, id: "x", name: "nas", version: version) }
        #expect(throws: JellyfinError.tooOld("10.8.13")) { try JellyfinClient.supported(at("10.8.13")) }
        #expect(throws: Never.self) { try JellyfinClient.supported(at("10.9.11")) }
        #expect(throws: Never.self) { try JellyfinClient.supported(at("12.1.0-rc2")) }
        #expect(throws: Never.self) { try JellyfinClient.supported(at("")) }
    }

    @Test("Images d'un épisode : la sienne d'abord, puis celles de sa série")
    func artwork() {
        var episode = Item(id: "e", type: "Episode")
        episode.parentThumbItemId = "s"
        episode.parentThumbImageTag = "th"
        episode.parentBackdropItemId = "s"
        episode.parentBackdropImageTags = ["bd"]
        #expect(Artwork.of(episode, .wide) == Artwork(itemId: "s", type: "Thumb", tag: "th"))
        #expect(Artwork.of(episode, .backdrop) == Artwork(itemId: "s", type: "Backdrop", tag: "bd"))
        episode.imageTags = ["Primary": "own"]
        #expect(Artwork.of(episode, .wide) == Artwork(itemId: "e", type: "Primary", tag: "own"))
    }
}

@Suite("Accueil")
struct HomeTests {
    func item(_ id: String, series: String? = nil, position: Int64 = 0, played: Bool = false, last: String? = nil) -> Item {
        var item = Item(id: id, type: series == nil ? "Movie" : "Episode")
        item.seriesId = series
        item.runTimeTicks = 60 * 60 * ItemText.ticksPerSecond
        item.userData = UserData(playbackPositionTicks: position, played: played, lastPlayedDate: last)
        return item
    }

    @Test("Continuer à regarder : une reprise l'emporte sur l'épisode suivant, ordre de la dernière activité")
    func orderContinue() {
        let resume = [item("film", position: 100, last: "2026-10-20T10:00:00Z"), item("e3", series: "s", position: 50, last: "2026-10-21T10:00:00.5Z")]
        let next = [item("e4", series: "s"), item("x2", series: "x")]
        let history = [item("x1", series: "x", played: true, last: "2026-10-22T10:00:00Z")]
        let ordered = HomeContent.orderContinue(resume: resume, next: next, history: history).map(\.id)
        #expect(ordered == ["x2", "e3", "film"])
    }

    @Test("Un titre déjà vu ou sans position n'est pas repris")
    func notResumed() {
        let resume = [item("vu", position: 100, played: true), item("zero")]
        #expect(HomeContent.orderContinue(resume: resume, next: [], history: []).isEmpty)
    }

    @Test("Dates de Jellyfin à sept décimales")
    func dates() throws {
        let date = try #require(HomeContent.parseDate("2026-10-27T21:14:03.1234567Z"))
        let expected = try Date("2026-10-27T21:14:03Z", strategy: .iso8601)
        #expect(date == expected)
        #expect(HomeContent.parseDate("2026-10-27T21:14:03+02:00") != nil)
        #expect(HomeContent.parseDate("hier") == nil)
    }

    @Test("Le bandeau : la série d'un épisode en cours, puis les ajouts, sans doublon, six au plus")
    func banner() {
        var series = Item(id: "s", type: "Series")
        series.backdropImageTags = ["b"]
        var episode = item("e1", series: "s", position: 10, last: "2026-10-27T10:00:00Z")
        episode.parentBackdropItemId = "s"
        episode.parentBackdropImageTags = ["b"]
        let latest = (0..<8).map { index -> Item in
            var film = item("f\(index)")
            film.backdropImageTags = index == 1 ? nil : ["b\(index)"]
            return film
        }
        let content = HomeContent(resume: [episode], latest: [series] + latest, series: ["s": series])
        let banner = content.banner(hidden: [:])
        #expect(banner.map(\.id) == ["s", "f0", "f2", "f3", "f4", "f5"])
        #expect(banner.first?.play?.id == "e1")
    }
}

@Suite("Textes")
struct TextTests {
    @Test("Durées et codes d'épisode")
    func texts() {
        #expect(ItemText.duration(seconds: 20) == "1 min")
        #expect(ItemText.duration(seconds: 45 * 60) == "45 min")
        var episode = Item(id: "e", type: "Episode")
        episode.parentIndexNumber = 2
        episode.indexNumber = 5
        #expect(ItemText.episodeCode(episode).hasPrefix("S2 · "))
        #expect(ItemText.progress(episode) == 0)
    }

    @Test("BlurHash : une petite image de la bonne taille")
    func blurHash() throws {
        let image = try #require(BlurHash.image("LEHV6nWB2yk8pyo0adR*.7kCMdnj", size: CGSize(width: 24, height: 24)))
        #expect(image.size == CGSize(width: 24, height: 24))
        #expect(BlurHash.image("trop court", size: CGSize(width: 8, height: 8)) == nil)
    }
}
