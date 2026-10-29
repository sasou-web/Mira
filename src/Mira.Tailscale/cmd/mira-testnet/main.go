// mira-testnet runs a Tailscale network of its own for the apps' end-to-end checks: Tailscale's test coordination
// server, a relay and a STUN server, and a « Jellyfin PC » that exposes local ports (a real Jellyfin) on the test tailnet.
// An app's node joins it through the real interactive login: opening the login page logs the node in.
//
//	mira-testnet -addr 127.0.0.1:18090 -forward 8096=127.0.0.1:8096 [-login interactive|none] [-approval] [-info file]
//
// Once ready it prints one line, MIRA-TESTNET {…} (the control address, the Jellyfin PC's name, MagicDNS name and IPs),
// writes the same JSON to -info, and serves, on the control address:
//
//	GET  /auth/<id>                  the login page: logs the node in (« Connecté au réseau Tailscale de test »)
//	GET  /mira/info                  the same JSON
//	POST /mira/jellyfin?online=0|1   switches the Jellyfin PC off or on (same addresses)
//	POST /mira/expire?name=<host>    makes a device's key expire now
//	POST /mira/approve               approves the devices waiting for approval (-approval)
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"syscall"

	"miracinema.app/tailscale/tailnet/tailnettest"
)

type forwards map[uint16]string

func (f forwards) String() string { return fmt.Sprint(map[uint16]string(f)) }

func (f forwards) Set(v string) error {
	port, target, ok := strings.Cut(v, "=")
	n, err := strconv.ParseUint(port, 10, 16)
	if !ok || err != nil || n == 0 || target == "" {
		return fmt.Errorf("%q: tailnet port=local host:port", v)
	}
	f[uint16(n)] = target
	return nil
}

func main() {
	fwd := forwards{}
	addr := flag.String("addr", "127.0.0.1:18090", "address of the coordination server (the relay and STUN listen on its IP)")
	hostname := flag.String("hostname", "jellyfin-pc", "the Jellyfin PC's name on the tailnet")
	login := flag.String("login", "interactive", "interactive (a login page per node) or none (nodes join at once)")
	approval := flag.Bool("approval", false, "new devices wait for POST /mira/approve")
	info := flag.String("info", "", "file that receives the network's description once ready")
	dir := flag.String("dir", "", "folder for the Jellyfin PC's state (temporary by default)")
	verbose := flag.Bool("v", false, "log the network's diagnostics")
	flag.Var(fwd, "forward", "tailnet port=local host:port the Jellyfin PC exposes (repeatable)")
	flag.Parse()

	logf := func(string, ...any) {}
	if *verbose {
		logf = log.Printf
	}
	n, err := tailnettest.Start(tailnettest.Options{
		Addr:     *addr,
		Hostname: *hostname,
		Forward:  fwd,
		Login:    *login,
		Approval: *approval,
		Dir:      *dir,
		Logf:     logf,
	})
	if err != nil {
		log.Fatalf("mira-testnet: %v", err)
	}
	data, _ := json.Marshal(n.Info())
	if *info != "" {
		if err := os.WriteFile(*info, append(data, '\n'), 0o644); err != nil {
			n.Close()
			log.Fatalf("mira-testnet: %v", err)
		}
	}
	fmt.Printf("MIRA-TESTNET %s\n", data)

	stop := make(chan os.Signal, 1)
	signal.Notify(stop, os.Interrupt, syscall.SIGTERM)
	<-stop
	n.Close()
}
