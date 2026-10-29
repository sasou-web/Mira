package main

import "tailscale.com/net/netmon"

// setDefaultInterface tells Tailscale the interface iOS sends the Internet's traffic through, as Tailscale's own app does
// from its NWPathMonitor: its routing table may still name the previous one during a change of network.
func setDefaultInterface(name string) {
	netmon.UpdateLastKnownDefaultRouteInterface(name)
}
