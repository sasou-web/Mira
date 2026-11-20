# Mira.Tailscale

Mira’s Tailscale node: a tsnet node, in user space, that reaches the person’s Jellyfin over their Tailscale network
without the Tailscale app, without a network driver and without administrator rights.

- `tailnet/`: the node (`Start`, `Status`, `Login`, `Logout`, `Dial`, `Probe`, `Rebind`), its encrypted state
  (`state.bin`, AES-256-GCM under a key kept by the app), its folder lock, the local proxy (SOCKS5 for .NET,
  HTTP proxy for mpv); shared by the three apps.
- `cmd/mira-tailscale/`: Mira’s helper for Windows and Mac (JSON lines on standard input and output).
- `cmd/mira-testnet/`: the test bench (test Tailscale control server, relay, “Jellyfin’s PC” on the test network).
- `build-tags.txt`: the build tags, on one line; only the features that are needed, without sending logs to
  Tailscale (`ts_omit_logtail`). `TestBuildTags` checks it.

Privacy: the node only talks to Tailscale’s coordination server and its relays, like any Tailscale device;
it tells them it keeps no logs (`--no-logs-no-support`) and sends none.

Tests (the Go from `go.mod`; `GOTOOLCHAIN=auto` downloads it):

    cd src/Mira.Tailscale
    go test -tags "$(cat build-tags.txt)" ./...
