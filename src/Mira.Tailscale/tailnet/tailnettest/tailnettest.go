// Package tailnettest runs a Tailscale network of its own, for tests and the mira-testnet bench: Tailscale's test
// coordination server, a relay (DERP) and a STUN server on one address, and a « Jellyfin PC » node that exposes local
// ports on the test tailnet. Logging in is the real interactive flow: the login page (GET /auth/<id> on the control
// address) logs the node in and says so.
package tailnettest

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"errors"
	"fmt"
	"html"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"net/netip"
	"net/url"
	"os"
	"slices"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"tailscale.com/derp/derpserver"
	"tailscale.com/envknob"
	"tailscale.com/logtail"
	"tailscale.com/net/netns"
	"tailscale.com/net/stun/stuntest"
	"tailscale.com/tailcfg"
	"tailscale.com/tailcfg/nodecap"
	"tailscale.com/tsnet"
	"tailscale.com/tstest/integration/testcontrol"
	"tailscale.com/types/key"
	"tailscale.com/types/logger"
	"tailscale.com/types/nettype"
)

func init() {
	// The Jellyfin PC is a node too: no logs to Tailscale from the bench either, whatever the build.
	envknob.SetNoLogsNoSupport()
	logtail.Disable()
}

// MagicDNSSuffix is the test tailnet's: a ts.net name, as the apps expect of a tailnet.
const MagicDNSSuffix = "mira-test.ts.net"

// Options shape the test network.
type Options struct {
	// Addr is where the coordination server listens (« 127.0.0.1:18090 »; port 0 takes a free one). The relay and the
	// STUN server listen on its IP, on ports of their own.
	Addr string
	// Hostname is the Jellyfin PC's name on the tailnet: « jellyfin-pc » by default.
	Hostname string
	// Forward exposes local addresses on the Jellyfin PC: tailnet port → local « host:port ».
	Forward map[uint16]string
	// Login: "interactive" (the default: a login page per node), "none" (every node joins at once).
	Login string
	// Approval makes new devices wait for POST /mira/approve, as a tailnet that approves its devices does.
	Approval bool
	// CacheNetmaps grants every node « cache-network-maps », as the tailnet policy's nodeAttrs do: a node keeps its
	// network map on disk and starts from it while the coordination server is down (SetControlDown).
	CacheNetmaps bool
	// Dir keeps the Jellyfin PC's state: a temporary folder by default.
	Dir string
	// Logf receives the network's diagnostics; nil drops them.
	Logf func(format string, args ...any)
}

// Net is a running test network.
type Net struct {
	opts     Options
	control  *testcontrol.Server
	server   *http.Server
	base     string
	tb       *bench
	tempDir  string
	down     atomic.Bool // the coordination server answers 503, as during an outage at Tailscale's
	mu       sync.Mutex
	pc       *tsnet.Server
	pcKey    key.NodePublic
	pcIPs    []netip.Addr
	pcName   string
	forwards []net.Listener

	expiredMu sync.Mutex
	expired   map[key.NodePublic]bool // keys made to expire by Expire
}

// Info describes the network, as the bench prints it (MIRA-TESTNET {…}) and serves it (GET /mira/info).
type Info struct {
	Control        string            `json:"control"`
	MagicDNSSuffix string            `json:"magicDNSSuffix"`
	Jellyfin       Device            `json:"jellyfin"`
	Forward        map[uint16]string `json:"forward,omitempty"`
}

// Device is a node of the test network.
type Device struct {
	Name    string   `json:"name"`
	DNSName string   `json:"dnsName"`
	IPs     []string `json:"ips"`
	Online  bool     `json:"online"`
}

