// Package tailnet runs Mira's own Tailscale node: a tsnet node, in user space, that reaches the person's Jellyfin over
// their tailnet without the Tailscale app, a network driver or administrator rights. Windows and Mac run it in the
// mira-tailscale helper (cmd/mira-tailscale), the iPhone app in its own process.
//
// Privacy: the node talks to Tailscale's coordination server and relays, as any Tailscale device does, and to nothing
// else of Tailscale's. The log uploader is compiled out (build-tags.txt, checked by TestBuildTags), and the node tells the
// coordination server it keeps no logs, as tailscaled --no-logs-no-support does. Its own diagnostics go to Config.Logf,
// with login pages hidden, for a local file at most.
package tailnet

import (
	"context"
	"errors"
	"fmt"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
	"unicode"

	"golang.org/x/text/unicode/norm"
	"tailscale.com/client/local"
	"tailscale.com/envknob"
	"tailscale.com/ipn"
	"tailscale.com/logtail"
	"tailscale.com/tsnet"
)

func init() {
	// Before any node exists: the node's description sent to the coordination server reads it once.
	envknob.SetNoLogsNoSupport()
	logtail.Disable()
}

// Config is what an app gives its node.
type Config struct {
	// Dir holds the node's files: its state, encrypted with StateKey (state.bin), and the lock that keeps a second Mira
	// off the same node (state.lock). Created 0700 if missing.
	Dir string
	// Hostname is the device's name in the Tailscale admin console and in MagicDNS, cleaned to a DNS label (Hostname).
	Hostname string
	// ControlURL is empty for Tailscale's coordination server; tests and the test bench give theirs.
	ControlURL string
	// AuthKey logs the node in without a browser: tests only, never shipped in an app.
	AuthKey string
	// StateKey encrypts the node's state (its private keys) at rest: 32 bytes the app keeps in the keychain (Mac, iPhone)
	// or protects with DPAPI (Windows).
	StateKey []byte
	// Logf receives the node's diagnostics, login pages hidden, for a local log; nil drops them.
	Logf func(format string, args ...any)
}

// Node is a running node. Its methods are safe for concurrent use.
type Node struct {
	cfg    Config
	srv    *tsnet.Server
	lc     *local.Client
	lock   *dirLock
	ctx    context.Context
	cancel context.CancelFunc
	done   chan struct{} // the watch loop ended

	// dialTCP connects through the node's own network stack only: never the system's.
	dialTCP func(context.Context, netip.AddrPort) (net.Conn, error)

	mu      sync.Mutex
	status  Status
	health  map[string]Warning
	subs    map[int]func(Status)
	nextSub int
	authURL []chan string // Login calls waiting for a login page
	closed  bool
}

// Start starts a node and returns once it runs, logged in or not: it keeps connecting in the background, and Status,
// Watch and Login tell where it is. A node closed by Close can be started again from its saved state.
func Start(cfg Config) (*Node, error) {
	if cfg.Dir == "" {
		return nil, errors.New("tailnet: no folder for the node's state")
	}
	if len(cfg.StateKey) != stateKeySize {
		return nil, fmt.Errorf("tailnet: the state key has %d bytes, not %d", len(cfg.StateKey), stateKeySize)
	}
	if err := os.MkdirAll(cfg.Dir, 0o700); err != nil {
		return nil, err
	}
	lock, err := lockDir(filepath.Join(cfg.Dir, lockFile))
	if err != nil {
		return nil, err
	}
	n := &Node{cfg: cfg, lock: lock, done: make(chan struct{}), health: map[string]Warning{}, subs: map[int]func(Status){}}
	store, err := openStore(filepath.Join(cfg.Dir, stateFile), cfg.StateKey, n.logf)
	if err != nil {
		lock.release()
		return nil, err
	}
	n.status = Status{State: StateStarting}
	n.srv = &tsnet.Server{
		Dir:        cfg.Dir,
		Store:      store,
		Hostname:   Hostname(cfg.Hostname),
		ControlURL: cfg.ControlURL,
		AuthKey:    cfg.AuthKey,
		Logf:       n.logf,
		UserLogf:   n.logf,
	}
	fail := func(err error) (*Node, error) {
		n.srv.Close()
		lock.release()
		return nil, err
	}
	if err := n.srv.Start(); err != nil {
		return fail(err)
	}
	if n.lc, err = n.srv.LocalClient(); err != nil {
		return fail(err)
	}
	dialer, ok := n.srv.Sys().Dialer.GetOK()
	if !ok || dialer.NetstackDialTCP == nil {
		return fail(errors.New("tailnet: the node has no network stack of its own"))
	}
	n.dialTCP = dialer.NetstackDialTCP
	n.ctx, n.cancel = context.WithCancel(context.Background())
	go n.watch()
	return n, nil
}

