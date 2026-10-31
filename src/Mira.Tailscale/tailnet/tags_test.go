package tailnet

import (
	"os"
	"strings"
	"testing"

	"tailscale.com/feature/buildfeatures"
	"tailscale.com/feature/featuretags"
)

// kept are the features of Tailscale that Mira builds in; build-tags.txt leaves out all the others.
var kept = []featuretags.FeatureTag{
	"netstack",     // the node's own network stack: no driver, no administrator rights
	"udptransport", // direct connections to the other devices…
	"nattraversal", // …through NATs (STUN, hole punching)
	"portmapper",   // …and through routers that open a port when asked (UPnP, NAT-PMP, PCP)
	"useproxy",     // reaching Tailscale through the system's proxy, where there is one
	"bakedroots",   // reaching Tailscale on a system whose certificate roots are out of date
	"health",       // the warnings the apps explain (no network, no relay)
	"ipnbus",       // following the node's state
	"tailnetlock",  // joining a tailnet that uses Tailnet Lock
	"ace",          // reaching Tailscale through its alternate endpoints, where it asks to
	"gro",          // faster reception (video)
	"cachenetmap",  // starting from the last network map while Tailscale's coordination server is down (an outage)
	"serve",        // required by tsnet's code, never used
	"acme",         // required by tsnet's code, never used
}

// TestBuildTags checks build-tags.txt against Tailscale's own list of features: after an update of tailscale.com, a new
// feature is left out until it is added to kept. Run it with the tags: go test -tags "$(cat build-tags.txt)".
func TestBuildTags(t *testing.T) {
	data, err := os.ReadFile("../build-tags.txt")
	if err != nil {
		t.Fatal(err)
	}
	want := strings.Join(featuretags.MinTags(kept...), ",")
	if got := strings.TrimSpace(string(data)); got != want {
		t.Errorf("build-tags.txt is out of date; write:\n%s", want)
	}
	if buildfeatures.HasLogTail || buildfeatures.HasNetLog || buildfeatures.HasC2N || buildfeatures.HasPosture || buildfeatures.HasPortList {
		t.Error("built without build-tags.txt: logs, flow logs, control-to-node requests, posture or port lists are in")
	}
}
