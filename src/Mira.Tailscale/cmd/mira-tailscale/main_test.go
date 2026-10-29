package main

import (
	"bufio"
	"bytes"
	"crypto/rand"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"miracinema.app/tailscale/tailnet"
	"miracinema.app/tailscale/tailnet/tailnettest"
)

// The test binary is the helper too, when the test runs it as a child process.
func TestMain(m *testing.M) {
	if os.Getenv("MIRA_TAILSCALE_HELPER") == "1" {
		main()
		return
	}
	os.Exit(m.Run())
}

// child is the helper, run as the app runs it.
type child struct {
	t       *testing.T
	cmd     *exec.Cmd
	in      io.WriteCloser
	lines   chan message
	pending []message // lines read but not yet asked for
	stderr  *bytes.Buffer
	mu      sync.Mutex
	nextID  int64
}

func startChild(t *testing.T) *child {
	t.Helper()
	cmd := exec.Command(os.Args[0])
	cmd.Env = append(os.Environ(), "MIRA_TAILSCALE_HELPER=1")
	in, err := cmd.StdinPipe()
	if err != nil {
		t.Fatal(err)
	}
	out, err := cmd.StdoutPipe()
	if err != nil {
		t.Fatal(err)
	}
	c := &child{t: t, cmd: cmd, in: in, lines: make(chan message, 1000), stderr: &bytes.Buffer{}}
	cmd.Stderr = lockedWriter{&c.mu, c.stderr}
	if err := cmd.Start(); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { cmd.Process.Kill(); cmd.Wait() })
	go func() {
		scan := bufio.NewScanner(out)
		scan.Buffer(make([]byte, 64*1024), 4*1024*1024)
		for scan.Scan() {
			var m message
			if err := json.Unmarshal(scan.Bytes(), &m); err != nil {
				t.Errorf("not JSON: %q", scan.Text())
				continue
			}
			c.lines <- m
		}
		close(c.lines)
	}()
	return c
}

type lockedWriter struct {
	mu *sync.Mutex
	w  io.Writer
}

func (l lockedWriter) Write(b []byte) (int, error) {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.w.Write(b)
}

func (c *child) send(req map[string]any) int64 {
	c.t.Helper()
	c.nextID++
	req["id"] = c.nextID
	data, _ := json.Marshal(req)
	if _, err := c.in.Write(append(data, '\n')); err != nil {
		c.t.Fatal(err)
	}
	return c.nextID
}

// next waits for the first line that ok accepts; the others stay for later.
func (c *child) next(what string, ok func(message) bool) message {
	c.t.Helper()
	for i, m := range c.pending {
		if ok(m) {
			c.pending = append(c.pending[:i], c.pending[i+1:]...)
			return m
		}
	}
	timeout := time.After(60 * time.Second)
	for {
		select {
		case m, open := <-c.lines:
			if !open {
				c.t.Fatalf("waiting for %s: the helper ended; its log:\n%s", what, c.log())
			}
			if ok(m) {
				return m
			}
			c.pending = append(c.pending, m)
		case <-timeout:
			c.t.Fatalf("waiting for %s: too long; its log:\n%s", what, c.log())
		}
	}
}

func (c *child) answer(id int64) message {
	c.t.Helper()
	return c.next(fmt.Sprintf("answer %d", id), func(m message) bool { return m.ID == id })
}

func (c *child) status(what string, ok func(tailnet.Status) bool) tailnet.Status {
	c.t.Helper()
	return *c.next(what, func(m message) bool { return m.Event == "status" && m.Status != nil && ok(*m.Status) }).Status
}

func (c *child) log() string {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.stderr.String()
}

