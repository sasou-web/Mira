package tailnet

import (
	"context"
	"errors"
	"fmt"
	"net"
	"net/netip"
	"strconv"
	"strings"
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
		return conn, nil
	}
	return nil, n.explain(err, dev)
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
