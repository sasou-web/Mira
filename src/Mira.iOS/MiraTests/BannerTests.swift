import Foundation
import Testing
@testable import Mira

@Suite("Bandeau de l'Accueil")
struct BannerTests {
    /// A slide of an iPhone 15 Pro: 393 points wide, the banner's height, at 3 pixels per point.
    let slide = CGSize(width: 393, height: BannerView.height(CGSize(width: 393, height: 852)))

    func info(_ type: String, _ width: Int, _ height: Int, index: Int? = nil, tag: String? = nil) -> ImageInfo {
        ImageInfo(imageType: type, imageIndex: index, imageTag: tag ?? "\(type)-\(index ?? 0)", width: width, height: height)
    }

    func series(_ pictures: [ImageInfo]) -> BannerEntry {
        var item = Item(id: "s", type: "Series")
        item.backdropImageTags = ["Backdrop-0"]
        var episode = Item(id: "e", type: "Episode")
        episode.seriesId = "s"
        episode.userData = UserData(playbackPositionTicks: 10, played: false)
        return BannerEntry(item: item, play: episode, pictures: pictures, playPictures: [info("Primary", 1920, 1080)])
    }

    @Test("Une bannière fine d'anime (1900 × 400) n'est jamais prise : l'image de l'épisode, nette, à sa place")
    func thinBanner() throws {
        let entry = series([info("Backdrop", 1900, 400), info("Primary", 460, 650)])
        #expect(BannerPicture.enlargement(width: 1900, height: 400, slide: slide, scale: 3) > 3)
        let choice = try #require(BannerPicture.choose(entry, slide: slide, scale: 3, hidingSpoilers: false))
        #expect(choice.art.itemId == "e")
        #expect(choice.art.type == "Primary")
        #expect(choice.pixels == 1920)
    }

    @Test("La première image de fond nette l'emporte, même si une autre est plus grande")
    func firstSharp() throws {
        let entry = series([info("Backdrop", 1920, 1080, index: 0), info("Backdrop", 3840, 2160, index: 1)])
        let choice = try #require(BannerPicture.choose(entry, slide: slide, scale: 3, hidingSpoilers: false))
        #expect(choice.art.type == "Backdrop")
        #expect(choice.art.index == 0)
        let wide = BannerPicture.askedWidth(choice, slide: slide, scale: 3)
        // Asked as large as Jellyfin has it, never more: 1920 pixels.
        #expect(abs(wide * 3 - 1920) < 0.5)
    }

    @Test("Floue au premier rang : une autre image de fond nette passe devant")
    func sharperBackdrop() throws {
        let entry = series([info("Backdrop", 1280, 300, index: 0), info("Backdrop", 3840, 2160, index: 1)])
        let choice = try #require(BannerPicture.choose(entry, slide: slide, scale: 3, hidingSpoilers: false))
        #expect(choice.art.index == 1)
        let url = JellyfinClient(server: URL(string: "http://nas:8096")!, device: DeviceIdentity(id: "d1", name: "iPhone", version: "0.9.8"), token: "t0k", userId: "u1").imageURL(choice.art, width: BannerPicture.askedWidth(choice, slide: slide, scale: 3), scale: 3)
        #expect(url.path().hasSuffix("Images/Backdrop/1"))
    }

    @Test("Cacher les spoilers : jamais l'image d'un épisode pas encore commencé")
    func spoilers() throws {
        var entry = series([info("Backdrop", 1900, 400)])
        let episode = Item(id: "e", type: "Episode")
        entry = BannerEntry(item: entry.item, play: episode, pictures: entry.pictures, playPictures: entry.playPictures)
        let choice = try #require(BannerPicture.choose(entry, slide: slide, scale: 3, hidingSpoilers: true))
        #expect(choice.art.itemId == "s")
    }

    @Test("Sans les tailles de Jellyfin : la première image de fond, comme avant")
    func unknownSizes() throws {
        let known = series([])
        let entry = BannerEntry(item: known.item, play: known.play)
        let choice = try #require(BannerPicture.choose(entry, slide: slide, scale: 3, hidingSpoilers: false))
        #expect(choice.art.type == "Backdrop")
        #expect(choice.pixels == nil)
    }

    @Test("Les faits d'un épisode sont les siens, jamais l'année ni les saisons de la série")
    func facts() {
        var show = Item(id: "s", type: "Series")
        show.productionYear = 1993
        show.childCount = 1
        show.communityRating = 8.4
        show.overview = "La série."
        var episode = Item(id: "e", type: "Episode")
        episode.productionYear = 2022
        episode.parentIndexNumber = 6
        episode.indexNumber = 9
        episode.runTimeTicks = 24 * 60 * ItemText.ticksPerSecond
        episode.userData = UserData(playbackPositionTicks: 600 * ItemText.ticksPerSecond, played: false)
        let entry = BannerEntry(item: show, play: episode)
        let facts = BannerText.facts(entry)
        #expect(facts.first == "2022")
        #expect(!facts.contains("1993"))
        #expect(facts.count == 3)
        // No summary of its own: the series'.
        #expect(BannerText.overview(entry, hidingSpoilers: false) == "La série.")
        #expect(BannerText.facts(BannerEntry(item: show, play: nil)).contains { $0.hasPrefix("★") })
    }

    @Test("La barre se remplit, s'arrête et reprend où elle était ; une autre diapositive repart de zéro")
    func clock() {
        var clock = BannerClock()
        let start = Date(timeIntervalSince1970: 1000)
        clock.run("a", at: start)
        #expect(abs(clock.progress(of: "a", at: start.addingTimeInterval(3.25), period: 6.5) - 0.5) < 0.001)
        clock.pause(at: start.addingTimeInterval(3.25))
        #expect(abs(clock.progress(of: "a", at: start.addingTimeInterval(60), period: 6.5) - 0.5) < 0.001)
        clock.run("a", at: start.addingTimeInterval(100))
        #expect(abs(clock.elapsed(at: start.addingTimeInterval(101)) - 4.25) < 0.001)
        #expect(clock.progress(of: "b", at: start.addingTimeInterval(101), period: 6.5) == 0)
        clock.run("b", at: start.addingTimeInterval(101))
        #expect(clock.elapsed(at: start.addingTimeInterval(101)) == 0)
    }

    @Test("Teinte du bandeau : rouge et bleu différents et clairs ; neutre = gris clair de Mira (ArtworkPalette)")
    func tint() {
        func pixels(_ b: UInt8, _ g: UInt8, _ r: UInt8) -> [UInt8] { (0..<64).flatMap { _ in [b, g, r, 255] } }
        let red = ArtworkAccent.extract(pixels(30, 40, 180)), blue = ArtworkAccent.extract(pixels(180, 60, 30))
        #expect(red.red > red.blue && blue.blue > blue.red && red != blue)
        #expect(min(red.red, red.green, red.blue) >= 175 && min(blue.red, blue.green, blue.blue) >= 175)
        for neutral in [[], [0, 0, 0, 255, 255, 0, 0, 0], [110, 110, 110, 255, 250, 250, 250, 255]] as [[UInt8]] {
            #expect(ArtworkAccent.extract(neutral) == ArtworkAccent.neutral)
        }
    }
}
