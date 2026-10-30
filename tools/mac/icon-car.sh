#!/bin/bash
# Mira's icon for macOS 26: src/Mira.Mac/Icon/Mira.icon (Icon Composer's format) compiled into an asset catalog,
#   tools/mac/icon-car.sh <folder>   →   <folder>/Assets.car, which tools/mac/package.sh puts in Mira.app.
# Needs Xcode 26's actool on macOS 26: on macOS 15, its asset agent crashes (it links macOS 26's media frameworks), so
# mac.yml runs this on macos-26 and hands Assets.car to the build on macos-15 (MIRA_ASSETS_CAR).
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
OUT=${1:?output folder}
# Mira's oldest macOS (tools/mac/package.sh): the catalog stays readable there, where Mira.icns is shown.
MINIMUM=15.0
mkdir -p "$OUT"
xcrun actool --version
xcrun actool "$ROOT/src/Mira.Mac/Icon/Mira.icon" --compile "$OUT" --app-icon Mira \
  --platform macosx --target-device mac --minimum-deployment-target "$MINIMUM" \
  --output-partial-info-plist "$OUT/partial.plist" --output-format human-readable-text --errors --warnings
[ -f "$OUT/Assets.car" ] || { echo "actool made no Assets.car" >&2; ls -l "$OUT" >&2; exit 1; }
cat "$OUT/partial.plist"
