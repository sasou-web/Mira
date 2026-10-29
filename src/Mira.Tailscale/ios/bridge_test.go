package main

import (
	"context"
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"miracinema.app/tailscale/tailnet"
	"miracinema.app/tailscale/tailnet/tailnettest"
	"tailscale.com/types/logger"
)

// The test's Tailscale network (tailnettest): its coordination server asks each new device to sign in on a page, and
// its PC with Jellyfin answers on 8096 with this stand-in.
func startNet(t *testing.T) (*tailnettest.Net, string) {
	t.Helper()
	jellyfin := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/System/Info/Public":
			w.Header().Set("Content-Type", "application/json")
			io.WriteString(w, `{"Id":"b6c2f1a0d4e94c0a9f3e2d1c0b9a8f7e","ServerName":"PC"}`)
		case "/Videos/1/master.m3u8":
			w.Header().Set("Content-Type", "application/vnd.apple.mpegurl")
			io.WriteString(w, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\n/videos/1/main.m3u8?ApiKey=k\n")
		default:
			http.NotFound(w, r)
		}
	}))
	t.Cleanup(jellyfin.Close)
	network, err := tailnettest.Start(tailnettest.Options{Forward: map[uint16]string{8096: jellyfin.Listener.Addr().String()}, Logf: logger.Discard})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(network.Close)
	return network, network.Info().Jellyfin.IPs[0]
}

func newConfig(t *testing.T, network *tailnettest.Net) string {
	t.Helper()
	key := make([]byte, 32)
	rand.Read(key)
	text, err := json.Marshal(config{
		Dir:         filepath.Join(t.TempDir(), "Tailscale"),
		Hostname:    "mira-iphone",
		StateKey:    base64.RawURLEncoding.EncodeToString(key),
		RelaySecret: "relay-secret-0123456789",
		ControlURL:  network.ControlURL(),
	})
	if err != nil {
		t.Fatal(err)
	}
	return string(text)
}

// waitFor reads the status until ok, or fails.
func waitFor(t *testing.T, ctx context.Context, b *bridge, what string, ok func(report) bool) report {
	t.Helper()
	for {
		r := b.status()
		if ok(r) {
			return r
		}
		if ctx.Err() != nil {
			t.Fatalf("%s: never; last status %s", what, encode(r))
		}
		time.Sleep(50 * time.Millisecond)
	}
}

