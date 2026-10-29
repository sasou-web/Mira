package main

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"sync"
	"time"

	"miracinema.app/tailscale/tailnet"
)

// config is what the app starts Built-in Tailscale with (TailscaleLibrary.Config in the app).
type config struct {
	// Dir holds the node's state (encrypted) and its log, on this iPhone only: never in a backup.
	Dir string `json:"dir"`
	// Hostname is the iPhone's name on the tailnet (« mira-iphone »).
	Hostname string `json:"hostname"`
	// StateKey encrypts the node's state: 32 bytes in base64url without padding, kept in the Keychain.
	StateKey string `json:"stateKey"`
	// RelayPort is the relay's port on 127.0.0.1 (another one when it is taken), RelaySecret the first segment of its
	// paths (the Keychain's too).
	RelayPort   int    `json:"relayPort"`
	RelaySecret string `json:"relaySecret"`
	// ControlURL and AuthKey belong to the tests' Tailscale network, never to the app people install.
	ControlURL string `json:"controlURL,omitempty"`
	AuthKey    string `json:"authKey,omitempty"`
}

// report is the node's status as the app reads it: tailnet.Status (« off » while no node runs), the relay's base, and
// why the node could not start.
type report struct {
	tailnet.Status
	Relay string   `json:"relay,omitempty"`
	Error *failure `json:"error,omitempty"`
}

// answer is what a sign-in or a sign-out says: the page to open, or why it failed.
type answer struct {
	URL   string   `json:"url,omitempty"`
	Error *failure `json:"error,omitempty"`
}

// failure is a reason in the codes the app explains: tailnet's (needs-login, offline…) or the library's own (config,
// relay, start).
type failure struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

// stateOff is the state while no node runs: never started, or stopped by the app when it left the screen.
const stateOff = "off"

const (
	loginTimeout  = 30 * time.Second
	logoutTimeout = 15 * time.Second
)

// bridge is Built-in Tailscale in the app: one node and its relay at a time, started and stopped by the app.
type bridge struct {
	mu    sync.Mutex
	cfg   config
	node  *tailnet.Node
	relay *tailnet.Relay
	log   *fileLog
}

// shared is the bridge the C functions use.
var shared bridge

// main never runs: the app links the library and calls its functions (main.go).
func main() {}

// start starts the node and its relay; started already with this configuration, it only makes sure the relay listens.
func (b *bridge) start(text string) report {
	var cfg config
	if err := json.Unmarshal([]byte(text), &cfg); err != nil {
		return off("config", err)
	}
	key, err := base64.RawURLEncoding.DecodeString(cfg.StateKey)
	if err != nil || len(key) != 32 {
		return off("config", errors.New("the state key is not 32 bytes in base64url"))
	}
	if cfg.Dir == "" {
		return off("config", errors.New("no folder for the node"))
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.node != nil {
		if b.cfg == cfg {
			if _, err := b.relay.Check(); err != nil {
				r := b.reportLocked()
				r.Error = &failure{Code: "relay", Message: err.Error()}
				return r
			}
			return b.reportLocked()
		}
		b.closeLocked()
	}
	if b.log == nil || b.log.dir != cfg.Dir {
		b.log.close()
		b.log = openLog(cfg.Dir)
	}
	node, err := tailnet.Start(tailnet.Config{
		Dir:        cfg.Dir,
		Hostname:   cfg.Hostname,
		ControlURL: cfg.ControlURL,
		AuthKey:    cfg.AuthKey,
		StateKey:   key,
		Logf:       b.log.logf,
	})
	if err != nil {
		code := tailnet.CodeOf(err)
		if code == "" {
			code = "start"
		}
		return off(code, err)
	}
	relay := &tailnet.Relay{Dial: node.Dial, Secret: cfg.RelaySecret, Logf: b.log.logf}
	if _, err := relay.Start(cfg.RelayPort); err != nil {
		node.Close()
		return off("relay", err)
	}
	b.cfg, b.node, b.relay = cfg, node, relay
	return b.reportLocked()
}

func (b *bridge) status() report {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.reportLocked()
}

// check is Mira back on screen, or on another network: the node takes a fresh look at the network, and the relay
// listens again if iOS took its socket.
func (b *bridge) check() report {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.node == nil {
		return b.reportLocked()
	}
	b.node.Rebind()
	if _, err := b.relay.Check(); err != nil {
		r := b.reportLocked()
		r.Error = &failure{Code: "relay", Message: err.Error()}
		return r
	}
	return b.reportLocked()
}

// network is the interface iOS now reaches the Internet with (NWPathMonitor, « en0 », « pdp_ip0 »): the node's own
// connections go out there, as Tailscale's app does it.
func (b *bridge) network(name string) {
	setDefaultInterface(name)
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.node != nil {
		b.node.Rebind()
	}
}

// login gives the page that signs the node in, once it is started.
func (b *bridge) login() answer {
	node := b.current()
	if node == nil {
		return answer{Error: &failure{Code: tailnet.CodeStopped, Message: "Built-in Tailscale is not started"}}
	}
	ctx, cancel := context.WithTimeout(context.Background(), loginTimeout)
	defer cancel()
	url, err := node.Login(ctx)
	if err != nil {
		return answer{Error: reason(err)}
	}
	return answer{URL: url}
}

// logout signs the node out of its tailnet: it waits for the next sign-in.
func (b *bridge) logout() answer {
	node := b.current()
	if node == nil {
		return answer{Error: &failure{Code: tailnet.CodeStopped, Message: "Built-in Tailscale is not started"}}
	}
	ctx, cancel := context.WithTimeout(context.Background(), logoutTimeout)
	defer cancel()
	if err := node.Logout(ctx); err != nil {
		return answer{Error: reason(err)}
	}
	return answer{}
}

// stop stops the relay and the node, keeping the node's state for the next start.
func (b *bridge) stop() report {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.closeLocked()
	return b.reportLocked()
}

func (b *bridge) current() *tailnet.Node {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.node
}

func (b *bridge) closeLocked() {
	if b.relay != nil {
		b.relay.Close()
	}
	if b.node != nil {
		if err := b.node.Close(); err != nil {
			b.log.logf("mira-tailscale: stopping the node: %v", err)
		}
	}
	b.node, b.relay, b.cfg = nil, nil, config{}
}

func (b *bridge) reportLocked() report {
	if b.node == nil {
		return report{Status: tailnet.Status{State: stateOff}}
	}
	return report{Status: b.node.Status(), Relay: b.relay.Base()}
}

func off(code string, err error) report {
	return report{Status: tailnet.Status{State: stateOff}, Error: &failure{Code: code, Message: tailnet.Redact(err.Error())}}
}

func reason(err error) *failure {
	code := tailnet.CodeOf(err)
	switch {
	case code != "":
	case errors.Is(err, context.DeadlineExceeded):
		code = "timeout"
	default:
		code = "tailscale"
	}
	return &failure{Code: code, Message: tailnet.Redact(err.Error())}
}

// encode is a value in JSON, for the app.
func encode(v any) string {
	data, err := json.Marshal(v)
	if err != nil {
		return fmt.Sprintf(`{"state":%q,"error":{"code":"library","message":%q}}`, stateOff, err.Error())
	}
	return string(data)
}
