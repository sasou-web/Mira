package tailnet

import (
	"context"
	"errors"
	"fmt"
	"net"
	"net/netip"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// Why a connection over the tailnet failed: Error.Code, the same in the three apps, which explain each in their words.
const (
	// CodeNeedsLogin: the node isn't logged in to a tailnet.
	CodeNeedsLogin = "needs-login"
	// CodeExpired: the node's key expired; it must log in again.
	CodeExpired = "expired"
	// CodeMachineAuth: the tailnet's admin hasn't approved this device yet.
	CodeMachineAuth = "machine-auth"
	// CodeStarting: the node was still connecting to Tailscale when the time was up.
	CodeStarting = "starting"
	// CodeStopped: the node was closed.
	CodeStopped = "stopped"
	// CodeOffline: this device can't reach the tailnet (no network, or no Tailscale relay).
	CodeOffline = "offline"
	// CodeRefused: the address isn't a device of the tailnet; the node never sends anything elsewhere.
	CodeRefused = "refused"
	// CodePeerOffline: the device is in the tailnet but not connected to Tailscale (off, asleep, Tailscale stopped).
	CodePeerOffline = "peer-offline"
	// CodeUnreachable: the device didn't answer in time.
	CodeUnreachable = "unreachable"
	// CodeClosed: the device answered that nothing listens on that port (Jellyfin stopped, or another port).
	CodeClosed = "closed"
	// CodeLocked: another Mira already runs a node from the same folder.
	CodeLocked = "locked"
	// CodeCertificate: the relay reached an HTTPS server whose certificate it refused.
	CodeCertificate = "certificate"
	// CodeTimeout: the relay's request took too long.
	CodeTimeout = "timeout"
	// CodeProtocol: the helper didn't understand a request.
	CodeProtocol = "protocol"
)

// Error is a failure of the node, with the code the apps explain.
type Error struct {
	code string
	err  error
}

func newError(code string, err error) *Error { return &Error{code: code, err: err} }

// Code is one of the Code constants.
func (e *Error) Code() string { return e.code }

func (e *Error) Error() string {
	if e.err == nil {
		return "tailnet: " + e.code
	}
	return fmt.Sprintf("tailnet: %s: %v", e.code, e.err)
}

func (e *Error) Unwrap() error { return e.err }

// CodeOf is the code of err, or "" when err isn't the node's.
func CodeOf(err error) string {
	var coded interface{ Code() string }
	if errors.As(err, &coded) {
		return coded.Code()
	}
	return ""
}

// peerOfflineWait is how long a dial to a device Tailscale says is offline waits for it anyway (a device can reach us
// directly while its link to Tailscale is down).
const peerOfflineWait = 8 * time.Second

// Dial connects to a device of the tailnet, by Tailscale IP, MagicDNS name or short MagicDNS name, over TCP. It waits for
// the node to be running until ctx ends, and never connects anywhere else: an address outside the tailnet is
// CodeRefused. Its errors are *Error.
func (n *Node) Dial(ctx context.Context, network, address string) (net.Conn, error) {
	switch network {
	case "tcp", "tcp4", "tcp6":
	default:
		return nil, newError(CodeRefused, fmt.Errorf("network %q", network))
	}
	host, portText, err := net.SplitHostPort(address)
	if err != nil {
		return nil, newError(CodeRefused, err)
	}
	port, err := strconv.ParseUint(portText, 10, 16)
	if err != nil || port == 0 {
		return nil, newError(CodeRefused, fmt.Errorf("port %q", portText))
	}
	st, err := n.awaitRunning(ctx)
	if err != nil {
		return nil, err
	}
	ip, dev, ok := resolve(host, st, network)
	if !ok {
		// The status may be a quarter of a second behind: read it again before refusing.
		n.refresh()
		if ip, dev, ok = resolve(host, n.Status(), network); !ok {
			return nil, newError(CodeRefused, fmt.Errorf("%s is not a device of the tailnet", host))
		}
	}
	dialCtx := ctx
	if !dev.Online {
		var cancel context.CancelFunc
		dialCtx, cancel = context.WithTimeout(ctx, peerOfflineWait)
		defer cancel()
	}
	conn, err := n.dialTCP(dialCtx, netip.AddrPortFrom(ip, uint16(port)))
	if err == nil {
		return n.keep(conn, dev.ID), nil
	}
	return nil, n.explain(err, dev)
}

// deviceConn is a connection Dial made to a device of the tailnet. A device switched off or asleep never answers on it
// again: a request waiting there would wait until whoever made it gave up, and one kept idle in a pool (the relay's, the
// proxy's) would hang the next request when the device is back. So the node closes it as soon as the device goes
// offline or leaves the tailnet; reading or writing it then fails with CodePeerOffline, and a new Dial says why.
type deviceConn struct {
	net.Conn
	node    *Node
	device  string // the device's ID
	once    sync.Once
	dropped atomic.Bool
}

func (n *Node) keep(conn net.Conn, device string) net.Conn {
	c := &deviceConn{Conn: conn, node: n, device: device}
	n.mu.Lock()
	if n.conns == nil {
		n.conns = map[*deviceConn]struct{}{}
	}
	n.conns[c] = struct{}{}
	n.mu.Unlock()
	return c
}

// dropGone closes the connections to the devices online in prev and no longer in next: offline, or gone from the
// tailnet. A connection made while its device was already offline (a direct path, which Dial tries) is left to itself.
func (n *Node) dropGone(prev, next Status) {
	online := map[string]bool{}
	for _, d := range next.Peers {
		if d.Online {
			online[d.ID] = true
		}
	}
	gone := map[string]bool{}
	for _, d := range prev.Peers {
		if d.Online && !online[d.ID] {
			gone[d.ID] = true
		}
	}
	if len(gone) == 0 {
		return
	}
	n.mu.Lock()
	var doomed []*deviceConn
	for c := range n.conns {
		if gone[c.device] {
			doomed = append(doomed, c)
		}
	}
	n.mu.Unlock()
	for _, c := range doomed {
		c.dropped.Store(true)
		c.Close()
	}
}

func (c *deviceConn) Read(p []byte) (int, error) {
	n, err := c.Conn.Read(p)
	if err != nil && c.dropped.Load() {
		err = newError(CodePeerOffline, err)
	}
	return n, err
}

func (c *deviceConn) Write(p []byte) (int, error) {
	n, err := c.Conn.Write(p)
	if err != nil && c.dropped.Load() {
		err = newError(CodePeerOffline, err)
	}
	return n, err
}

// CloseWrite ends the sending half, as the connection under it does (a tunnel of the proxy ends each way on its own).
func (c *deviceConn) CloseWrite() error {
	if cw, ok := c.Conn.(interface{ CloseWrite() error }); ok {
		return cw.CloseWrite()
	}
	return nil
}

func (c *deviceConn) Close() error {
	c.once.Do(func() {
		c.node.mu.Lock()
		delete(c.node.conns, c)
		c.node.mu.Unlock()
	})
	return c.Conn.Close()
}

// Probe tells whether address answers over the tailnet, and if not, why (an *Error): nothing goes to it but a TCP
// connection, closed at once.
func (n *Node) Probe(ctx context.Context, address string) error {
	conn, err := n.Dial(ctx, "tcp", address)
	if err != nil {
		return err
	}
	return conn.Close()
}

// awaitRunning waits for the node to run, or says why it can't.
func (n *Node) awaitRunning(ctx context.Context) (Status, error) {
	for {
		n.mu.Lock()
		st, closed := n.status, n.closed
		n.mu.Unlock()
		if closed {
			return st, newError(CodeStopped, nil)
		}
		switch st.State {
		case StateRunning:
			// Even offline: a direct path to the device may still work, and the warning may be late.
			return st, nil
		case StateNeedsLogin:
			return st, newError(CodeNeedsLogin, nil)
		case StateExpired:
			return st, newError(CodeExpired, nil)
		case StateMachineAuth:
			return st, newError(CodeMachineAuth, nil)
		case StateStopped:
			return st, newError(CodeStopped, nil)
		}
		changed := make(chan struct{}, 1)
		stop := n.Watch(func(s Status) {
			if s.State != StateStarting {
				select {
				case changed <- struct{}{}:
				default:
				}
			}
		})
		select {
		case <-changed:
			stop()
		case <-ctx.Done():
			stop()
			return st, newError(CodeStarting, ctx.Err())
		}
	}
}

// explain gives a failed dial its code.
func (n *Node) explain(err error, dev Device) error {
	switch {
	case strings.Contains(err.Error(), "refused"):
		return newError(CodeClosed, err)
	case n.Status().Offline:
		return newError(CodeOffline, err)
	case !dev.Online:
		return newError(CodePeerOffline, err)
	default:
		return newError(CodeUnreachable, err)
	}
}

// resolve finds the device host names: one of its Tailscale IPs, its MagicDNS name or the short one (its first label),
// in any case. Names give IPv4 unless the network asks for IPv6. The node's own addresses don't count: nothing of
// Mira's listens there.
func resolve(host string, st Status, network string) (netip.Addr, Device, bool) {
	host = strings.ToLower(strings.TrimSuffix(strings.Trim(host, "[]"), "."))
	if host == "" {
		return netip.Addr{}, Device{}, false
	}
	if ip, err := netip.ParseAddr(host); err == nil {
		ip = ip.Unmap().WithZone("")
		for _, d := range st.Peers {
			for _, text := range d.IPs {
				if a, err := netip.ParseAddr(text); err == nil && a == ip {
					return ip, d, true
				}
			}
		}
		return netip.Addr{}, Device{}, false
	}
	for _, d := range st.Peers {
		name := strings.ToLower(d.DNSName)
		short, _, _ := strings.Cut(name, ".")
		if name == "" || (host != name && host != short) {
			continue
		}
		var four, six netip.Addr
		for _, text := range d.IPs {
			a, err := netip.ParseAddr(text)
			switch {
			case err != nil:
			case a.Is4() && !four.IsValid():
				four = a
			case a.Is6() && !six.IsValid():
				six = a
			}
		}
		if network == "tcp6" || !four.IsValid() {
			if six.IsValid() && network != "tcp4" {
				return six, d, true
			}
		}
		if four.IsValid() && network != "tcp6" {
			return four, d, true
		}
		return netip.Addr{}, Device{}, false
	}
	return netip.Addr{}, Device{}, false
}
