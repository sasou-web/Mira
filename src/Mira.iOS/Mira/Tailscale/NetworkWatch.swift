import Foundation
import Network

/// The iPhone's connection, as the way to Jellyfin needs it: Wi-Fi or a cable (where the home network may be), another
/// one (cellular), or none, and the interface it goes through (for Built-in Tailscale); told on each change.
@MainActor
final class NetworkWatch {
    private let monitor = NWPathMonitor()

    func start(_ changed: @escaping @Sendable (WayPlan.Network, String?) -> Void) {
        monitor.pathUpdateHandler = { path in
            let network: WayPlan.Network = if path.status != .satisfied {
                .none
            } else if path.usesInterfaceType(.wifi) || path.usesInterfaceType(.wiredEthernet) {
                .local
            } else {
                .cellular
            }
            changed(network, path.status == .satisfied ? path.availableInterfaces.first?.name : nil)
        }
        monitor.start(queue: DispatchQueue(label: "app.miracinema.Mira.network"))
    }
}