func get(t *testing.T, url string) (int, string, string) {
	t.Helper()
	client := &http.Client{Timeout: 60 * time.Second}
	resp, err := client.Get(url)
	if err != nil {
		t.Fatalf("GET %s: %v", url, err)
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	return resp.StatusCode, resp.Header.Get(tailnet.ProxyHeader), string(body)
}

// The whole life of Built-in Tailscale in the app: started, signed in once on the network's page, the PC with Jellyfin
// reached through the relay, stopped and started again without a new sign-in, signed out.
func TestBridge(t *testing.T) {
	if testing.Short() {
		t.Skip("a whole Tailscale network")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Minute)
	defer cancel()
	network, pc := startNet(t)
	cfg := newConfig(t, network)

	var b bridge
	t.Cleanup(func() {
		b.stop()
		b.log.close()
	})
	r := b.start(cfg)
	if r.Error != nil || r.Relay == "" || !strings.HasPrefix(r.Relay, "http://127.0.0.1:") || !strings.HasSuffix(r.Relay, "/relay-secret-0123456789") {
		t.Fatalf("start: %s", encode(r))
	}
	relay := r.Relay
	info := fmt.Sprintf("%s/http/%s:8096/System/Info/Public", relay, pc)

	// Not signed in yet: the relay says so, the page comes.
	waitFor(t, ctx, &b, "needs-login", func(r report) bool { return r.State == tailnet.StateNeedsLogin })
	if status, code, _ := get(t, info); status != http.StatusBadGateway || code != tailnet.CodeNeedsLogin {
		t.Errorf("before the sign-in: %d %q", status, code)
	}
	a := b.login()
	if a.Error != nil || !strings.Contains(a.URL, "/auth/") {
		t.Fatalf("login: %s", encode(a))
	}
	if !network.CompleteLogin(a.URL) {
		t.Fatalf("the network doesn't know the page %s", a.URL)
	}
	running := waitFor(t, ctx, &b, "running with the PC", func(r report) bool {
		if r.State != tailnet.StateRunning {
			return false
		}
		for _, p := range r.Peers {
			if p.Online && len(p.IPs) > 0 && p.IPs[0] == pc {
				return true
			}
		}
		return false
	})
	if running.Self == nil || running.Self.HostName != "mira-iphone" || running.Relay != relay {
		t.Errorf("running: %s", encode(running))
	}

	// Jellyfin through the relay; what isn't there, said.
	if status, _, body := get(t, info); status != http.StatusOK || !strings.Contains(body, "b6c2f1a0d4e94c0a9f3e2d1c0b9a8f7e") {
		t.Errorf("Jellyfin: %d %s", status, body)
	}
	// Jellyfin's absolute paths, back under the relay's prefix.
	prefix := fmt.Sprintf("/relay-secret-0123456789/http/%s:8096", pc)
	if status, _, body := get(t, fmt.Sprintf("%s/http/%s:8096/Videos/1/master.m3u8", relay, pc)); status != http.StatusOK ||
		!strings.Contains(body, "\n"+prefix+"/videos/1/main.m3u8?ApiKey=k\n") {
		t.Errorf("playlist: %d %s", status, body)
	}
	if status, code, _ := get(t, fmt.Sprintf("%s/http/%s:9/", relay, pc)); status != http.StatusBadGateway || code != tailnet.CodeClosed {
		t.Errorf("a closed port: %d %q", status, code)
	}
	if status, code, _ := get(t, relay+"/http/100.101.102.103:8096/"); status != http.StatusBadGateway || code != tailnet.CodeRefused {
		t.Errorf("not a device of the network: %d %q", status, code)
	}

	// The PC switched off, then on again: said, then reached again through the same relay.
	if err := network.SetJellyfinOnline(false); err != nil {
		t.Fatal(err)
	}
	waitFor(t, ctx, &b, "the PC offline", func(r report) bool {
		for _, p := range r.Peers {
			if len(p.IPs) > 0 && p.IPs[0] == pc {
				return !p.Online
			}
		}
		return false
	})
	if status, code, _ := get(t, info); status != http.StatusBadGateway || code != tailnet.CodePeerOffline {
		t.Errorf("the PC off: %d %q", status, code)
	}
	if err := network.SetJellyfinOnline(true); err != nil {
		t.Fatal(err)
	}
	waitFor(t, ctx, &b, "the PC online again", func(r report) bool {
		for _, p := range r.Peers {
			if len(p.IPs) > 0 && p.IPs[0] == pc {
				return p.Online
			}
		}
		return false
	})
	if status, _, _ := get(t, info); status != http.StatusOK {
		t.Errorf("the PC on again: %d", status)
	}

	// Started again as it is: the same node and relay.
	if again := b.start(cfg); again.Error != nil || again.Relay != relay || again.State != tailnet.StateRunning {
		t.Errorf("started again: %s", encode(again))
	}
	if r := b.check(); r.Error != nil || r.Relay != relay {
		t.Errorf("check: %s", encode(r))
	}

	// Stopped (Mira off screen): nothing listens; started again, signed in without a page, its keys kept encrypted.
	if r := b.stop(); r.State != stateOff || r.Relay != "" {
		t.Errorf("stopped: %s", encode(r))
	}
	if _, err := net.DialTimeout("tcp", strings.SplitN(relay, "/", 4)[2], time.Second); err == nil {
		t.Errorf("the relay still listens once stopped")
	}
	var cfgValue config
	json.Unmarshal([]byte(cfg), &cfgValue)
	state, err := os.ReadFile(filepath.Join(cfgValue.Dir, "state.bin"))
	if err != nil || len(state) == 0 || strings.Contains(string(state), "PrivateNodeKey") || strings.Contains(string(state), "privkey:") {
		t.Errorf("state.bin: %v, %d bytes, in clear: %v", err, len(state), strings.Contains(string(state), "privkey:"))
	}
	if r := b.start(cfg); r.Error != nil {
		t.Fatalf("started again: %s", encode(r))
	}
	waitFor(t, ctx, &b, "running again without a sign-in", func(r report) bool { return r.State == tailnet.StateRunning })
	if status, _, _ := get(t, fmt.Sprintf("%s/http/%s:8096/System/Info/Public", b.status().Relay, pc)); status != http.StatusOK {
		t.Errorf("Jellyfin once started again: %d", status)
	}

	// Signed out: the node waits for the next sign-in, and says so.
	if a := b.logout(); a.Error != nil {
		t.Fatalf("logout: %s", encode(a))
	}
	waitFor(t, ctx, &b, "needs-login after the sign-out", func(r report) bool { return r.State == tailnet.StateNeedsLogin })

	// The node's log stayed in its folder, without the sign-in page.
	b.stop()
	b.log.close()
	b.log = nil
	data, err := os.ReadFile(filepath.Join(cfgValue.Dir, logName))
	if err != nil || len(data) == 0 {
		t.Errorf("log: %v, %d bytes", err, len(data))
	}
	if strings.Contains(string(data), "/auth/") {
		t.Errorf("the log shows a sign-in page")
	}
}

func TestBridgeRefusesABadConfiguration(t *testing.T) {
	var b bridge
	for _, text := range []string{
		`not json`,
		`{"dir":"/tmp/x","stateKey":"short","relaySecret":"relay-secret-0123456789"}`,
		`{"stateKey":"` + base64.RawURLEncoding.EncodeToString(make([]byte, 32)) + `","relaySecret":"relay-secret-0123456789"}`,
	} {
		if r := b.start(text); r.State != stateOff || r.Error == nil || r.Error.Code != "config" {
			t.Errorf("%s: %s", text, encode(r))
		}
	}
	// A secret too weak for the relay: the node doesn't stay up without it.
	key := base64.RawURLEncoding.EncodeToString(make([]byte, 32))
	text := `{"dir":"` + filepath.Join(t.TempDir(), "Tailscale") + `","stateKey":"` + key + `","relaySecret":"short","controlURL":"http://127.0.0.1:1"}`
	if r := b.start(text); r.State != stateOff || r.Error == nil || r.Error.Code != "relay" {
		t.Errorf("weak secret: %s", encode(r))
	}
	if r := b.status(); r.State != stateOff {
		t.Errorf("status: %s", encode(r))
	}
	b.log.close()
}

func TestBridgeBeforeStart(t *testing.T) {
	var b bridge
	if r := b.status(); r.State != stateOff || r.Relay != "" || r.Error != nil {
		t.Errorf("status: %s", encode(r))
	}
	if r := b.check(); r.State != stateOff {
		t.Errorf("check: %s", encode(r))
	}
	if a := b.login(); a.Error == nil || a.Error.Code != tailnet.CodeStopped {
		t.Errorf("login: %s", encode(a))
	}
	if a := b.logout(); a.Error == nil || a.Error.Code != tailnet.CodeStopped {
		t.Errorf("logout: %s", encode(a))
	}
	if r := b.stop(); r.State != stateOff {
		t.Errorf("stop: %s", encode(r))
	}
	b.network("en0")
}

func TestStatusJSON(t *testing.T) {
	text := encode(report{Status: tailnet.Status{State: tailnet.StateRunning, Peers: []tailnet.Device{{ID: "1", HostName: "PC", IPs: []string{"100.64.0.2"}, Online: true}}},
		Relay: "http://127.0.0.1:48096/s"})
	var back map[string]any
	if err := json.Unmarshal([]byte(text), &back); err != nil {
		t.Fatal(err)
	}
	// The names the app reads (TailnetStatus).
	for _, name := range []string{"state", "peers", "relay"} {
		if _, ok := back[name]; !ok {
			t.Errorf("%s missing in %s", name, text)
		}
	}
	if strings.Contains(text, `"error"`) {
		t.Errorf("an error without one: %s", text)
	}
}
