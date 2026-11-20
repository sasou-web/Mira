import Foundation

/// « Away from home », set up once by the administrator as a guide, the same words and drawings as Mira for Windows and
/// Mac (HomeGuide of Core; maquette A « Le guide », ogsas 2026-11-20): one step per screen, the one thing to do there, a
/// button to the right page of Tailscale's console and a drawing of what to tap; then Mira checks the key and puts the
/// network's rules right on a tap, undone for 10 minutes.
enum HomeGuide {
    static let steps = 4
    static let tagsPage = URL(string: "https://login.tailscale.com/admin/acls/visual")!
    static let credentialsPage = URL(string: "https://login.tailscale.com/admin/settings/trust-credentials")!
    static let policyPage = URL(string: "https://login.tailscale.com/admin/acls/file")!
    static let tagName = "mira-home"

    // MARK: Settings → Home

    static var entryTitle: String { String(localized: "Let the family watch away from home") }
    static var notSetUp: String { String(localized: "Not set up") }
    static var entryText: String {
        String(localized: "Today the home works on your Wi-Fi only. Mira walks you through Tailscale once, about 3 minutes. After that, a home link is all the family needs: no address, no Tailscale account.")
    }
    static var setUp: String { String(localized: "Set up…") }
    static var readyTitle: String { String(localized: "The family can watch away from home") }
    static var ready: String { String(localized: "Ready") }
    static var readyText: String { String(localized: "Home devices reach only Jellyfin on your Tailscale network. Checked just now.") }
    static var firstLinkText: String {
        String(localized: "Send it to someone in the family: it opens Mira on your home, wherever they are. Works once, ends after 48 hours.")
    }
    static func changed(_ until: Date) -> String { String(localized: "Mira changed your rules. You can undo it until \(until.formatted(date: .omitted, time: .shortened)).") }
    static var undone: String { String(localized: "Your previous rules are back.") }

    // MARK: The guide

    static func header(_ step: Int) -> String { String(localized: "Away from home · Step \(step) of \(steps)") }

    struct Step {
        var title: String
        var why: String
        var todo: [String] = []
        var open: URL?
        var openLabel: String?
        var foot: String?
        var next: String
    }

    static var tag: Step {
        Step(title: String(localized: "Make a label for the family’s devices"),
             why: String(localized: "Tailscale calls it a tag. It’s how your network tells the home’s devices apart, so they can reach Jellyfin and nothing else."),
             todo: [String(localized: "Tap Tags, then Create tag."),
                    String(localized: "Name it \(tagName) (Copy button below)."),
                    String(localized: "Tap Save tag, then come back here.")],
             open: tagsPage, openLabel: String(localized: "Open Tailscale’s tags"),
             foot: String(localized: "Mira can’t do this step for you: Tailscale asks for it first."),
             next: String(localized: "I’ve saved the tag"))
    }

    static var key: Step {
        Step(title: String(localized: "Give Mira a key to your network"),
             why: String(localized: "A key that can do three things only: make the home links, remove a device, and keep your rules right. Tick exactly what the picture shows."),
             todo: [String(localized: "Tap Credential, choose OAuth, then Custom scopes. Not “All”."),
                    String(localized: "Tick Read and Write on Policy File, Devices › Core and Keys › Auth Keys. On the last two, pick the tag \(tagName)."),
                    String(localized: "Tap Generate credential.")],
             open: credentialsPage, openLabel: String(localized: "Open Tailscale’s credentials"),
             foot: String(localized: "The tag isn’t in the list? Step 1 wasn’t saved."),
             next: String(localized: "I see the secret"))
    }

    static var secret: Step {
        Step(title: String(localized: "Copy the secret, Mira does the rest"),
             why: String(localized: "Tailscale shows it once. Mira sends it to your Jellyfin, which keeps it; this iPhone never stores it."),
             foot: String(localized: "One box, not two: the client ID is inside the secret."),
             next: String(localized: "Check it"))
    }

    static func rules(_ changes: Int) -> Step {
        changes > 0
            ? Step(title: String(localized: "One thing left: your network’s rules"),
                   why: String(localized: "Mira checked your key with Tailscale. Your rules would let a home device see your whole network, so Mira will make \(changes) change(s). Your comments stay."),
                   foot: String(localized: "Nothing is written until you tap. You can undo it for 10 minutes."),
                   next: makeChanges(changes))
            : Step(title: String(localized: "All set"),
                   why: String(localized: "Mira checked your key and your rules with Tailscale: home devices reach only Jellyfin."),
                   next: String(localized: "Done"))
    }

