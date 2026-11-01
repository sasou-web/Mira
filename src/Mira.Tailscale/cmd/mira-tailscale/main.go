// mira-tailscale is the Tailscale node of Mira for Windows and Mac: a helper process the app starts, which speaks JSON
// lines on its standard input and output, and logs on its standard error (login pages hidden).
//
// Requests ({"id":1,"op":"start",...}) are answered by a line with the same id ({"id":1,"ok":true} or {"id":1,"ok":false,
// "code":"…","error":"…"}); events have no id ({"event":"status","status":{…}}). The first line is the hello event, which
// tells the protocol, this helper's version (Mira's), Tailscale's, its capability version (149 and later: the cache of
// the network map comes without a line in the tailnet's policy, tools/tailscale/backport.mjs) and Go's, and whether
// logs could ever be uploaded
// (never: the app refuses a helper that says so). The helper ends when its input does.
//
//	start   dir, hostname, controlURL (tests), authKey (tests), stateKey (32 bytes, base64), proxyUser, proxyPassword:
//	        starts the node and its loopback proxy; then the proxy event (its address) and status events.
//	status  answers with the status.
//	login   answers with url, the page that logs the node in.
//	logout  signs the node out of its tailnet.
//	probe   address, timeout (seconds, 10 by default): ok, or the code of the failure.
//	rebind  takes a fresh look at the network (after sleep).
//	stop    stops the node, keeping its state; start can follow.
//	quit    stops the node and ends.
package main

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"os"
	"os/signal"
	"runtime"
	"runtime/debug"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"

	"miracinema.app/tailscale/tailnet"
	"tailscale.com/feature/buildfeatures"
	"tailscale.com/tailcfg"
)

// version is Mira's, set at build time (-X main.version=x.y.z): the app runs a helper of its own version only.
var version = "dev"

// protocol changes when a request, an answer or an event changes meaning.
const protocol = 1

type request struct {
	ID            int64   `json:"id"`
	Op            string  `json:"op"`
	Dir           string  `json:"dir,omitempty"`
	Hostname      string  `json:"hostname,omitempty"`
	ControlURL    string  `json:"controlURL,omitempty"`
	AuthKey       string  `json:"authKey,omitempty"`
	StateKey      []byte  `json:"stateKey,omitempty"`
	ProxyUser     string  `json:"proxyUser,omitempty"`
	ProxyPassword string  `json:"proxyPassword,omitempty"`
	Address       string  `json:"address,omitempty"`
	Timeout       float64 `json:"timeout,omitempty"`
}

type message struct {
	Event     string          `json:"event,omitempty"`
	ID        int64           `json:"id,omitempty"`
	OK        *bool           `json:"ok,omitempty"`
	Code      string          `json:"code,omitempty"`
	Error     string          `json:"error,omitempty"`
	URL       string          `json:"url,omitempty"`
	Address   string          `json:"address,omitempty"`
	Status    *tailnet.Status `json:"status,omitempty"`
	Protocol  int             `json:"protocol,omitempty"`
	Version   string          `json:"version,omitempty"`
	Tailscale string          `json:"tailscale,omitempty"`
	CapVer    int             `json:"capabilityVersion,omitempty"`
	Go        string          `json:"go,omitempty"`
	LogUpload *bool           `json:"logUpload,omitempty"`
}

func main() {
	logs := newLogger(os.Stderr)
	// What Tailscale writes with the standard logger (the system's proxy, for one) goes the same way, redacted.
	log.SetFlags(0)
	log.SetOutput(logs)
	out := newOutput(os.Stdout)
	h := &helper{out: out, logf: logs.logf}
	uploads := buildfeatures.HasLogTail
	out.send(message{Event: "hello", Protocol: protocol, Version: version, Tailscale: tailscaleVersion(), CapVer: int(tailcfg.CurrentCapabilityVersion), Go: runtime.Version(), LogUpload: &uploads})

	signals := make(chan os.Signal, 1)
	signal.Notify(signals, os.Interrupt, syscall.SIGTERM)
	go func() {
		<-signals
		h.quit(0)
	}()

	in := bufio.NewScanner(os.Stdin)
	in.Buffer(make([]byte, 64*1024), 1024*1024)
	for in.Scan() {
		line := strings.TrimSpace(in.Text())
		if line == "" {
			continue
		}
		var req request
		if err := json.Unmarshal([]byte(line), &req); err != nil {
			out.send(message{Event: "error", Code: tailnet.CodeProtocol, Error: "not a request: " + err.Error()})
			continue
		}
		if req.Op == "quit" {
			out.send(answer(req.ID, nil))
			h.quit(0)
		}
		h.inflight.Add(1)
		go func() {
			defer h.inflight.Done()
			h.handle(req)
		}()
	}
	// The app is gone (or closed our input): nothing to serve any more.
	h.quit(0)
}

