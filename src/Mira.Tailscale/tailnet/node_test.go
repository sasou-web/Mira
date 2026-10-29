package tailnet_test

import (
	"bufio"
	"context"
	"crypto/rand"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"miracinema.app/tailscale/tailnet"
	"miracinema.app/tailscale/tailnet/tailnettest"
)

// jellyfin stands in for Jellyfin: it answers /System/Info/Public with its name.
func jellyfin(t *testing.T) string {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprintf(w, `{"ServerName":"Jellyfin de test","Path":%q}`, r.URL.Path)
	}))
	t.Cleanup(srv.Close)
	return strings.TrimPrefix(srv.URL, "http://")
}

func waitFor(t *testing.T, node *tailnet.Node, what string, ok func(tailnet.Status) bool) tailnet.Status {
	t.Helper()
	got := make(chan tailnet.Status, 1)
	stop := node.Watch(func(st tailnet.Status) {
		if ok(st) {
			select {
			case got <- st:
			default:
			}
		}
	})
	defer stop()
	select {
	case st := <-got:
		return st
	case <-time.After(45 * time.Second):
		t.Fatalf("waiting for %s: status %+v", what, node.Status())
		return tailnet.Status{}
	}
}

func key(t *testing.T) []byte {
	k := make([]byte, 32)
	rand.Read(k)
	return k
}

// logIn opens the node's login page, as the person would in a browser.
func logIn(t *testing.T, node *tailnet.Node) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	page, err := node.Login(ctx)
	if err != nil {
		t.Fatalf("Login: %v", err)
	}
	if !strings.Contains(page, "/auth/") {
		t.Fatalf("login page %q", page)
	}
	res, err := http.Get(page)
	if err != nil {
		t.Fatal(err)
	}
	body, _ := io.ReadAll(res.Body)
	res.Body.Close()
	if res.StatusCode != 200 || !strings.Contains(string(body), "Connecté") {
		t.Fatalf("login page answered %d: %s", res.StatusCode, body)
	}
}

func get(t *testing.T, client *http.Client, address string) (*http.Response, string) {
	t.Helper()
	res, err := client.Get(address)
	if err != nil {
		t.Fatalf("GET %s: %v", address, err)
	}
	body, _ := io.ReadAll(res.Body)
	res.Body.Close()
	return res, string(body)
}

func code(t *testing.T, err error) string {
	t.Helper()
	if err == nil {
		t.Fatal("no error")
	}
	return tailnet.CodeOf(err)
}

