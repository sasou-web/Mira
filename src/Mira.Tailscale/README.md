# Mira.Tailscale

Le nœud Tailscale de Mira : un nœud tsnet, en espace utilisateur, qui joint le Jellyfin de la personne par son réseau
Tailscale sans l'app Tailscale, sans pilote réseau ni droits d'administrateur.

- `tailnet/` : le nœud (`Start`, `Status`, `Login`, `Logout`, `Dial`, `Probe`, `Rebind`), son état chiffré
  (`state.bin`, AES-256-GCM sous une clé gardée par l'app), le verrou de son dossier, le proxy local (SOCKS5 pour .NET,
  proxy HTTP pour mpv) ; commun aux trois apps.
- `cmd/mira-tailscale/` : l'assistant de Mira pour Windows et Mac (lignes JSON sur l'entrée et la sortie standard).
- `cmd/mira-testnet/` : le banc d'essai (contrôle Tailscale de test, relais, « PC de Jellyfin » sur le réseau de test).
- `build-tags.txt` : les étiquettes de compilation, une ligne ; rien que les fonctions utiles, sans l'envoi de journaux à
  Tailscale (`ts_omit_logtail`). `TestBuildTags` la vérifie.

Vie privée : le nœud ne parle qu'au serveur de coordination de Tailscale et à ses relais, comme tout appareil Tailscale ;
il leur dit qu'il ne garde aucun journal (`--no-logs-no-support`) et n'en envoie aucun.

Tests (Go de `go.mod`, `GOTOOLCHAIN=auto` le télécharge) :

    cd src/Mira.Tailscale
    go test -tags "$(cat build-tags.txt)" ./...
