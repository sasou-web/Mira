import Foundation
import Network

/// The iPhone's connection, as the way to Jellyfin needs it: Wi-Fi or a cable (where the home network may be), another
/// one (cellular), or none, the interface it goes through (for Built-in Tailscale), and what it costs (for the quality:
/// expensive = cellular or a personal hotspot, constrained = Low Data Mode); told on each change.
@MainActor
final class NetworkWatch {
    private let monitor = NWPathMonitor()

    struct Cost: Equatable, Sendable {
        var expensive = false
        var constrained = false
    }

    func start(_ changed: @escaping @Sendable (WayPlan.Network, String?, Cost) -> Void) {
        monitor.pathUpdateHandler = { path in
            let network: WayPlan.Network = if path.status != .satisfied {
                .none
            } else if path.usesInterfaceType(.wifi) || path.usesInterfaceType(.wiredEthernet) {
                .local
            } else {
                .cellular
            }
            let cost = Cost(expensive: path.status == .satisfied && path.isExpensive, constrained: path.status == .satisfied && path.isConstrained)
            changed(network, path.status == .satisfied ? path.availableInterfaces.first?.name : nil, cost)
        }
        monitor.start(queue: DispatchQueue(label: "app.miracinema.Mira.network"))
    }
}
