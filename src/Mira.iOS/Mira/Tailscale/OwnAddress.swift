import Darwin
import Foundation

/// This iPhone's IPv4 address on Wi-Fi or a cable, with its network's prefix length, read from its interfaces: the
/// home network the way asks address by address once Jellyfin's PC got another address (Way, WayPlan.neighbours).
enum OwnAddress {
    static func current() -> (address: String, prefix: Int)? {
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0, let first = list else { return nil }
        defer { freeifaddrs(list) }
        var found: (address: String, prefix: Int)?
        for pointer in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let entry = pointer.pointee
            let flags = Int32(bitPattern: entry.ifa_flags)
            // Wi-Fi and cables are en0, en1…; cellular (pdp_ip), the VPN of Tailscale's app (utun) are not the home network.
            guard let address = entry.ifa_addr, address.pointee.sa_family == UInt8(AF_INET), let mask = entry.ifa_netmask,
                  flags & IFF_UP != 0, flags & IFF_LOOPBACK == 0, String(cString: entry.ifa_name).hasPrefix("en") else { continue }
            let ip = address.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
            let bits = mask.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
            let text = "\(ip >> 24).\(ip >> 16 & 255).\(ip >> 8 & 255).\(ip & 255)"
            // A self-assigned address (169.254.x.x) is no home network.
            guard ip >> 16 != 0xA9FE else { continue }
            found = (text, bits.nonzeroBitCount)
            if String(cString: entry.ifa_name) == "en0" { break }
        }
        return found
    }
}