    static func makeChanges(_ changes: Int) -> String { changes == 1 ? String(localized: "Make this change") : String(localized: "Make these \(changes) changes") }
    static var copy: String { String(localized: "Copy “\(tagName)”") }
    static var copied: String { String(localized: "Copied.") }
    static var foundSecret: String { String(localized: "Found a Tailscale secret you just copied") }
    static var secretBox: String { String(localized: "Client secret") }
    static var idBox: String { String(localized: "Client ID") }
    static var idNeeded: String {
        String(localized: "Tailscale didn’t take the ID read from the secret: paste the Client ID shown just above the secret too.")
    }
    static var checking: String { String(localized: "Checking with Tailscale…") }
    static var seeLines: String { String(localized: "See the exact lines") }
    static var hideLines: String { String(localized: "Hide the lines") }

    static func looksLikeSecret(_ text: String?) -> Bool {
        guard let text = text?.trimmingCharacters(in: .whitespacesAndNewlines), text.count > 20, text.count < 256 else { return false }
        return text.hasPrefix("tskey-client-") && !text.contains(" ")
    }

    static func masked(_ secret: String) -> String {
        let text = secret.trimmingCharacters(in: .whitespacesAndNewlines)
        return text.count <= 19 ? "tskey-client-…" : String(text.prefix(19)) + "…••••••••"
    }

    // MARK: Step 4: what Mira checked

    enum Mark { case ok, warn, bad }

    static func checks(_ state: HomeTailnetState) -> [(Mark, String)] {
        let refused = state.policy == "refused"
        var lines: [(Mark, String)] = [(refused ? .bad : .ok, refused ? String(localized: "Tailscale no longer knows this key") : String(localized: "Tailscale accepts the key"))]
        if refused { return lines }
        let missing = state.missing ?? []
        lines.append(state.broad == true ? (.bad, String(localized: "This key can do too much"))
            : !missing.isEmpty || state.policy == "scope" ? (.bad, missingTitle(missing))
            : (.ok, String(localized: "It can do what the home needs, and no more")))
        lines.append(state.tag ?? true ? (.ok, String(localized: "The \(tagName) tag is there")) : (.warn, String(localized: "The \(tagName) tag is missing: Mira adds it")))
        let changes = state.fix?.count ?? 0
        lines.append(state.policy == "ok" ? (.ok, String(localized: "Your rules are right"))
            : changes > 0 ? (.warn, String(localized: "Rules: \(changes) change(s) to make"))
            : (.bad, String(localized: "Rules Mira can’t fix by itself")))
        return lines
    }

    static func change(_ change: HomeTailnetState.Change) -> String {
        switch change.kind {
        case "tag": String(localized: "Add the \(tagName) tag.")
        case "grant": String(localized: "Add a rule: the home’s devices reach Jellyfin, nothing else.")
        case "narrow": String(localized: "Rule \(change.number ?? 0) let everyone go everywhere: it now covers your own devices and the friends you share with, not the home’s.")
        default: change.kind
        }
    }

    // MARK: When something is off

    enum Remedy { case redoKey, openCredentials, fix, seeLines, setUpAgain, checkAgain, openPolicy }

    struct Problem {
        var title: String
        var text: String
        var remedies: [Remedy]
    }

    static func trouble(_ code: String?, missing: [String] = [], broad: Bool = false, changes: Int = 0) -> Problem? {
        if broad || code == "too-broad" {
            return Problem(title: String(localized: "This key can do too much"),
                           text: String(localized: "It was made with “All - Read & Write”: it could change anything on your network, and it would sit on your server. Mira won’t use it. Delete it in Tailscale and make one with Custom scopes."),
                           remedies: [.redoKey, .openCredentials])
        }
        if code == "missing" || code == "scope" || !missing.isEmpty {
            return Problem(title: missingTitle(missing),
                           text: String(localized: "Without it Mira can’t do its job. Tailscale can’t change a key once made: make a new one, with the three lines ticked."),
                           remedies: [.redoKey])
        }
        if ["too-open", "no-access", "no-tag"].contains(code ?? ""), changes > 0 {
            return Problem(title: String(localized: "Your rules let home devices see your whole network"),
                           text: String(localized: "Mira can fix that in \(changes) change(s), keeping your comments."), remedies: [.fix, .seeLines])
        }
        switch code {
        case "too-open", "no-access", "no-tag", "invalid":
            return Problem(title: String(localized: "Rules Mira can’t fix by itself"),
                           text: String(localized: "A rule lets home devices go further than Jellyfin in a way Mira won’t change without you. Open your rules in Tailscale, or ask for help on Mira’s Discord."),
                           remedies: [.openPolicy, .checkAgain])
        case "no-write":
            return Problem(title: String(localized: "This key is missing one tick: Policy File"),
                           text: String(localized: "Without it Mira can’t fix your rules. Tailscale can’t change a key once made: make a new one, with the three lines ticked."), remedies: [.redoKey])
        case "changed":
            return Problem(title: String(localized: "Your rules just changed"),
                           text: String(localized: "Someone changed them while Mira was reading them. Check again, then fix them."), remedies: [.checkAgain])
        case "refused":
            return Problem(title: String(localized: "Tailscale no longer knows this key"),
                           text: String(localized: "It was deleted or revoked in Tailscale. Home links can’t be made until you give Mira a new one. Devices already in the home keep working."),
                           remedies: [.setUpAgain])
        case "no-tailscale":
            return Problem(title: String(localized: "Tailscale is off on Jellyfin’s PC"),
                           text: String(localized: "Turn it on on Jellyfin’s PC, then check again."), remedies: [.checkAgain])
        case "unreachable":
            return Problem(title: String(localized: "Tailscale isn’t answering"),
                           text: String(localized: "Jellyfin’s PC couldn’t reach Tailscale. Try again in a moment."), remedies: [.checkAgain])
        default: return nil
        }
    }