type helper struct {
	out  *output
	logf func(string, ...any)

	mu       sync.Mutex
	node     *tailnet.Node
	proxy    *tailnet.Proxy
	stop     func()
	quits    atomic.Bool
	inflight sync.WaitGroup // requests being handled: answered before the helper ends
}

func (h *helper) handle(req request) {
	var reply message
	switch req.Op {
	case "start":
		reply = answer(req.ID, h.start(req))
	case "status":
		node, err := h.current()
		reply = answer(req.ID, err)
		if node != nil {
			st := node.Status()
			reply.Status = &st
		}
	case "login":
		node, err := h.current()
		if err == nil {
			ctx, cancel := context.WithTimeout(context.Background(), time.Minute)
			var url string
			url, err = node.Login(ctx)
			cancel()
			reply = answer(req.ID, err)
			reply.URL = url
		} else {
			reply = answer(req.ID, err)
		}
	case "logout":
		node, err := h.current()
		if err == nil {
			ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
			err = node.Logout(ctx)
			cancel()
		}
		reply = answer(req.ID, err)
	case "probe":
		node, err := h.current()
		if err == nil {
			timeout := 10 * time.Second
			if req.Timeout > 0 {
				timeout = time.Duration(req.Timeout * float64(time.Second))
			}
			ctx, cancel := context.WithTimeout(context.Background(), timeout)
			err = node.Probe(ctx, req.Address)
			cancel()
		}
		reply = answer(req.ID, err)
	case "rebind":
		node, err := h.current()
		if err == nil {
			node.Rebind()
		}
		reply = answer(req.ID, err)
	case "stop":
		h.close()
		reply = answer(req.ID, nil)
	default:
		reply = answer(req.ID, fmt.Errorf("unknown op %q", req.Op))
		reply.Code = tailnet.CodeProtocol
	}
	h.out.send(reply)
}

func (h *helper) start(req request) error {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.node != nil {
		return errors.New("the node is already started")
	}
	node, err := tailnet.Start(tailnet.Config{
		Dir:        req.Dir,
		Hostname:   req.Hostname,
		ControlURL: req.ControlURL,
		AuthKey:    req.AuthKey,
		StateKey:   req.StateKey,
		Logf:       h.logf,
	})
	if err != nil {
		return err
	}
	proxy, err := node.ServeProxy("127.0.0.1:0", req.ProxyUser, req.ProxyPassword)
	if err != nil {
		node.Close()
		return err
	}
	h.node, h.proxy = node, proxy
	h.out.send(message{Event: "proxy", Address: proxy.Addr()})
	h.stop = node.Watch(func(st tailnet.Status) { h.out.sendStatus(st) })
	return nil
}

func (h *helper) current() (*tailnet.Node, error) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.node == nil {
		return nil, errors.New("tailnet: stopped")
	}
	return h.node, nil
}

func (h *helper) close() {
	h.mu.Lock()
	node, proxy, stop := h.node, h.proxy, h.stop
	h.node, h.proxy, h.stop = nil, nil, nil
	h.mu.Unlock()
	if proxy != nil {
		proxy.Close()
	}
	if stop != nil {
		stop()
	}
	if node != nil {
		node.Close()
		h.out.sendStatus(tailnet.Status{State: tailnet.StateStopped})
	}
}

