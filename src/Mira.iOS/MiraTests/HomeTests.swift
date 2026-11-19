import Foundation
import Testing
@testable import Mira

/// « Le foyer » on the iPhone: the home link the extension makes read back (and nothing else taken for one), as
/// FamilyTests of Core; the refusals of /Mira/home as the app shows them.
@Suite("Foyer")
struct HomeTests {
    private static let id = "0123456789abcdef0123456789abcdef"
    private static let until = Date().addingTimeInterval(48 * 3600)

    /// A link as HomeTailnet.Link of the extension writes it: its page, then base64url of its JSON.
    private func made(address: String = "http://100.88.12.34:8096", key: String = "tskey-auth-kAbC123CNTRL-0123456789abcdef", name: String = "Maison",
                      version: Int = 1, until: Date = HomeTests.until) -> String {
        let json: [String: Any] = ["v": version, "name": name, "id": Self.id, "url": address, "key": key, "until": Int(until.timeIntervalSince1970)]
        let data = (try? JSONSerialization.data(withJSONObject: json)) ?? Data()
        return HomeLink.page + data.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }

    @Test("Le lien que fait l’extension se relit, sur sa page comme dans Mira")
    func readBack() throws {
        let page = made()
        let data = String(page[page.index(after: try #require(page.firstIndex(of: "#")))...])
        for text in [page, HomeLink.app + data, "mira://home/#" + data] {
            let link = try #require(HomeLink.read(text), "\(text.prefix(20))")
            #expect(link.name == "Maison" && link.serverId == Self.id && link.key == "tskey-auth-kAbC123CNTRL-0123456789abcdef")
            #expect(link.address.absoluteString == "http://100.88.12.34:8096/")
            #expect(link.host == "100.88.12.34")
            #expect(!link.expired() && link.expired(Self.until.addingTimeInterval(1)))
            #expect(link.is("01234567-89ab-cdef-0123-456789ABCDEF") && !link.is("ffffffffffffffffffffffffffffffff") && !link.is(nil))
        }
        #expect(HomeLink.read(made(address: "http://100.88.12.34:8096/jellyfin"))?.address.absoluteString == "http://100.88.12.34:8096/jellyfin/")
        #expect(HomeLink.read(made(address: "http://[fd7a:115c:a1e0::1]:8096"))?.host == "fd7a:115c:a1e0::1")
    }

    @Test("Rien d’autre n’est pris pour un lien du foyer")
    func nothingElse() {
        #expect(HomeLink.read(made(address: "http://192.168.1.42:8096")) == nil, "An address outside Tailscale's")
        #expect(HomeLink.read(made(address: "http://jellyfin.example.com")) == nil, "A name, which could be anywhere")
        #expect(HomeLink.read(made(address: "http://alex:secret@100.88.12.34:8096")) == nil, "Credentials in the address")
        #expect(HomeLink.read(made(key: "not-a-key")) == nil, "A key that is not Tailscale's")
        #expect(HomeLink.read(made(version: 2)) == nil, "Another version")
        let page = made()
        #expect(HomeLink.read(String(page.dropLast(6))) == nil, "Cut short")
        #expect(HomeLink.read(page + "<script>") == nil, "Something after it")
        #expect(HomeLink.read("mira://party/abc") == nil, "Another link")
        #expect(HomeLink.read("https://example.com/home#" + page.split(separator: "#")[1]) == nil, "Another site")
        let odd = HomeLink.read(made(name: "Chez\u{7} Alex " + String(repeating: "x", count: 120)))
        #expect(odd != nil && odd?.name.contains("\u{7}") == false && (odd?.name.count ?? 99) <= 80, "A name cleaned and kept short")
        #expect(HomeLink.read(made(until: Date(timeIntervalSince1970: 1000)))?.expired() == true, "Read, but expired")
    }

    @Test("Les refus du foyer, comme l’extension répond")
    func refusals() {
        func kind(_ path: String, _ status: Int, _ answer: HomeAnswer? = nil, pinSent: Bool = false) -> HomeRefusal {
            HomeAPI.refusal(path, status: status, answer: answer, pinSent: pinSent).kind
        }
        let pin = HomeAnswer(error: "That’s not the right PIN.", pin: true, left: 3)
        #expect(kind("Mira/home/join", 401) == .wrong)
        #expect(kind("Mira/home/join", 404) == .noHome)
        #expect(kind("Mira/home/join", 409) == .full)
        #expect(kind("Mira/home/join", 429) == .tooMany)
        #expect(kind("Mira/home/profiles", 401) == .outOfHome)
        #expect(kind("Mira/home/open", 404) == .notInHome)
        #expect(kind("Mira/home/open", 403, pin) == .pinAsked, "No PIN sent: asked")
        #expect(kind("Mira/home/open", 403, pin, pinSent: true) == .pinWrong, "A PIN sent: wrong")
        #expect(kind("Mira/home/open", 429, HomeAnswer(pin: true, wait: 300)) == .pinWait)
        #expect(kind("Mira/home/open", 502) == .notReady)
        let refused = HomeAPI.refusal("Mira/home/open", status: 403, answer: pin, pinSent: true)
        #expect(refused.message == "That’s not the right PIN." && refused.left == 3, "In the extension's words, with the tries left")
    }

    @Test("Les réponses du foyer se lisent")
    func answers() throws {
        let joined = #"{"token":"t0k3n","profiles":[{"id":"a1","name":"Sam","imageTag":null,"pin":false},{"id":"b2","name":"Lina","imageTag":"x9","pin":true}],"current":null,"deviceId":"mira-home-0011"}"#
        let answer = try HomeAPI.decoder.decode(HomeAnswer.self, from: Data(joined.utf8))
        #expect(answer.token == "t0k3n" && answer.deviceId == "mira-home-0011")
        #expect(answer.profiles?.map(\.name) == ["Sam", "Lina"] && answer.profiles?[1].pin == true && answer.profiles?[1].imageTag == "x9")
        let settings = #"{"home":true,"changed":"2026-11-19T13:26:08.1234567+00:00","minPassword":8,"profiles":[{"id":"a1","name":"Sam","imageTag":null,"admin":false,"disabled":false,"offered":true,"pin":false,"allLibraries":true,"maxRating":"PG-13"}],"devices":[{"id":"d1","name":"Mira on iPhone","joined":"2026-11-19T13:26:08Z","seen":"2026-11-19T14:00:00.5Z","profile":"a1","profileName":"Sam"}]}"#
        let home = try HomeAPI.decoder.decode(HomeSettings.self, from: Data(settings.utf8))
        #expect(home.home && home.profiles.first?.maxRating == "PG-13" && home.devices.first?.profileName == "Sam")
        #expect(home.changed != nil && home.devices.first?.seen != nil, "The extension's dates, with fractions of a second or not")
    }
}
