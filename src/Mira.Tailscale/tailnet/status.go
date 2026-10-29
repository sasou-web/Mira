package tailnet

import (
	"context"
	"encoding/json"
	"maps"
	"net/netip"
	"slices"
	"strings"
	"time"

	"tailscale.com/health"
	"tailscale.com/ipn"
	"tailscale.com/ipn/ipnstate"
)

// The node's states, as Status.State gives them.
const (
	// StateStarting: the node is connecting to Tailscale, or reconnecting after being stopped.
	StateStarting = "starting"
	// StateNeedsLogin: the node was never logged in, or was logged out: Login gives the page to log it in.
	StateNeedsLogin = "needs-login"
	// StateExpired: the node's key expired (180 days by default): it must log in again, Login gives the page.
	StateExpired = "expired"
	// StateMachineAuth: the tailnet's admin must approve this new device in the Tailscale admin console.
	StateMachineAuth = "machine-auth"
	// StateRunning: the node is on the tailnet.
	StateRunning = "running"
	// StateStopped: the node was closed.
	StateStopped = "stopped"
)

// Status is where the node is, in a shape the apps read as JSON.
type Status struct {
	State string `json:"state"`
	// AuthURL is the page that logs this node in, while it needs one.
	AuthURL string `json:"authURL,omitempty"`
	// Offline is whether, while running, this device can't reach the tailnet: no network, or no Tailscale relay.
	Offline bool    `json:"offline,omitempty"`
	Self    *Device `json:"self,omitempty"`
	// User is the account the node is logged in with (its login name).
	User string `json:"user,omitempty"`
	// Tailnet is the tailnet's name.
	Tailnet        string    `json:"tailnet,omitempty"`
	MagicDNSSuffix string    `json:"magicDNSSuffix,omitempty"`
	Peers          []Device  `json:"peers,omitempty"`
	Warnings       []Warning `json:"warnings,omitempty"`
}

// Device is one device of the tailnet.
type Device struct {
	ID       string `json:"id"`
	HostName string `json:"hostName"`
	// DNSName is its MagicDNS name, without the final dot (« desktop-abc.tail1234.ts.net »).
	DNSName string   `json:"dnsName,omitempty"`
	IPs     []string `json:"ips,omitempty"`
	OS      string   `json:"os,omitempty"`
	// Online is whether the device is connected to Tailscale.
	Online    bool       `json:"online"`
	KeyExpiry *time.Time `json:"keyExpiry,omitempty"`
	// LastSeen is when an offline device was last connected.
	LastSeen *time.Time `json:"lastSeen,omitempty"`
}

// Warning is one of the node's health problems.
type Warning struct {
	// Code is Tailscale's (« network-status », « no-derp-home », « login-state »…).
	Code string `json:"code"`
	// Text is Tailscale's explanation, in English.
	Text string `json:"text"`
	// Connectivity is whether the problem keeps the node from reaching the other devices.
	Connectivity bool `json:"connectivity,omitempty"`
}

// Health warnings that mean this device can't reach the tailnet at all.
var offlineWarnings = []string{"network-status", "no-derp-home", "no-derp-connection"}

// Status is where the node is now.
func (n *Node) Status() Status {
	n.mu.Lock()
	defer n.mu.Unlock()
	return n.status
}

// Watch calls f with the node's status now and after each change, until the returned function is called. f must not
// block: it runs on the node's watch loop.
func (n *Node) Watch(f func(Status)) (stop func()) {
	n.mu.Lock()
	id := n.nextSub
	n.nextSub++
	n.subs[id] = f
	now := n.status
	n.mu.Unlock()
	f(now)
	return func() {
		n.mu.Lock()
		delete(n.subs, id)
		n.mu.Unlock()
	}
}

func (n *Node) subscribers() []func(Status) {
	return slices.Collect(maps.Values(n.subs))
}

func (n *Node) setHealth(warnings map[health.WarnableCode]health.UnhealthyState) {
	now := map[string]Warning{}
	for code, w := range warnings {
		now[string(code)] = Warning{Code: string(code), Text: w.Text, Connectivity: w.ImpactsConnectivity}
	}
	n.mu.Lock()
	n.health = now
	n.mu.Unlock()
}