func (h *helper) quit(code int) {
	if !h.quits.CompareAndSwap(false, true) {
		select {}
	}
	answered := make(chan struct{})
	go func() {
		h.inflight.Wait()
		close(answered)
	}()
	select {
	case <-answered:
	case <-time.After(5 * time.Second):
	}
	done := make(chan struct{})
	go func() {
		h.close()
		close(done)
	}()
	select {
	case <-done:
	case <-time.After(10 * time.Second):
		h.logf("mira-tailscale: the node took too long to stop")
	}
	h.out.flush()
	os.Exit(code)
}

func answer(id int64, err error) message {
	ok := err == nil
	m := message{ID: id, OK: &ok}
	if err != nil {
		m.Code = tailnet.CodeOf(err)
		m.Error = tailnet.Redact(err.Error())
	}
	return m
}

func tailscaleVersion() string {
	if info, ok := debug.ReadBuildInfo(); ok {
		for _, dep := range info.Deps {
			if dep.Path == "tailscale.com" {
				return strings.TrimPrefix(dep.Version, "v")
			}
		}
	}
	return ""
}

// output writes the answers and events in order, one JSON line each; a status not yet written gives way to a newer one,
// so a slow reader never holds the node up.
type output struct {
	mu      sync.Mutex
	queue   []message
	status  *tailnet.Status
	wake    chan struct{}
	w       *bufio.Writer
	idle    *sync.Cond
	writing bool
}

func newOutput(w io.Writer) *output {
	o := &output{wake: make(chan struct{}, 1), w: bufio.NewWriter(w)}
	o.idle = sync.NewCond(&o.mu)
	go o.run()
	return o
}

func (o *output) send(m message) {
	o.mu.Lock()
	o.queue = append(o.queue, m)
	o.mu.Unlock()
	o.poke()
}

func (o *output) sendStatus(st tailnet.Status) {
	o.mu.Lock()
	o.status = &st
	o.mu.Unlock()
	o.poke()
}

func (o *output) poke() {
	select {
	case o.wake <- struct{}{}:
	default:
	}
}

func (o *output) run() {
	enc := json.NewEncoder(o.w)
	for range o.wake {
		for {
			o.mu.Lock()
			var batch []message
			batch, o.queue = o.queue, nil
			if o.status != nil {
				batch = append(batch, message{Event: "status", Status: o.status})
				o.status = nil
			}
			if len(batch) == 0 {
				o.writing = false
				o.idle.Broadcast()
				o.mu.Unlock()
				break
			}
			o.writing = true
			o.mu.Unlock()
			for _, m := range batch {
				enc.Encode(m)
			}
			o.w.Flush()
		}
	}
}

// flush waits, a little, for what is queued to be written.
func (o *output) flush() {
	deadline := time.Now().Add(2 * time.Second)
	o.poke()
	for time.Now().Before(deadline) {
		o.mu.Lock()
		done := len(o.queue) == 0 && o.status == nil && !o.writing
		o.mu.Unlock()
		if done {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
}

// logger writes the node's diagnostics to standard error without ever blocking the node: when the app reads too slowly,
// lines are dropped and counted.
type logger struct {
	lines   chan string
	dropped atomic.Int64
}

func newLogger(w io.Writer) *logger {
	l := &logger{lines: make(chan string, 4096)}
	go func() {
		bw := bufio.NewWriter(w)
		for line := range l.lines {
			if n := l.dropped.Swap(0); n > 0 {
				fmt.Fprintf(bw, "%s mira-tailscale: %d lines dropped\n", time.Now().UTC().Format(time.RFC3339), n)
			}
			bw.WriteString(line)
			if len(l.lines) == 0 {
				bw.Flush()
			}
		}
	}()
	return l
}

// Write takes the standard logger's lines.
func (l *logger) Write(p []byte) (int, error) {
	l.logf("%s", p)
	return len(p), nil
}

func (l *logger) logf(format string, args ...any) {
	line := time.Now().UTC().Format("2006-01-02T15:04:05.000Z ") + strings.TrimRight(tailnet.Redact(fmt.Sprintf(format, args...)), "\n") + "\n"
	select {
	case l.lines <- line:
	default:
		l.dropped.Add(1)
	}
}