// Start starts the network and its Jellyfin PC, logged in and online.
func Start(opts Options) (*Net, error) {
	if opts.Addr == "" {
		opts.Addr = "127.0.0.1:0"
	}
	if opts.Hostname == "" {
		opts.Hostname = "jellyfin-pc"
	}
	if opts.Logf == nil {
		opts.Logf = logger.Discard
	}
	switch opts.Login {
	case "":
		opts.Login = "interactive"
	case "interactive", "none":
	default:
		return nil, fmt.Errorf("tailnettest: login %q: interactive or none", opts.Login)
	}
	host, _, err := net.SplitHostPort(opts.Addr)
	if err != nil {
		return nil, err
	}
	if ip, err := netip.ParseAddr(host); err == nil && ip.IsLoopback() {
		// Sockets bound to the default network interface can't reach a loopback coordination server (Linux).
		netns.SetEnabled(false)
	}
	ln, err := net.Listen("tcp", opts.Addr)
	if err != nil {
		return nil, err
	}
	n := &Net{opts: opts, tb: &bench{logf: opts.Logf}, base: "http://" + ln.Addr().String()}
	derpMap := runRelay(n.tb, host)
	n.control = &testcontrol.Server{
		DERPMap:            derpMap,
		DNSConfig:          &tailcfg.DNSConfig{Proxied: true},
		MagicDNSDomain:     MagicDNSSuffix,
		RequireAuth:        opts.Login == "interactive",
		RequireMachineAuth: opts.Approval,
		AllNodesSameUser:   true,
		ExplicitBaseURL:    n.base,
		Logf:               logger.WithPrefix(opts.Logf, "control: "),
	}
	if opts.CacheNetmaps {
		n.control.DefaultNodeCapabilities = &tailcfg.NodeCapMap{nodecap.CacheNetworkMaps: []tailcfg.RawMessage{}}
	}
	// A node that logs in again after its key expired gets a new key, which the test coordination server gives the old
	// key's expiry: a new key starts afresh, as on Tailscale's.
	n.control.SetOnMapRequest(func(k key.NodePublic) { go n.unexpire(k) })
	mux := http.NewServeMux()
	control := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if n.down.Load() {
			http.Error(w, "the coordination server is down", http.StatusServiceUnavailable)
			return
		}
		n.control.ServeHTTP(w, r)
	})
	for _, path := range []string{"/key", "/machine/", "/ts2021", "/generate_204"} {
		mux.Handle(path, control)
	}
	mux.HandleFunc("/auth/", n.serveLogin)
	mux.HandleFunc("/mira/", n.serveBench)
	// Anything else would make the test coordination server panic.
	mux.HandleFunc("/", http.NotFound)
	n.server = &http.Server{Handler: mux, ReadHeaderTimeout: 10 * time.Second}
	go n.server.Serve(ln)
	if opts.Dir == "" {
		if n.tempDir, err = os.MkdirTemp("", "mira-testnet-"); err != nil {
			n.Close()
			return nil, err
		}
		n.opts.Dir = n.tempDir
	}
	if err := n.SetJellyfinOnline(true); err != nil {
		n.Close()
		return nil, err
	}
	return n, nil
}

// ControlURL is the coordination server's address, for Config.ControlURL.
func (n *Net) ControlURL() string { return n.base }

// SetControlDown makes the coordination server answer 503 to every new request, as during an outage at Tailscale's;
// the connections it holds already stay open.
func (n *Net) SetControlDown(down bool) { n.down.Store(down) }

// Control is the test coordination server.
func (n *Net) Control() *testcontrol.Server { return n.control }

// Info describes the network.
func (n *Net) Info() Info {
	n.mu.Lock()
	defer n.mu.Unlock()
	info := Info{Control: n.base, MagicDNSSuffix: MagicDNSSuffix, Forward: n.opts.Forward}
	info.Jellyfin = Device{Name: n.opts.Hostname, DNSName: n.pcName, Online: n.pc != nil}
	for _, ip := range n.pcIPs {
		info.Jellyfin.IPs = append(info.Jellyfin.IPs, ip.String())
	}
	return info
}

// Close stops everything.
func (n *Net) Close() {
	n.SetJellyfinOnline(false)
	if n.server != nil {
		n.server.Close()
	}
	n.tb.cleanup()
	if n.tempDir != "" {
		os.RemoveAll(n.tempDir)
	}
}

// SetJellyfinOnline starts the Jellyfin PC (from its saved state: same addresses) or stops it, as switching that PC on
// or off would.
func (n *Net) SetJellyfinOnline(online bool) error {
	n.mu.Lock()
	defer n.mu.Unlock()
	if online == (n.pc != nil) {
		return nil
	}
	if !online {
		for _, ln := range n.forwards {
			ln.Close()
		}
		n.forwards = nil
		n.pc.Close()
		n.pc = nil
		n.markOnline(n.pcKey, false)
		return nil
	}
	pc := &tsnet.Server{
		Dir:        n.opts.Dir,
		Hostname:   n.opts.Hostname,
		ControlURL: n.base,
		Logf:       logger.WithPrefix(n.opts.Logf, n.opts.Hostname+": "),
		UserLogf:   logger.WithPrefix(n.opts.Logf, n.opts.Hostname+": "),
	}
	ctx, cancel := context.WithTimeout(context.Background(), time.Minute)
	defer cancel()
	if err := n.logIn(ctx, pc); err != nil {
		pc.Close()
		return err
	}
	st, err := pc.Up(ctx)
	if err != nil {
		pc.Close()
		return err
	}
	for port, target := range n.opts.Forward {
		ln, err := pc.Listen("tcp", fmt.Sprintf(":%d", port))
		if err != nil {
			pc.Close()
			return err
		}
		n.forwards = append(n.forwards, ln)
		go forward(ln, target, n.opts.Logf)
	}
	n.pc, n.pcKey = pc, st.Self.PublicKey
	n.pcIPs = slices.Clone(st.TailscaleIPs)
	n.pcName = strings.TrimSuffix(st.Self.DNSName, ".")
	n.markOnline(n.pcKey, true)
	return nil
}