func TestHelper(t *testing.T) {
	if testing.Short() {
		t.Skip("starts a Tailscale network")
	}
	jf := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		fmt.Fprint(w, `{"ServerName":"Jellyfin de test"}`)
	}))
	defer jf.Close()
	tn, err := tailnettest.Start(tailnettest.Options{Forward: map[uint16]string{8096: strings.TrimPrefix(jf.URL, "http://")}})
	if err != nil {
		t.Fatal(err)
	}
	defer tn.Close()

	c := startChild(t)
	hello := c.next("hello", func(m message) bool { return m.Event == "hello" })
	if hello.Protocol != protocol || hello.Version != "dev" || hello.Tailscale == "" || !strings.HasPrefix(hello.Go, "go") || hello.LogUpload == nil || *hello.LogUpload {
		t.Fatalf("hello: %+v", hello)
	}

	// Before start: said, not a crash.
	if m := c.answer(c.send(map[string]any{"op": "status"})); m.OK == nil || *m.OK {
		t.Fatalf("status before start: %+v", m)
	}
	if m := c.answer(c.send(map[string]any{"op": "nonsense"})); *m.OK || m.Code != "protocol" {
		t.Fatalf("unknown op: %+v", m)
	}
	if m := c.answer(c.send(map[string]any{"op": "start", "dir": t.TempDir(), "stateKey": []byte("short"), "proxyUser": "m", "proxyPassword": "p"})); *m.OK {
		t.Fatalf("start with a short key: %+v", m)
	}

	stateKey := make([]byte, 32)
	rand.Read(stateKey)
	dir := filepath.Join(t.TempDir(), "tailscale")
	id := c.send(map[string]any{"op": "start", "dir": dir, "hostname": "Mira-Helper", "controlURL": tn.ControlURL(), "stateKey": stateKey, "proxyUser": "mira", "proxyPassword": "pw"})
	if m := c.answer(id); !*m.OK {
		t.Fatalf("start: %+v\n%s", m, c.log())
	}
	proxy := c.next("proxy", func(m message) bool { return m.Event == "proxy" }).Address
	c.status("needs-login", func(st tailnet.Status) bool { return st.State == tailnet.StateNeedsLogin })
	login := c.answer(c.send(map[string]any{"op": "login"}))
	if !*login.OK || !strings.Contains(login.URL, "/auth/") {
		t.Fatalf("login: %+v", login)
	}
	if res, err := http.Get(login.URL); err != nil || res.StatusCode != 200 {
		t.Fatalf("login page: %v %v", res, err)
	}
	c.status("running with the Jellyfin PC", func(st tailnet.Status) bool {
		if st.State != tailnet.StateRunning {
			return false
		}
		for _, p := range st.Peers {
			if p.HostName == "jellyfin-pc" && p.Online {
				return true
			}
		}
		return false
	})

	if m := c.answer(c.send(map[string]any{"op": "probe", "address": "jellyfin-pc:8096"})); !*m.OK {
		t.Fatalf("probe: %+v", m)
	}
	if m := c.answer(c.send(map[string]any{"op": "probe", "address": strings.TrimPrefix(jf.URL, "http://"), "timeout": 2})); *m.OK || m.Code != tailnet.CodeRefused {
		t.Fatalf("probe this computer: %+v", m)
	}
	if m := c.answer(c.send(map[string]any{"op": "status"})); !*m.OK || m.Status == nil || m.Status.State != tailnet.StateRunning {
		t.Fatalf("status: %+v", m)
	}
	if m := c.answer(c.send(map[string]any{"op": "rebind"})); !*m.OK {
		t.Fatalf("rebind: %+v", m)
	}
	u, _ := url.Parse("http://mira:pw@" + proxy)
	client := &http.Client{Transport: &http.Transport{Proxy: http.ProxyURL(u)}, Timeout: 30 * time.Second}
	res, err := client.Get("http://jellyfin-pc:8096/System/Info/Public")
	if err != nil {
		t.Fatal(err)
	}
	body, _ := io.ReadAll(res.Body)
	res.Body.Close()
	if res.StatusCode != 200 || !strings.Contains(string(body), "Jellyfin de test") {
		t.Fatalf("through the proxy: %d %s", res.StatusCode, body)
	}

	// Stopped and started again in the same process: still logged in.
	if m := c.answer(c.send(map[string]any{"op": "stop"})); !*m.OK {
		t.Fatalf("stop: %+v", m)
	}
	c.status("stopped", func(st tailnet.Status) bool { return st.State == tailnet.StateStopped })
	id = c.send(map[string]any{"op": "start", "dir": dir, "hostname": "Mira-Helper", "controlURL": tn.ControlURL(), "stateKey": stateKey, "proxyUser": "mira", "proxyPassword": "pw"})
	if m := c.answer(id); !*m.OK {
		t.Fatalf("start again: %+v", m)
	}
	c.status("running again", func(st tailnet.Status) bool { return st.State == tailnet.StateRunning })

	// A request right before the end of the input still gets its answer; then the helper ends by itself.
	last := c.send(map[string]any{"op": "probe", "address": "jellyfin-pc:8096"})
	c.in.Close()
	if m := c.answer(last); !*m.OK {
		t.Fatalf("last probe: %+v", m)
	}
	ended := make(chan error, 1)
	go func() { ended <- c.cmd.Wait() }()
	select {
	case err := <-ended:
		if err != nil {
			t.Fatalf("the helper ended with %v", err)
		}
	case <-time.After(20 * time.Second):
		t.Fatal("the helper didn't end with its input")
	}
	if log := c.log(); strings.Contains(log, "/auth/") || strings.Contains(log, "pw@") {
		t.Fatalf("the log shows a login page or the proxy's password:\n%s", log)
	}
}