// refresh reads the node's status and tells the watchers when it changed.
func (n *Node) refresh() {
	ctx, cancel := context.WithTimeout(n.ctx, 10*time.Second)
	defer cancel()
	st, err := n.lc.Status(ctx)
	if err != nil {
		if n.ctx.Err() == nil {
			n.logf("tailnet: reading the node's status: %v", err)
		}
		return
	}
	n.mu.Lock()
	if n.closed {
		n.mu.Unlock()
		return
	}
	next := convertStatus(st, n.health, time.Now())
	if sameStatus(next, n.status) {
		n.mu.Unlock()
		return
	}
	n.status = next
	subs := n.subscribers()
	n.mu.Unlock()
	for _, f := range subs {
		f(next)
	}
}

func sameStatus(a, b Status) bool {
	ja, _ := json.Marshal(a)
	jb, _ := json.Marshal(b)
	return string(ja) == string(jb)
}

// convertStatus turns Tailscale's status into Mira's.
func convertStatus(st *ipnstate.Status, warnings map[string]Warning, now time.Time) Status {
	s := Status{MagicDNSSuffix: strings.TrimSuffix(st.MagicDNSSuffix, ".")}
	if st.Self != nil && st.Self.ID != "" {
		self := device(st.Self)
		s.Self = &self
		if p, ok := st.User[st.Self.UserID]; ok {
			s.User = p.LoginName
		}
	}
	if st.CurrentTailnet != nil {
		s.Tailnet = st.CurrentTailnet.Name
		if s.MagicDNSSuffix == "" {
			s.MagicDNSSuffix = strings.TrimSuffix(st.CurrentTailnet.MagicDNSSuffix, ".")
		}
	}
	switch st.BackendState {
	case ipn.Running.String():
		s.State = StateRunning
	case ipn.NeedsLogin.String():
		s.State = StateNeedsLogin
		// A node that had a key and lost it to time: the same login, said differently.
		if st.Self != nil && st.Self.KeyExpiry != nil && !st.Self.KeyExpiry.IsZero() && st.Self.KeyExpiry.Before(now) {
			s.State = StateExpired
		}
		s.AuthURL = st.AuthURL
	case ipn.NeedsMachineAuth.String():
		s.State = StateMachineAuth
	case ipn.Stopped.String():
		s.State = StateStopped
	default: // NoState, Starting
		s.State = StateStarting
	}
	for _, code := range slices.Sorted(maps.Keys(warnings)) {
		w := warnings[code]
		s.Warnings = append(s.Warnings, w)
		if s.State == StateRunning && slices.Contains(offlineWarnings, code) {
			s.Offline = true
		}
	}
	for _, p := range st.Peer {
		if p == nil || p.ID == "" {
			continue
		}
		s.Peers = append(s.Peers, device(p))
	}
	slices.SortFunc(s.Peers, func(a, b Device) int { return strings.Compare(a.DNSName+a.ID, b.DNSName+b.ID) })
	return s
}

func device(p *ipnstate.PeerStatus) Device {
	d := Device{
		ID:       string(p.ID),
		HostName: p.HostName,
		DNSName:  strings.TrimSuffix(p.DNSName, "."),
		OS:       p.OS,
		Online:   p.Online,
	}
	ips := slices.Clone(p.TailscaleIPs)
	// IPv4 first: the one people type and see.
	slices.SortStableFunc(ips, func(a, b netip.Addr) int {
		if a.Is4() == b.Is4() {
			return 0
		}
		if a.Is4() {
			return -1
		}
		return 1
	})
	for _, ip := range ips {
		d.IPs = append(d.IPs, ip.String())
	}
	if p.KeyExpiry != nil && !p.KeyExpiry.IsZero() {
		t := p.KeyExpiry.UTC()
		d.KeyExpiry = &t
	}
	if !p.Online && !p.LastSeen.IsZero() {
		t := p.LastSeen.UTC()
		d.LastSeen = &t
	}
	return d
}