// logIn starts pc and logs it in, approving it if the network approves devices.
func (n *Net) logIn(ctx context.Context, pc *tsnet.Server) error {
	if err := pc.Start(); err != nil {
		return err
	}
	lc, err := pc.LocalClient()
	if err != nil {
		return err
	}
	for {
		st, err := lc.StatusWithoutPeers(ctx)
		if err != nil {
			return err
		}
		switch {
		case st.BackendState == "Running":
			return nil
		case st.AuthURL != "":
			n.control.CompleteAuth(st.AuthURL)
		case st.BackendState == "NeedsMachineAuth":
			n.ApproveAll()
		}
		select {
		case <-ctx.Done():
			return fmt.Errorf("tailnettest: %s didn't join the network: %w", n.opts.Hostname, ctx.Err())
		case <-time.After(50 * time.Millisecond):
		}
	}
}

// markOnline tells the other devices whether a device is connected to Tailscale (the test coordination server doesn't
// track it by itself).
func (n *Net) markOnline(k key.NodePublic, online bool) {
	if k.IsZero() {
		return
	}
	node := n.control.Node(k)
	if node == nil {
		return
	}
	node.Online = &online
	if !online {
		now := time.Now()
		node.LastSeen = &now
	}
	n.control.UpdateNode(node)
}

// CompleteLogin logs in the node whose login page is url (what opening it does).
func (n *Net) CompleteLogin(url string) bool { return n.control.CompleteAuth(url) }

// ApproveAll approves the devices waiting for approval, and says how many.
func (n *Net) ApproveAll() int {
	count := 0
	for _, node := range n.control.AllNodes() {
		if !node.MachineAuthorized && n.control.CompleteDeviceApproval(n.base, n.base+"/admin", &node.Key) {
			count++
		}
	}
	return count
}

// Expire makes the key of the devices named name expire now, as 180 days would.
func (n *Net) Expire(name string) int {
	count := 0
	for _, node := range n.control.AllNodes() {
		if !strings.EqualFold(node.Hostinfo.Hostname(), name) {
			continue
		}
		n.expiredMu.Lock()
		if n.expired == nil {
			n.expired = map[key.NodePublic]bool{}
		}
		n.expired[node.Key] = true
		n.expiredMu.Unlock()
		node.KeyExpiry = time.Now().Add(-time.Minute)
		node.Expired = true
		n.control.UpdateNode(node)
		count++
	}
	return count
}

// unexpire clears the expiry a new key inherited from the one that expired.
func (n *Net) unexpire(k key.NodePublic) {
	n.expiredMu.Lock()
	expired := n.expired[k]
	n.expiredMu.Unlock()
	if expired {
		return
	}
	node := n.control.Node(k)
	if node == nil || node.KeyExpiry.IsZero() || node.KeyExpiry.After(time.Now()) {
		return
	}
	node.KeyExpiry = time.Time{}
	node.Expired = false
	n.control.UpdateNode(node)
}

// serveLogin is the login page: it logs the node in, as approving it on Tailscale's site does.
func (n *Net) serveLogin(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		http.Error(w, "GET", http.StatusMethodNotAllowed)
		return
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	if !n.control.CompleteAuth(r.URL.Path) {
		w.WriteHeader(http.StatusNotFound)
		io.WriteString(w, page("Lien de connexion inconnu ou déjà utilisé", "Unknown or used login link"))
		return
	}
	io.WriteString(w, page("Connecté au réseau Tailscale de test", "Signed in to the test Tailscale network"))
}

func page(fr, en string) string {
	return `<!doctype html><html lang="fr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">` +
		`<title>` + html.EscapeString(fr) + `</title></head><body style="font-family:system-ui;margin:2em">` +
		`<h1 id="mira-testnet-result">` + html.EscapeString(fr) + `</h1><p lang="en">` + html.EscapeString(en) + `</p></body></html>`
}

