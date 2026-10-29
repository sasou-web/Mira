package tailnet_test

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"miracinema.app/tailscale/tailnet"
	"miracinema.app/tailscale/tailnet/tailnettest"
)

// A connection to the Jellyfin PC ends as soon as the PC goes offline, said as peer-offline: a request waiting on it
// doesn't hang until its client gives up, nor does one kept idle for the next request.
func TestDialEndsWhenTheDeviceGoesOffline(t *testing.T) {
	if testing.Short() {
		t.Skip("a whole Tailscale network")
	}
	started := make(chan struct{}, 4)
	// The PC going away mid-request: /hang never answers.
	pcJellyfin := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/hang" {
			started <- struct{}{}
			<-r.Context().Done()
			return
		}
		io.WriteString(w, "ok")
	}))
	t.Cleanup(pcJellyfin.Close)
	network, err := tailnettest.Start(tailnettest.Options{Login: "none", Forward: map[uint16]string{8096: strings.TrimPrefix(pcJellyfin.URL, "http://")}})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(network.Close)
	node, err := tailnet.Start(tailnet.Config{Dir: t.TempDir(), Hostname: "mira-test", ControlURL: network.ControlURL(), StateKey: key(t)})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { node.Close() })
	pc := network.Info().Jellyfin
	address := pc.IPs[0] + ":8096"
	online := func(want bool) func(tailnet.Status) bool {
		return func(st tailnet.Status) bool {
			for _, d := range st.Peers {
				if d.DNSName == pc.DNSName {
					return st.State == tailnet.StateRunning && d.Online == want
				}
			}
			return false
		}
	}
	waitFor(t, node, "the PC online", online(true))

	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	waiting, err := node.Dial(ctx, "tcp", address)
	if err != nil {
		t.Fatal(err)
	}
	defer waiting.Close()
	fmt.Fprintf(waiting, "GET /hang HTTP/1.1\r\nHost: %s\r\n\r\n", address)
	idle, err := node.Dial(ctx, "tcp", address)
	if err != nil {
		t.Fatal(err)
	}
	defer idle.Close()
	select {
	case <-started:
	case <-time.After(30 * time.Second):
		t.Fatal("the request never reached the PC")
	}
	ended := make(chan error, 2)
	for _, c := range []interface{ Read([]byte) (int, error) }{waiting, idle} {
		go func() {
			_, err := c.Read(make([]byte, 1))
			ended <- err
		}()
	}

	if err := network.SetJellyfinOnline(false); err != nil {
		t.Fatal(err)
	}
	for range 2 {
		select {
		case err := <-ended:
			if code := tailnet.CodeOf(err); code != tailnet.CodePeerOffline {
				t.Errorf("a connection to the PC gone ended with %v (%q)", err, code)
			}
		case <-time.After(45 * time.Second):
			t.Fatal("a connection to the PC gone still waits")
		}
	}

	// Back on: dialed again, it answers.
	if err := network.SetJellyfinOnline(true); err != nil {
		t.Fatal(err)
	}
	waitFor(t, node, "the PC online again", online(true))
	client := &http.Client{Transport: &http.Transport{DialContext: node.Dial}, Timeout: 30 * time.Second}
	if res, body := get(t, client, "http://"+address+"/"); res.StatusCode != http.StatusOK || body != "ok" {
		t.Errorf("the PC back: %d %q", res.StatusCode, body)
	}
}
