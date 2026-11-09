#!/bin/bash
# An unpublished build of Mira for Mac that runs on Linux (x64), for trying the Mac app's pages, player and settings on
# a Linux desktop: the same Avalonia app, its .NET runtime inside, Mira's Tailscale node beside it. Not a release: no
# signature, no updates (it only says when a newer Mira is out), mpv from the system (libmpv2), and nothing of macOS
# (menu bar, Dock, trackpad, keychain, notifications). Made by .github/workflows/linux.yml.
#   tools/linux/package.sh 0.9.5   →   dist/linux/Mira-0.9.5-linux-x64-preview.tar.gz and its .sha256
# Needs the .NET 10 SDK, Node, and Go 1.21 or later (tools/tailscale/build.mjs fetches the Go of go.mod).
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1
VERSION=${1:?version, for example 0.9.5}
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
OUT="$ROOT/dist/linux"
NAME="Mira-$VERSION-linux-x64-preview"
APP="$OUT/$NAME"
rm -rf "$OUT" && mkdir -p "$APP"

dotnet publish "$ROOT/src/Mira.Mac/Mira.Mac.csproj" -c Release -r linux-x64 --self-contained true \
  -p:Version="$VERSION" -p:UseAppHost=true -p:DebugType=none -p:DebugSymbols=false -o "$APP"
# Beside Mira, which starts it from there (TailnetExecutable), at Mira's version (TailnetProcess refuses another).
node "$ROOT/tools/tailscale/build.mjs" linux/amd64 "$OUT/tailscale" --version "$VERSION"
cp "$OUT/tailscale/mira-tailscale" "$APP/mira-tailscale"
mkdir -p "$APP/licenses"
cp -R "$OUT/tailscale/licenses" "$APP/licenses/mira-tailscale"
cp "$ROOT/LICENSE" "$APP/licenses/" 2>/dev/null || true
cp "$ROOT/tools/linux/mira" "$ROOT/tools/linux/README.txt" "$APP/"
chmod +x "$APP/mira" "$APP/Mira" "$APP/mira-tailscale"

tar -C "$OUT" -czf "$OUT/$NAME.tar.gz" "$NAME"
(cd "$OUT" && sha256sum "$NAME.tar.gz" > "$NAME.tar.gz.sha256")
cat "$OUT/$NAME.tar.gz.sha256"