    static func label(_ remedy: Remedy) -> String {
        switch remedy {
        case .redoKey: String(localized: "Redo step 2")
        case .openCredentials: String(localized: "Open Tailscale’s credentials")
        case .fix: String(localized: "Make the changes")
        case .seeLines: seeLines
        case .setUpAgain: String(localized: "Set up again")
        case .checkAgain: String(localized: "Check again")
        case .openPolicy: String(localized: "Open my rules in Tailscale")
        }
    }

    static func missingTitle(_ missing: [String]) -> String {
        let names = missing.map(console)
        switch names.count {
        case 0: return String(localized: "This key is missing some ticks")
        case 1: return String(localized: "This key is missing one tick: \(names[0])")
        default: return String(localized: "This key is missing some ticks: \(names.joined(separator: ", "))")
        }
    }

    static func console(_ scope: String) -> String {
        switch scope {
        case "auth_keys": "Keys › Auth Keys"
        case "devices:core": "Devices › Core"
        case "policy_file": "Policy File"
        default: scope
        }
    }

    // MARK: The drawings of the console

    enum SketchKind { case heading, field, choice, scope, button }

    struct Sketch: Identifiable {
        let id = UUID()
        var kind: SketchKind
        var label: String
        var value: String?
        var on = false
        var dim = false
        var marker = 0
        var tag: String?
        var wrong = false
    }

    static func drawing(_ step: Int) -> (address: String, lines: [Sketch]) {
        switch step {
        case 1:
            ("login.tailscale.com/admin/acls/visual",
             [Sketch(kind: .heading, label: "Access controls · Tags"), Sketch(kind: .button, label: "Create tag", marker: 1),
              Sketch(kind: .field, label: "Tag name", value: "tag:" + tagName, marker: 2),
              Sketch(kind: .field, label: "Tag owners", value: String(localized: "Leave as it is"), dim: true),
              Sketch(kind: .button, label: "Save tag", marker: 3)])
        case 2:
            ("login.tailscale.com/admin/settings/trust-credentials",
             [Sketch(kind: .heading, label: "New credential · OAuth"),
              Sketch(kind: .choice, label: "All - Read & Write", value: String(localized: "not this one"), wrong: true),
              Sketch(kind: .choice, label: "Custom scopes", on: true, marker: 1),
              Sketch(kind: .scope, label: "Policy File", on: true),
              Sketch(kind: .scope, label: "Devices › Core", on: true, tag: "tag:" + tagName),
              Sketch(kind: .scope, label: "Devices › Routes", dim: true),
              Sketch(kind: .scope, label: "Keys › Auth Keys", on: true, marker: 2, tag: "tag:" + tagName),
              Sketch(kind: .scope, label: "Keys › OAuth Keys", dim: true),
              Sketch(kind: .button, label: "Generate credential", marker: 3)])
        default:
            ("login.tailscale.com/admin/settings/trust-credentials",
             [Sketch(kind: .heading, label: "Credential created"),
              Sketch(kind: .field, label: "Client ID", value: "kAbC12DeFG11CNTRL", dim: true),
              Sketch(kind: .field, label: String(localized: "Client secret · shown once"), value: "tskey-client-kAbC12…-9fQ2…", marker: 1),
              Sketch(kind: .heading, label: String(localized: "Only the secret is needed: the client ID is already inside it."))])
        }
    }
}