// Close stops the node, keeping its state for the next Start. It waits for the node to let go of its files.
func (n *Node) Close() error {
	n.mu.Lock()
	if n.closed {
		n.mu.Unlock()
		return nil
	}
	n.closed = true
	n.mu.Unlock()
	n.cancel()
	err := n.srv.Close()
	<-n.done
	n.lock.release()
	n.mu.Lock()
	n.status = Status{State: StateStopped}
	subs := n.subscribers()
	n.mu.Unlock()
	for _, f := range subs {
		f(Status{State: StateStopped})
	}
	return err
}

// Login returns the page that logs this node in: the one already offered, or a new one. The node joins the tailnet as
// soon as the person approves it there; Status then turns StateRunning (or StateMachineAuth when the tailnet's admin
// must approve new devices).
func (n *Node) Login(ctx context.Context) (string, error) {
	n.mu.Lock()
	if n.closed {
		n.mu.Unlock()
		return "", newError(CodeStopped, nil)
	}
	if n.status.State == StateNeedsLogin && n.status.AuthURL != "" {
		url := n.status.AuthURL
		n.mu.Unlock()
		return url, nil
	}
	ch := make(chan string, 1)
	n.authURL = append(n.authURL, ch)
	n.mu.Unlock()
	defer func() {
		n.mu.Lock()
		for i, c := range n.authURL {
			if c == ch {
				n.authURL = append(n.authURL[:i], n.authURL[i+1:]...)
				break
			}
		}
		n.mu.Unlock()
	}()
	if err := n.lc.StartLoginInteractive(ctx); err != nil {
		return "", err
	}
	select {
	case url := <-ch:
		return url, nil
	case <-ctx.Done():
		return "", ctx.Err()
	}
}

// Logout signs this node out of its tailnet: the device turns « logged out » in the admin console, and the node waits for
// a new Login.
func (n *Node) Logout(ctx context.Context) error {
	return n.lc.Logout(ctx)
}

// Rebind makes the node take a fresh look at the network after the computer woke up or changed networks: new sockets, its
// public address found again, new paths to the other devices. The node also does it by itself when it notices a change.
func (n *Node) Rebind() {
	sys := n.srv.Sys()
	if mon, ok := sys.NetMon.GetOK(); ok {
		mon.InjectEvent()
	}
	if conn, ok := sys.MagicSock.GetOK(); ok {
		conn.Rebind()
		conn.ReSTUN("mira-rebind")
	}
}

// Hostname turns a device name (« Mira-MacBook Air de Élodie ») into the DNS label Tailscale shows
// (« mira-macbook-air-de-elodie »).
func Hostname(name string) string {
	var b strings.Builder
	dash := false
	for _, r := range norm.NFD.String(strings.ToLower(name)) {
		if unicode.Is(unicode.Mn, r) {
			continue // the accent of a letter
		}
		switch {
		case r >= 'a' && r <= 'z', r >= '0' && r <= '9':
			b.WriteRune(r)
			dash = false
		case b.Len() > 0 && !dash:
			b.WriteByte('-')
			dash = true
		}
		if b.Len() >= 63 {
			break
		}
	}
	label := strings.Trim(b.String(), "-")
	if len(label) > 63 {
		label = strings.Trim(label[:63], "-")
	}
	if label == "" {
		return "mira"
	}
	return label
}

// watch follows the node's state, its devices and its health, and keeps Status current.
func (n *Node) watch() {
	defer close(n.done)
	refresh := make(chan struct{}, 1)
	go func() {
		// One status read for a burst of changes.
		for {
			select {
			case <-n.ctx.Done():
				return
			case <-refresh:
			}
			n.refresh()
			select {
			case <-n.ctx.Done():
				return
			case <-time.After(250 * time.Millisecond):
			}
		}
	}()
	poke := func() {
		select {
		case refresh <- struct{}{}:
		default:
		}
	}
	for n.ctx.Err() == nil {
		w, err := n.lc.WatchIPNBus(n.ctx, ipn.NotifyInitialState|ipn.NotifyInitialHealthState|ipn.NotifyPeerChanges)
		if err != nil {
			if n.ctx.Err() == nil {
				n.logf("tailnet: watching the node: %v", err)
				time.Sleep(time.Second)
			}
			continue
		}
		for {
			nf, err := w.Next()
			if err != nil {
				break
			}
			if nf.BrowseToURL != nil && *nf.BrowseToURL != "" {
				n.offerLogin(*nf.BrowseToURL)
			}
			if nf.Health != nil {
				n.setHealth(nf.Health.Warnings)
			}
			poke()
		}
		w.Close()
	}
}

func (n *Node) offerLogin(url string) {
	n.mu.Lock()
	waiting := n.authURL
	n.authURL = nil
	n.mu.Unlock()
	for _, ch := range waiting {
		select {
		case ch <- url:
		default:
		}
	}
}

func (n *Node) logf(format string, args ...any) {
	if n.cfg.Logf == nil {
		return
	}
	line := fmt.Sprintf(format, args...)
	// The busiest of the backend's diagnostics: one line per packet path, of no use in a person's log.
	if strings.HasPrefix(line, "[v2] ") {
		return
	}
	n.cfg.Logf("%s", Redact(line))
}
