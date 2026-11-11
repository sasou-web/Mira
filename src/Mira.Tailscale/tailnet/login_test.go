package tailnet_test

import (
	"bytes"
	"context"
	"encoding/json"
	"io"
	"net/http"
	"strings"
	"sync"
	"testing"
	"time"

	"miracinema.app/tailscale/tailnet"
	"miracinema.app/tailscale/tailnet/tailnettest"
)

// A node that needs a login asks the coordination server for one as it starts (tsnet does). Mira asks for the page as
// soon as the node says it needs one, often before that first login has brought its page: asking Tailscale again then
// made it drop that login for another, with a new node key, and the dropped login's page could still come first (CI,
// 2026-11-11): opened, it logged in a key the node no longer used, and the node never joined. One login, one page, and
// it is the page the node waits on.
func TestLoginAskedBeforeTheFirstPageComes(t *testing.T) {
	if testing.Short() {
		t.Skip("starts a Tailscale network")
	}
	tn, err := tailnettest.Start(tailnettest.Options{Logf: t.Logf})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(tn.Close)
	var mu sync.Mutex
	logins := 0 // registrations that start a login, follow-ups of a login page aside
	held := make(chan struct{})
	release := make(chan struct{})
	var once sync.Once
	letGo := func() { once.Do(func() { close(release) }) }
	t.Cleanup(letGo)
	tn.Control().MaybeRejectRequest = func(r *http.Request) (int, string, string) {
		if r.URL.Path != "/machine/register" {
			return 0, "", ""
		}
		body, err := io.ReadAll(r.Body)
		if err != nil {
			return http.StatusBadRequest, "", err.Error()
		}
		r.Body = io.NopCloser(bytes.NewReader(body))
		var req struct{ Followup string }
		if json.Unmarshal(body, &req) != nil || req.Followup != "" {
			return 0, "", ""
		}
		mu.Lock()
		logins++
		first := logins == 1
		mu.Unlock()
		if first {
			// The first login stays at the coordination server until Mira has asked for the page.
			close(held)
			<-release
		}
		return 0, "", ""
	}
	node, err := tailnet.Start(tailnet.Config{Dir: t.TempDir(), Hostname: "Mira", ControlURL: tn.ControlURL(), StateKey: key(t), Logf: t.Logf})
	if err != nil {
		t.Fatal(err)
	}
	defer node.Close()
	select {
	case <-held:
	case <-time.After(30 * time.Second):
		t.Fatal("the node never asked for a login")
	}
	waitFor(t, node, "needs-login", func(st tailnet.Status) bool { return st.State == tailnet.StateNeedsLogin })
	type answer struct {
		page string
		err  error
	}
	got := make(chan answer, 1)
	go func() {
		ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
		defer cancel()
		page, err := node.Login(ctx)
		got <- answer{page, err}
	}()
	// Login asked Tailscale for a page, if it does, well within this.
	time.Sleep(500 * time.Millisecond)
	letGo()
	var a answer
	select {
	case a = <-got:
	case <-time.After(40 * time.Second):
		t.Fatal("Login never answered")
	}
	if a.err != nil || !strings.Contains(a.page, "/auth/") {
		t.Fatalf("Login: %q, %v", a.page, a.err)
	}
	res, err := http.Get(a.page)
	if err != nil {
		t.Fatal(err)
	}
	res.Body.Close()
	if res.StatusCode != http.StatusOK {
		t.Fatalf("login page answered %d", res.StatusCode)
	}
	waitFor(t, node, "running once the page is opened", func(st tailnet.Status) bool { return st.State == tailnet.StateRunning })
	mu.Lock()
	defer mu.Unlock()
	if logins != 1 {
		t.Fatalf("%d logins asked of the coordination server for one Login, not 1", logins)
	}
}
