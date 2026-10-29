package tailnet

import (
	"bytes"
	"crypto/rand"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"tailscale.com/ipn"
)

func TestHostname(t *testing.T) {
	for in, want := range map[string]string{
		"Mira-MacBook Air de Ogsas": "mira-macbook-air-de-ogsas",
		"Mira-DESKTOP-INVEFQH":      "mira-desktop-invefqh",
		"  Mira  --  PC  ":          "mira-pc",
		"Mira-Élodie’s Mac":         "mira-elodie-s-mac",
		"MacBook Air de Zoë":        "macbook-air-de-zoe",
		"":                          "mira",
		"---":                       "mira",
		strings.Repeat("a", 80):     strings.Repeat("a", 63),
		"Mira-" + strings.Repeat("b", 57) + " c": "mira-" + strings.Repeat("b", 57),
	} {
		if got := Hostname(in); got != want {
			t.Errorf("Hostname(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestRedact(t *testing.T) {
	for in, want := range map[string]string{
		"To start this tsnet server, go to: https://login.tailscale.com/a/1a2b3c4d5e": "To start this tsnet server, go to: <login page>",
		"authURL=http://127.0.0.1:18090/auth/0123456789abcdef0123 (wait)":            "authURL=<login page> (wait)",
		"peer 100.64.0.1 at https://example.com/about":                               "peer 100.64.0.1 at https://example.com/about",
	} {
		if got := Redact(in); got != want {
			t.Errorf("Redact(%q) = %q, want %q", in, got, want)
		}
	}
}

func newKey(t *testing.T) []byte {
	key := make([]byte, stateKeySize)
	if _, err := rand.Read(key); err != nil {
		t.Fatal(err)
	}
	return key
}

func TestStore(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, stateFile)
	key := newKey(t)
	s, err := openStore(path, key, t.Logf)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := s.ReadState("_machinekey"); !errors.Is(err, ipn.ErrStateNotExist) {
		t.Fatalf("empty store: %v", err)
	}
	secret := []byte("privkey:0123456789abcdef")
	if err := s.WriteState("_machinekey", secret); err != nil {
		t.Fatal(err)
	}
	if err := s.WriteState("_profiles", []byte("{}")); err != nil {
		t.Fatal(err)
	}
	if err := s.WriteState("_profiles", nil); err != nil {
		t.Fatal(err)
	}
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Contains(raw, secret) || bytes.Contains(raw, []byte("_machinekey")) {
		t.Fatal("the state is written in clear")
	}
	if info, err := os.Stat(path); err == nil && info.Mode().Perm()&0o077 != 0 && os.PathSeparator == '/' {
		t.Errorf("state.bin is readable by others: %v", info.Mode())
	}

	again, err := openStore(path, key, t.Logf)
	if err != nil {
		t.Fatal(err)
	}
	if got, err := again.ReadState("_machinekey"); err != nil || !bytes.Equal(got, secret) {
		t.Fatalf("reopened: %q, %v", got, err)
	}
	if _, err := again.ReadState("_profiles"); !errors.Is(err, ipn.ErrStateNotExist) {
		t.Fatalf("deleted entry: %v", err)
	}

	// Another key (the keychain was reset): an empty state, the old file kept aside.
	other, err := openStore(path, newKey(t), t.Logf)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := other.ReadState("_machinekey"); !errors.Is(err, ipn.ErrStateNotExist) {
		t.Fatalf("other key: %v", err)
	}
	aside, _ := filepath.Glob(path + ".bad-*")
	if len(aside) != 1 {
		t.Fatalf("unreadable state kept aside: %v", aside)
	}
	if _, err := os.Stat(path); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("unreadable state still in place: %v", err)
	}
	// A damaged file too.
	if err := os.WriteFile(path, append([]byte(stateMagic), 1, 2, 3), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := openStore(path, key, t.Logf); err != nil {
		t.Fatal(err)
	}
}

func TestLock(t *testing.T) {
	path := filepath.Join(t.TempDir(), lockFile)
	first, err := lockDir(path)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := lockDir(path); CodeOf(err) != CodeLocked {
		t.Fatalf("second lock: %v", err)
	}
	first.release()
	second, err := lockDir(path)
	if err != nil {
		t.Fatalf("after release: %v", err)
	}
	second.release()
}

func TestResolve(t *testing.T) {
	st := Status{Peers: []Device{
		{ID: "a", DNSName: "desktop-invefqh.tail1234.ts.net", IPs: []string{"100.114.247.13", "fd7a:115c:a1e0::1"}, Online: true},
		{ID: "b", DNSName: "nas.tail1234.ts.net", IPs: []string{"fd7a:115c:a1e0::2"}},
	}, Self: &Device{ID: "self", DNSName: "mira-mac.tail1234.ts.net", IPs: []string{"100.64.0.9"}}}
	for _, c := range []struct {
		host, network, ip, id string
	}{
		{"100.114.247.13", "tcp", "100.114.247.13", "a"},
		{"fd7a:115c:a1e0::1", "tcp", "fd7a:115c:a1e0::1", "a"},
		{"[fd7a:115c:a1e0::1]", "tcp", "fd7a:115c:a1e0::1", "a"},
		{"::ffff:100.114.247.13", "tcp", "100.114.247.13", "a"},
		{"desktop-invefqh.tail1234.ts.net", "tcp", "100.114.247.13", "a"},
		{"DESKTOP-INVEFQH.tail1234.ts.net.", "tcp", "100.114.247.13", "a"},
		{"desktop-invefqh", "tcp", "100.114.247.13", "a"},
		{"desktop-invefqh", "tcp6", "fd7a:115c:a1e0::1", "a"},
		{"nas", "tcp", "fd7a:115c:a1e0::2", "b"},
		{"nas", "tcp4", "", ""},
		{"100.64.0.9", "tcp", "", ""},       // this node
		{"mira-mac", "tcp", "", ""},         // this node
		{"192.168.1.198", "tcp", "", ""},    // the home network
		{"127.0.0.1", "tcp", "", ""},        // this computer
		{"example.com", "tcp", "", ""},      // the Internet
		{"desktop-invefqh.local", "tcp", "", ""},
		{"", "tcp", "", ""},
	} {
		ip, d, ok := resolve(c.host, st, c.network)
		if c.ip == "" {
			if ok {
				t.Errorf("resolve(%q, %s) = %v, want refused", c.host, c.network, ip)
			}
			continue
		}
		if !ok || ip.String() != c.ip || d.ID != c.id {
			t.Errorf("resolve(%q, %s) = %v %q %v, want %s %q", c.host, c.network, ip, d.ID, ok, c.ip, c.id)
		}
	}
}
