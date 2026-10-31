package tailnet_test

import (
	"net/http"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"miracinema.app/tailscale/tailnet"
	"miracinema.app/tailscale/tailnet/tailnettest"
)

// TestControlOutage is an outage of Tailscale's coordination server (2026-10-30): a node that joined once, granted
// « cache-network-maps » by the tailnet's policy, starts again from its saved network map and reaches Jellyfin; without
// that attribute it keeps connecting.
func TestControlOutage(t *testing.T) {
	if testing.Short() {
		t.Skip("starts a Tailscale network")
	}
	for _, cached := range []bool{true, false} {
		name := "without the attribute"
		if cached {
			name = "with cache-network-maps"
		}
		t.Run(name, func(t *testing.T) {
			jf := jellyfin(t)
			tn, err := tailnettest.Start(tailnettest.Options{Forward: map[uint16]string{8096: jf}, Login: "none", CacheNetmaps: cached, Logf: t.Logf})
			if err != nil {
				t.Fatal(err)
			}
			t.Cleanup(tn.Close)
			pc := tn.Info().Jellyfin
			cfg := tailnet.Config{Dir: filepath.Join(t.TempDir(), "tailscale"), Hostname: "mira-iphone", ControlURL: tn.ControlURL(), StateKey: key(t), Logf: t.Logf}
			online := func(st tailnet.Status) bool {
				for _, p := range st.Peers {
					if p.HostName == "jellyfin-pc" && p.Online {
						return st.State == tailnet.StateRunning
					}
				}
				return false
			}

			node, err := tailnet.Start(cfg)
			if err != nil {
				t.Fatal(err)
			}
			waitFor(t, node, "the Jellyfin PC online", online)
			node.Close()

			tn.SetControlDown(true)
			node, err = tailnet.Start(cfg)
			if err != nil {
				t.Fatal(err)
			}
			defer node.Close()
			if !cached {
				time.Sleep(10 * time.Second)
				if st := node.Status(); st.State == tailnet.StateRunning {
					t.Fatalf("running without the coordination server nor a saved map: %+v", st)
				}
				return
			}
			waitFor(t, node, "the Jellyfin PC from the saved map", online)
			client := &http.Client{Transport: &http.Transport{DialContext: node.Dial}, Timeout: 20 * time.Second}
			res, body := get(t, client, "http://"+pc.IPs[0]+":8096/System/Info/Public")
			if res.StatusCode != 200 || !strings.Contains(body, "Jellyfin de test") {
				t.Fatalf("Jellyfin during the outage: %d %s", res.StatusCode, body)
			}
		})
	}
}