// serveBench drives the network from a test: GET /mira/info, POST /mira/jellyfin?online=0|1, POST /mira/expire?name=…,
// POST /mira/approve.
func (n *Net) serveBench(w http.ResponseWriter, r *http.Request) {
	if r.Method == http.MethodGet && r.URL.Path == "/mira/info" {
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(n.Info())
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "POST", http.StatusMethodNotAllowed)
		return
	}
	query, _ := url.ParseQuery(r.URL.RawQuery)
	var err error
	switch r.URL.Path {
	case "/mira/jellyfin":
		err = n.SetJellyfinOnline(query.Get("online") != "0")
	case "/mira/expire":
		if n.Expire(query.Get("name")) == 0 {
			err = errors.New("no such device")
		}
	case "/mira/approve":
		fmt.Fprintf(w, "%d\n", n.ApproveAll())
		return
	default:
		http.NotFound(w, r)
		return
	}
	if err != nil {
		http.Error(w, err.Error(), http.StatusConflict)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// runRelay runs a relay (DERP) and a STUN server on ip, as Tailscale's integration tests do (without their package,
// which needs the features Mira's build leaves out).
func runRelay(tb *bench, ip string) *tailcfg.DERPMap {
	d := derpserver.New(key.NewNode(), logger.Discard)
	ln, err := net.Listen("tcp", net.JoinHostPort(ip, "0"))
	if err != nil {
		tb.Fatal(err)
	}
	srv := httptest.NewUnstartedServer(derpserver.Handler(d))
	srv.Listener.Close()
	srv.Listener = ln
	srv.Config.ErrorLog = logger.StdLogger(logger.Discard)
	srv.Config.TLSNextProto = make(map[string]func(*http.Server, *tls.Conn, http.Handler))
	srv.StartTLS()
	stunAddr, stunStop := stuntest.ServeWithPacketListener(tb, nettype.Std{})
	tb.Cleanup(func() {
		srv.CloseClientConnections()
		srv.Close()
		d.Close()
		stunStop()
	})
	return &tailcfg.DERPMap{Regions: map[tailcfg.DERPRegionID]*tailcfg.DERPRegion{
		1: {RegionID: 1, RegionCode: "test", RegionName: "Mira test", Nodes: []*tailcfg.DERPNode{{
			Name:             "t1",
			RegionID:         1,
			HostName:         ip,
			IPv4:             ip,
			IPv6:             "none",
			STUNPort:         stunAddr.Port,
			DERPPort:         ln.Addr().(*net.TCPAddr).Port,
			InsecureForTests: true,
			STUNTestIP:       ip,
		}}},
	}}
}

// forward passes each connection to the tailnet port on to target.
func forward(ln net.Listener, target string, logf func(string, ...any)) {
	for {
		c, err := ln.Accept()
		if err != nil {
			return
		}
		go func() {
			defer c.Close()
			t, err := net.DialTimeout("tcp", target, 10*time.Second)
			if err != nil {
				logf("forward to %s: %v", target, err)
				return
			}
			defer t.Close()
			done := make(chan struct{}, 2)
			go func() { io.Copy(t, c); closeWrite(t); done <- struct{}{} }()
			go func() { io.Copy(c, t); closeWrite(c); done <- struct{}{} }()
			<-done
			<-done
		}()
	}
}

func closeWrite(c net.Conn) {
	if cw, ok := c.(interface{ CloseWrite() error }); ok {
		cw.CloseWrite()
	}
}

// bench stands in for the testing.TB that Tailscale's relay and STUN helpers expect.
type bench struct {
	testing.TB
	logf     func(string, ...any)
	mu       sync.Mutex
	cleanups []func()
}

func (b *bench) Helper()                         {}
func (b *bench) Name() string                    { return "mira-testnet" }
func (b *bench) Logf(format string, args ...any) { b.logf(format, args...) }
func (b *bench) Log(args ...any)                 { b.logf("%s", fmt.Sprint(args...)) }
func (b *bench) Errorf(format string, args ...any) {
	b.logf(format, args...)
}
func (b *bench) Fatalf(format string, args ...any) { panic(fmt.Sprintf(format, args...)) }
func (b *bench) Fatal(args ...any)                 { panic(fmt.Sprint(args...)) }
func (b *bench) Failed() bool                      { return false }
func (b *bench) Cleanup(f func()) {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.cleanups = append(b.cleanups, f)
}

func (b *bench) cleanup() {
	b.mu.Lock()
	fs := b.cleanups
	b.cleanups = nil
	b.mu.Unlock()
	for i := len(fs) - 1; i >= 0; i-- {
		fs[i]()
	}
}
