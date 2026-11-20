import Foundation

/// What Mira says of « Le foyer », the same words as Mira for Windows and Mac (HomeText of Core).
enum HomeText {
    static var noExtension: String { String(localized: "This Jellyfin has no home: its Mira plugin needs updating.") }
    static var whoIsWatching: String { String(localized: "Who’s watching?") }
    static func homeOf(_ name: String) -> String { String(localized: "\(name)’s home") }
    static func join(_ name: String) -> String { String(localized: "Join \(name)’s home") }
    static var password: String { String(localized: "Home password") }
    static func fromLink(_ name: String) -> String { String(localized: "Opened from \(name)’s link") }
    static var fromLinkNote: String { String(localized: "Mira knows where this home is, there and away from it. Nothing else to install or type.") }
    static var foundHere: String { String(localized: "Found at this address.") }
    static var next: String { String(localized: "Next: pick your profile. This iPhone stays in the home until it’s removed.") }
    static var ownAccount: String { String(localized: "I have my own account") }
    static var leave: String { String(localized: "Leave this home") }
    static func pinOf(_ name: String) -> String { String(localized: "\(name)’s PIN") }
    static var pinNote: String { String(localized: "Checked by Jellyfin’s Mira plugin, not by this iPhone. 5 wrong tries lock the profile for 5 minutes.") }
    static func pinLeft(_ left: Int) -> String {
        left <= 0 ? String(localized: "That’s not the right PIN.") : String(localized: "That’s not the right PIN. \(left) tries left.")
    }
    static func pinWait(_ seconds: Int) -> String { String(localized: "Too many tries: try again in \(max(1, (seconds + 59) / 60)) min.") }
    static var noProfiles: String { String(localized: "No profiles in this home yet: ask its administrator to turn one on in Settings → Home.") }
    static func unreachable(_ name: String) -> String { String(localized: "\(name)’s home isn’t answering right now. Try again in a moment.") }
    static func notThisServer(_ name: String) -> String { String(localized: "This isn’t \(name)’s Jellyfin: the link no longer matches. Ask for a new link.") }
    static func linkExpired(_ name: String) -> String { String(localized: "This link has expired (48 h). Ask \(name) for a new home link.") }
    static func linkUsed(_ name: String) -> String { String(localized: "This link was already used or taken back. Ask \(name) for a new home link.") }
    static var linkBroken: String { String(localized: "This home link is incomplete: copy all of it, or ask for a new one.") }
    static func reaching(_ name: String) -> String { String(localized: "Connecting to \(name)’s home network…") }
    static var noNode: String { String(localized: "Mira can’t reach this home from this iPhone right now. Close Mira and open it again.") }
    static func removed(_ name: String) -> String { String(localized: "This iPhone is no longer in \(name)’s home. To come back, ask for a new home link.") }
    static func left(_ name: String) -> String { String(localized: "This iPhone left \(name)’s home.") }
    static func leftHere(_ name: String) -> String {
        String(localized: "This iPhone left \(name)’s home here; the server didn’t hear it: its administrator can remove it in Settings → Home.")
    }
    static var thisDevice: String { String(localized: "On this iPhone") }
    static var offline: String { String(localized: "No Internet connection right now.") }

    /// What the policy's verdict means for the administrator (GET Mira/home/tailnet), in a line.
    static func policy(_ policy: String?) -> String {
        switch policy {
        case "ok": String(localized: "Ready: the home’s devices reach only Jellyfin on your Tailscale network.")
        case "no-tag": String(localized: "The tag:mira-home tag isn’t in your Tailscale policy yet (tagOwners).")
        case "too-open": String(localized: "Your policy lets the home’s devices go beyond Jellyfin.")
        case "no-access": String(localized: "No rule lets the home’s devices reach Jellyfin yet.")
        case "no-write": String(localized: "The home’s OAuth client can’t write the policy: create one with the policy_file scope, then connect the home again.")
        case "changed": String(localized: "Your Tailscale policy just changed: check again, then fix it.")
        case "invalid": String(localized: "Tailscale refused the fixed policy: nothing changed. Fix it in Tailscale’s console.")
        case "refused": String(localized: "Tailscale refuses the home’s OAuth client: it was deleted or revoked. Connect the home again.")
        case "scope": String(localized: "The OAuth client lacks the rights it needs (auth_keys, devices:core, policy_file, and the tag:mira-home tag).")
        case "no-tailscale": String(localized: "Tailscale isn’t on on Jellyfin’s PC.")
        case "unreachable": String(localized: "Tailscale didn’t answer. Try again in a moment.")
        case "disconnected": String(localized: "First connect the home to your Tailscale network.")
        default: String(localized: "The home isn’t connected to your Tailscale network: it works only at home.")
        }
    }

    /// Why the home's node cannot take this iPhone in, from its code (the library's, or the relay's X-Mira-Tailscale):
    /// signed out with a link's key means the key is spent; without one, that the administrator removed this iPhone.
    static func node(_ code: String, name: String, withKey: Bool) -> String {
        switch code {
        case "needs-login", "expired": withKey ? linkUsed(name) : removed(name)
        case "config", "relay", "start", "library", "keychain", "locked": noNode
        case "offline": offline
        default: unreachable(name)
        }
    }
}
