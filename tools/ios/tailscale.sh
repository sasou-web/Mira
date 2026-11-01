#!/bin/bash
# Built-in Tailscale for Mira for iPhone: src/Mira.Tailscale/ios built as a static library for the iPhone and for its
# simulator (Apple Silicon), in src/Mira.iOS/Frameworks/MiraTailscale.xcframework, which the Xcode project links.
# Needs macOS with Xcode, and the Go of src/Mira.Tailscale/go.mod (GOTOOLCHAIN=auto fetches it).
#   tools/ios/tailscale.sh
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
module="$root/src/Mira.Tailscale"
out="$root/src/Mira.iOS/Frameworks/MiraTailscale.xcframework"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
# The same features as Windows and Mac, without the upload of logs to Tailscale (TestBuildTags checks the list).
tags="$(tr -d '\n' < "$module/build-tags.txt")"
# What comes from Tailscale's main branch before its release, as on Windows and Mac (tools/tailscale/backport.mjs).
{ read -r source; read -r goflags; } < <(node "$root/tools/tailscale/backport.mjs" "$work/module")

# One slice: Go's c-archive for GOOS=ios, compiled by the clang of the SDK for that target (the simulator's slice is told
# apart by the target clang writes in its objects, which Go's linker follows).
slice() {
  local sdk=$1 target=$2 dir="$work/$1"
  mkdir -p "$dir"
  local clang sysroot
  clang=$(xcrun --sdk "$sdk" --find clang)
  sysroot=$(xcrun --sdk "$sdk" --show-sdk-path)
  printf '#!/bin/sh\nexec "%s" -target %s -isysroot "%s" "$@"\n' "$clang" "$target" "$sysroot" > "$dir/cc"
  chmod +x "$dir/cc"
  (cd "$source" && GOFLAGS="$goflags" CGO_ENABLED=1 GOOS=ios GOARCH=arm64 CC="$dir/cc" CXX="$dir/cc" \
    go build -trimpath -buildmode=c-archive -tags "$tags" -ldflags "-s -w -buildid=" -o "$dir/libmiratailscale.a" ./ios)
  # The header Go writes is not the one Swift reads (include/MiraTailscale): only the library is kept.
  rm -f "$dir/libmiratailscale.h"
  echo "$sdk: $(du -h "$dir/libmiratailscale.a" | cut -f1)"
}

slice iphoneos arm64-apple-ios26.0
slice iphonesimulator arm64-apple-ios26.0-simulator

rm -rf "$out"
mkdir -p "$(dirname "$out")"
xcodebuild -create-xcframework \
  -library "$work/iphoneos/libmiratailscale.a" -headers "$module/ios/include" \
  -library "$work/iphonesimulator/libmiratailscale.a" -headers "$module/ios/include" \
  -output "$out"
echo "$out"