func TestNode(t *testing.T) {
	if testing.Short() {
		t.Skip("starts a Tailscale network")
	}
	jf := jellyfin(t)
	tn, err := tailnettest.Start(tailnettest.Options{Forward: map[uint16]string{8096: jf}, Logf: t.Logf})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(tn.Close)
	pc := tn.Info().Jellyfin

	dir := filepath.Join(t.TempDir(), "tailscale")
	stateKey := key(t)
	start := func(k []byte) *tailnet.Node {
		t.Helper()
		node, err := tailnet.Start(tailnet.Config{Dir: dir, Hostname: "Mira-Test Mac", ControlURL: tn.ControlURL(), StateKey: k, Logf: t.Logf})
		if err != nil {
			t.Fatal(err)
		}
		return node
	}
	node := start(stateKey)

	// Never logged in: the login page, then the tailnet.
	waitFor(t, node, "needs-login", func(st tailnet.Status) bool { return st.State == tailnet.StateNeedsLogin })
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	_, err = node.Dial(ctx, "tcp", pc.IPs[0]+":8096")
	cancel()
	if c := code(t, err); c != tailnet.CodeNeedsLogin {
		t.Fatalf("dial before login: %s", c)
	}
	logIn(t, node)
	st := waitFor(t, node, "the Jellyfin PC online", func(st tailnet.Status) bool {
		for _, p := range st.Peers {
			if p.HostName == "jellyfin-pc" && p.Online {
				return st.State == tailnet.StateRunning
			}
		}
		return false
	})
	if st.Self == nil || st.Self.DNSName != "mira-test-mac."+tailnettest.MagicDNSSuffix || len(st.Self.IPs) == 0 {
		t.Fatalf("self: %+v", st.Self)
	}
	if st.MagicDNSSuffix != tailnettest.MagicDNSSuffix {
		t.Errorf("MagicDNS suffix %q", st.MagicDNSSuffix)
	}
	selfIP := st.Self.IPs[0]

	// Jellyfin by IP, by MagicDNS name, by short name.
	direct := &http.Client{Transport: &http.Transport{DialContext: node.Dial}, Timeout: 20 * time.Second}
	for _, host := range []string{pc.IPs[0], "[" + pc.IPs[1] + "]", pc.DNSName, "jellyfin-pc", "JELLYFIN-PC"} {
		res, body := get(t, direct, "http://"+host+":8096/System/Info/Public")
		if res.StatusCode != 200 || !strings.Contains(body, "Jellyfin de test") {
			t.Fatalf("Jellyfin at %s: %d %s", host, res.StatusCode, body)
		}
	}

	// Never outside the tailnet: not even this computer.
	ctx, cancel = context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if _, err := node.Dial(ctx, "tcp", jf); code(t, err) != tailnet.CodeRefused {
		t.Fatalf("dial %s: %v", jf, err)
	}
	if _, err := node.Dial(ctx, "tcp", "100.64.250.250:8096"); code(t, err) != tailnet.CodeRefused {
		t.Fatalf("dial an unknown tailnet IP: %v", err)
	}
	if _, err := node.Dial(ctx, "tcp", selfIP+":8096"); code(t, err) != tailnet.CodeRefused {
		t.Fatalf("dial itself: %v", err)
	}
	if _, err := node.Dial(ctx, "udp", pc.IPs[0]+":8096"); code(t, err) != tailnet.CodeRefused {
		t.Fatalf("dial UDP: %v", err)
	}
	// Online, but nothing on that port.
	err = node.Probe(ctx, "jellyfin-pc:8097")
	if c := code(t, err); c != tailnet.CodeClosed && c != tailnet.CodeUnreachable {
		t.Fatalf("probe a closed port: %v", err)
	}
	if err := node.Probe(ctx, "jellyfin-pc:8096"); err != nil {
		t.Fatalf("probe Jellyfin: %v", err)
	}

	// The proxy: HTTP for mpv, SOCKS5 for .NET, both behind credentials.
	proxy, err := node.ServeProxy("127.0.0.1:0", "mira", "s3cret")
	if err != nil {
		t.Fatal(err)
	}
	defer proxy.Close()
	if _, err := node.ServeProxy("0.0.0.0:0", "mira", "s3cret"); err == nil {
		t.Fatal("proxy on every interface")
	}
	through := func(proxyURL string) *http.Client {
		u, _ := url.Parse(proxyURL)
		return &http.Client{Transport: &http.Transport{Proxy: http.ProxyURL(u)}, Timeout: 30 * time.Second}
	}
	for _, scheme := range []string{"http", "socks5"} {
		client := through(scheme + "://mira:s3cret@" + proxy.Addr())
		res, body := get(t, client, "http://"+pc.DNSName+":8096/System/Info/Public?ApiKey=abc")
		if res.StatusCode != 200 || !strings.Contains(body, "Jellyfin de test") {
			t.Fatalf("%s proxy: %d %s", scheme, res.StatusCode, body)
		}
	}
	res, _ := get(t, through("http://"+proxy.Addr()), "http://jellyfin-pc:8096/")
	if res.StatusCode != http.StatusProxyAuthRequired {
		t.Fatalf("HTTP proxy without credentials: %d", res.StatusCode)
	}
	res, _ = get(t, through("http://mira:wrong@"+proxy.Addr()), "http://jellyfin-pc:8096/")
	if res.StatusCode != http.StatusProxyAuthRequired {
		t.Fatalf("HTTP proxy with a wrong password: %d", res.StatusCode)
	}
	if _, err := through("socks5://mira:wrong@" + proxy.Addr()).Get("http://jellyfin-pc:8096/"); err == nil {
		t.Fatal("SOCKS5 with a wrong password")
	}
	res, _ = get(t, through("http://mira:s3cret@"+proxy.Addr()), "http://"+strings.Replace(jf, "127.0.0.1", "127.0.0.1", 1)+"/")
	if res.StatusCode != http.StatusBadGateway || res.Header.Get(tailnet.ProxyHeader) != tailnet.CodeRefused {
		t.Fatalf("HTTP proxy to this computer: %d %q", res.StatusCode, res.Header.Get(tailnet.ProxyHeader))
	}
	// A tunnel, as .NET opens one for a WebSocket.
	conn, err := net.Dial("tcp", proxy.Addr())
	if err != nil {
		t.Fatal(err)
	}
	fmt.Fprintf(conn, "CONNECT jellyfin-pc:8096 HTTP/1.1\r\nHost: jellyfin-pc:8096\r\nProxy-Authorization: Basic %s\r\n\r\nGET /socket HTTP/1.1\r\nHost: jellyfin-pc\r\nConnection: close\r\n\r\n", "bWlyYTpzM2NyZXQ=")
	tunnel, err := io.ReadAll(bufio.NewReader(conn))
	conn.Close()
	if err != nil || !strings.HasPrefix(string(tunnel), "HTTP/1.1 200") || !strings.Contains(string(tunnel), `"Path":"/socket"`) {
		t.Fatalf("tunnel: %v %q", err, tunnel)
	}

	// The Jellyfin PC switched off: said, then back with the same address.
	if err := tn.SetJellyfinOnline(false); err != nil {
		t.Fatal(err)
	}
	waitFor(t, node, "the Jellyfin PC offline", func(st tailnet.Status) bool {
		for _, p := range st.Peers {
			if p.HostName == "jellyfin-pc" {
				return !p.Online && p.LastSeen != nil
			}
		}
		return false
	})
	ctx2, cancel2 := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel2()
	if err := node.Probe(ctx2, "jellyfin-pc:8096"); code(t, err) != tailnet.CodePeerOffline {
		t.Fatalf("probe the PC switched off: %v", err)
	}
	res, _ = get(t, through("http://mira:s3cret@"+proxy.Addr()), "http://jellyfin-pc:8096/")
	if res.StatusCode != http.StatusBadGateway || res.Header.Get(tailnet.ProxyHeader) != tailnet.CodePeerOffline {
		t.Fatalf("proxy to the PC switched off: %d %q", res.StatusCode, res.Header.Get(tailnet.ProxyHeader))
	}
	if err := tn.SetJellyfinOnline(true); err != nil {
		t.Fatal(err)
	}
	if tn.Info().Jellyfin.IPs[0] != pc.IPs[0] {
		t.Fatalf("the PC came back at %v, not %v", tn.Info().Jellyfin.IPs, pc.IPs)
	}
	waitFor(t, node, "the Jellyfin PC back", func(st tailnet.Status) bool {
		for _, p := range st.Peers {
			if p.HostName == "jellyfin-pc" {
				return p.Online
			}
		}
		return false
	})
	if err := node.Probe(ctx2, "jellyfin-pc:8096"); err != nil {
		t.Fatalf("probe the PC back: %v", err)
	}
	node.Rebind()
	if err := node.Probe(ctx2, "jellyfin-pc:8096"); err != nil {
		t.Fatalf("probe after Rebind: %v", err)
	}

	// One node per folder.
	if _, err := tailnet.Start(tailnet.Config{Dir: dir, ControlURL: tn.ControlURL(), StateKey: stateKey}); tailnet.CodeOf(err) != tailnet.CodeLocked {
		t.Fatalf("second node on the same folder: %v", err)
	}

	// Closed and started again: still logged in, same address, nothing in clear on disk.
	proxy.Close()
	if err := node.Close(); err != nil {
		t.Fatal(err)
	}
	if st := node.Status(); st.State != tailnet.StateStopped {
		t.Fatalf("closed node: %s", st.State)
	}
	if _, err := node.Dial(ctx2, "tcp", "jellyfin-pc:8096"); code(t, err) != tailnet.CodeStopped {
		t.Fatalf("dial a closed node: %v", err)
	}
	files, _ := os.ReadDir(dir)
	for _, f := range files {
		if strings.Contains(f.Name(), "tailscaled.state") {
			t.Errorf("Tailscale's clear state file: %s", f.Name())
		}
	}
	node = start(stateKey)
	st = waitFor(t, node, "running again", func(st tailnet.Status) bool { return st.State == tailnet.StateRunning })
	if st.Self.IPs[0] != selfIP {
		t.Fatalf("started again at %s, not %s", st.Self.IPs[0], selfIP)
	}
	if err := node.Probe(ctx2, "jellyfin-pc:8096"); err != nil {
		t.Fatalf("probe after a restart: %v", err)
	}

	// The key expired: said, and a new login brings it back.
	if tn.Expire("mira-test-mac") == 0 {
		t.Fatal("no node to expire")
	}
	waitFor(t, node, "expired", func(st tailnet.Status) bool { return st.State == tailnet.StateExpired })
	if _, err := node.Dial(ctx2, "tcp", "jellyfin-pc:8096"); code(t, err) != tailnet.CodeExpired {
		t.Fatalf("dial with an expired key: %v", err)
	}
	logIn(t, node)
	waitFor(t, node, "running after a new login", func(st tailnet.Status) bool { return st.State == tailnet.StateRunning })

	// Signed out: needs a login again.
	ctx3, cancel3 := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel3()
	if err := node.Logout(ctx3); err != nil {
		t.Fatal(err)
	}
	waitFor(t, node, "needs-login after logout", func(st tailnet.Status) bool { return st.State == tailnet.StateNeedsLogin })
	node.Close()

	// The keychain lost the key: the node starts over, the old state kept aside.
	node = start(key(t))
	waitFor(t, node, "needs-login with another key", func(st tailnet.Status) bool { return st.State == tailnet.StateNeedsLogin })
	node.Close()
	if aside, _ := filepath.Glob(filepath.Join(dir, "state.bin.bad-*")); len(aside) != 1 {
		t.Fatalf("state kept aside: %v", aside)
	}
}

func TestApproval(t *testing.T) {
	if testing.Short() {
		t.Skip("starts a Tailscale network")
	}
	tn, err := tailnettest.Start(tailnettest.Options{Approval: true, Logf: t.Logf})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(tn.Close)
	node, err := tailnet.Start(tailnet.Config{Dir: t.TempDir(), Hostname: "Mira", ControlURL: tn.ControlURL(), StateKey: key(t), Logf: t.Logf})
	if err != nil {
		t.Fatal(err)
	}
	defer node.Close()
	logIn(t, node)
	waitFor(t, node, "machine-auth", func(st tailnet.Status) bool { return st.State == tailnet.StateMachineAuth })
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	if _, err := node.Dial(ctx, "tcp", "jellyfin-pc:8096"); code(t, err) != tailnet.CodeMachineAuth {
		t.Fatalf("dial before approval: %v", err)
	}
	if tn.ApproveAll() == 0 {
		t.Fatal("nothing to approve")
	}
	waitFor(t, node, "running once approved", func(st tailnet.Status) bool { return st.State == tailnet.StateRunning })
}
