#!/bin/bash
# Builds Mira.app for Apple Silicon and its disk image, on a Mac with Homebrew's mpv and dylibbundler:
#   tools/mac/package.sh 0.6.0   →   dist/mac/Mira.app, dist/mac/Mira-0.6.0-mac-arm64.dmg and its .sha256
# No Apple Developer account: the app is signed ad hoc, so macOS asks once to allow it (see docs/INSTALLATION.md).
set -euo pipefail
# No usage data from the .NET SDK (nor from Avalonia's build, see Mira.Mac.csproj), on a Mac as in the CI.
export DOTNET_CLI_TELEMETRY_OPTOUT=1
VERSION=${1:?version, for example 0.6.0}
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
OUT="$ROOT/dist/mac"
APP="$OUT/Mira.app"
BREW=$(brew --prefix)
# The oldest macOS Mira runs on, announced in README.md and docs/INSTALLATION-MAC.md. Homebrew builds its libraries for
# the macOS it runs on (macos-15 on GitHub): step 4 refuses any bundled code that asks for a later one.
MINIMUM=15.0
rm -rf "$OUT" && mkdir -p "$OUT"

# 1. Mira, with its own .NET runtime: nothing to install beside it.
dotnet publish "$ROOT/src/Mira.Mac/Mira.Mac.csproj" -c Release -r osx-arm64 --self-contained true \
  -p:Version="$VERSION" -p:UseAppHost=true -p:DebugType=none -p:DebugSymbols=false -o "$OUT/publish"

# 2. The bundle. English and French (CFBundleLocalizations): macOS's own panels (open file…) follow the Mac's language,
# as Mira's texts do (SystemLanguage).
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$APP/Contents/Frameworks"
cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Mira</string>
  <key>CFBundleDisplayName</key><string>Mira</string>
  <key>CFBundleIdentifier</key><string>io.github.sasou-web.mira</string>
  <key>CFBundleExecutable</key><string>Mira</string>
  <key>CFBundleIconFile</key><string>Mira</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleLocalizations</key><array><string>en</string><string>fr</string></array>
  <key>LSMinimumSystemVersion</key><string>$MINIMUM</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.entertainment</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSSupportsAutomaticGraphicsSwitching</key><true/>
  <key>CFBundleURLTypes</key>
  <array><dict>
    <key>CFBundleURLName</key><string>io.github.sasou-web.mira.ensemble</string>
    <key>CFBundleURLSchemes</key><array><string>mira</string></array>
  </dict></array>
  <key>NSHumanReadableCopyright</key><string>Mira — client Jellyfin libre</string>
</dict>
</plist>
PLIST

# 3. The icon, from the 1024 px mark.
ICONSET="$OUT/Mira.iconset"; mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  sips -z $size $size "$ROOT/src/Mira.Mac/Assets/AppIcon.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  sips -z $((size * 2)) $((size * 2)) "$ROOT/src/Mira.Mac/Assets/AppIcon.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Mira.icns"

# 4. libmpv and every library it needs, from Homebrew, rewritten to load from Contents/Frameworks.
cp "$BREW/lib/libmpv.2.dylib" "$APP/Contents/Frameworks/"
chmod u+w "$APP/Contents/Frameworks/libmpv.2.dylib"
dylibbundler -of -b -ns -x "$APP/Contents/Frameworks/libmpv.2.dylib" -d "$APP/Contents/Frameworks/" -p @loader_path/ -s "$BREW/lib"
install_name_tool -id @rpath/libmpv.2.dylib "$APP/Contents/Frameworks/libmpv.2.dylib"
# dylibbundler turns every rpath into @loader_path/: Homebrew's libmpv has two (Swift's, in Xcode and in macOS), and
# macOS 15 refuses a library that names the same rpath twice (dyld: « duplicate LC_RPATH »). One of each is kept.
rpaths() { otool -l "$1" | awk '$1 == "cmd" { rpath = ($2 == "LC_RPATH") } rpath && $1 == "path" { print $2 }'; }
for binary in "$APP"/Contents/Frameworks/*.dylib; do
  for path in $(rpaths "$binary" | sort | uniq -d); do
    while [ "$(rpaths "$binary" | grep -c -x -F "$path")" -gt 1 ]; do install_name_tool -delete_rpath "$path" "$binary"; done
    # Some versions of install_name_tool delete every copy at once: one is put back.
    if [ "$(rpaths "$binary" | grep -c -x -F "$path")" -eq 0 ]; then install_name_tool -add_rpath "$path" "$binary"; fi
  done
done
# Nothing may still point into Homebrew: a Mac without it would fail to load mpv.
if otool -L "$APP"/Contents/Frameworks/*.dylib | grep -E "^\s+($BREW|/usr/local/(opt|Cellar))"; then echo "libraries still linked to Homebrew" >&2; exit 1; fi
# Nothing may ask for a macOS newer than the one Mira declares: it would fail to load there, after the Mac let it open.
newer=0
for binary in "$APP/Contents/MacOS/Mira" "$APP"/Contents/MacOS/*.dylib "$APP"/Contents/Frameworks/*.dylib; do
  [ -f "$binary" ] || continue
  # awk reads to the end: leaving early would stop otool with SIGPIPE, which pipefail turns into a failed build.
  needs=$(otool -l "$binary" | awk '/LC_BUILD_VERSION/ { build = 1 } /LC_VERSION_MIN_MACOSX/ { old = 1 }
    !found && ((build && $1 == "minos") || (old && $1 == "version")) { print $2; found = 1 }')
  if [ -z "$needs" ]; then echo "no minimum macOS in $(basename "$binary")" >&2; newer=1; continue; fi
  if awk -v a="$needs" -v b="$MINIMUM" 'BEGIN { split(a, x, "."); split(b, y, "."); exit !(x[1] * 1000 + x[2] > y[1] * 1000 + y[2]) }'; then
    echo "$(basename "$binary") needs macOS $needs, Mira declares $MINIMUM" >&2; newer=1
  fi
done
if [ "$newer" -ne 0 ]; then exit 1; fi

# 4b. What the bundle carries from others, and under which licences: Mira's own notices and licence files, then every
# Homebrew formula whose library is in Contents/Frameworks (libmpv and FFmpeg are GPL), with its exact version, its
# licence files and where its source is. Written into Contents/Resources before the bundle is signed.
RESOURCES="$APP/Contents/Resources"
cp "$ROOT/LICENSE" "$ROOT/THIRD_PARTY_NOTICES.md" "$RESOURCES/"
cp -R "$ROOT/licenses" "$RESOURCES/licenses"
mkdir -p "$RESOURCES/licenses/homebrew"
# A library's formula and version, from where Homebrew keeps it: <prefix>/Cellar/<formula>/<version>/lib/<file>.
cellar() { python3 -c 'import os, sys; print(os.path.realpath(sys.argv[1]))' "$1"; }
KEGS="$OUT/kegs.txt"; : > "$KEGS"
for library in "$APP"/Contents/Frameworks/*.dylib; do
  name=$(basename "$library"); keg=""
  # Linked into the prefix, or keg-only (its opt folder only).
  for candidate in "$BREW/lib/$name" "$BREW"/opt/*/lib/"$name"; do
    [ -e "$candidate" ] || continue
    real=$(cellar "$candidate")
    case "$real" in "$BREW/Cellar/"*/lib/*) keg=${real#"$BREW/Cellar/"}; keg=${keg%%/lib/*}; break ;; esac
  done
  if [ -z "$keg" ]; then echo "no Homebrew formula found for $name" >&2; exit 1; fi
  echo "$keg $name" >> "$KEGS"
done
cut -d ' ' -f 1 "$KEGS" | sort -u | while read -r keg; do
  formula=${keg%%/*}
  mkdir -p "$RESOURCES/licenses/homebrew/$formula"
  # Homebrew keeps each formula's licence files at the top of its keg.
  find "$BREW/Cellar/$keg" -maxdepth 1 -type f \( -iname 'COPYING*' -o -iname 'LICENSE*' -o -iname 'LICENCE*' -o -iname 'NOTICE*' -o -iname 'COPYRIGHT*' \) \
    -exec cp {} "$RESOURCES/licenses/homebrew/$formula/" \;
done
# shellcheck disable=SC2046 # one argument per formula
brew info --json=v2 $(cut -d ' ' -f 1 "$KEGS" | cut -d / -f 1 | sort -u) > "$OUT/formulae.json"
python3 - "$KEGS" "$OUT/formulae.json" "$RESOURCES/licenses/homebrew" "$VERSION" > "$RESOURCES/licenses/HOMEBREW-LIBRARIES.txt" <<'PY'
import json, os, sys
kegs, info, folder, version = sys.argv[1:5]
libraries = {}
for line in open(kegs):
    keg, name = line.split()
    libraries.setdefault(keg, []).append(name)
formulae = {f["name"]: f for f in json.load(open(info))["formulae"]}
print(f"Libraries bundled in Mira {version} for Mac (Mira.app/Contents/Frameworks)\n")
print("Mira loads libmpv to play video. libmpv and the libraries it needs were built by Homebrew (https://brew.sh) and")
print("copied unmodified into the app, except for their load paths (rewritten by dylibbundler and install_name_tool so that")
print("they load from the app) and their code signatures (replaced by an ad hoc signature). Each keeps its own licence;")
print("Mira's MIT licence does not cover them. Several are under the GPL or the LGPL, such as mpv and FFmpeg as Homebrew")
print("builds them: the full texts are licenses/GPL-2.0.txt, GPL-3.0.txt, LGPL-2.1.txt and LGPL-3.0.txt; each formula's own")
print("licence files, when it ships them, are in licenses/homebrew/<formula>/.\n")
print("Source code: each library's source is the release archive named below, built with the Homebrew formula of the same")
print("name and version (https://github.com/Homebrew/homebrew-core). For three years from this release, sasou-web will also")
print("provide anyone with a copy of the complete corresponding source code of these libraries, for no more than the cost")
print("of providing it: ask on https://github.com/sasou-web/Mira/issues.\n")
missing = []
for keg in sorted(libraries):
    name, kept = keg.split("/", 1)
    f = formulae.get(name, {})
    texts = sorted(os.listdir(os.path.join(folder, name))) if os.path.isdir(os.path.join(folder, name)) else []
    if not texts:
        missing.append(name)
    print(f"{name} {kept}")
    # An SPDX name, or an expression Homebrew writes as JSON ({"any_of": [...]}).
    licence = f.get("license")
    print(f"  Licence: {licence if isinstance(licence, str) else json.dumps(licence) if licence else 'see the project'}")
    print(f"  Project: {f.get('homepage') or '-'}")
    print(f"  Source: {((f.get('urls') or {}).get('stable') or {}).get('url') or '-'}")
    print(f"  Files: {', '.join(sorted(libraries[keg]))}")
    print(f"  Licence files: {', '.join('licenses/homebrew/' + name + '/' + t for t in texts) if texts else 'none in the formula: see the project'}\n")
if missing:
    print("Formulas without a licence file of their own: " + ", ".join(missing) + ".", file=sys.stderr)
PY
rm -f "$KEGS" "$OUT/formulae.json"
find "$RESOURCES/licenses/homebrew" -mindepth 1 -type d -empty -delete
# libmpv and FFmpeg must be listed, and the GPL's text be beside them, as the GPL asks.
for formula in mpv ffmpeg; do
  grep -q -E "^$formula(@[0-9.]+)? " "$RESOURCES/licenses/HOMEBREW-LIBRARIES.txt" || { echo "$formula missing from the bundled libraries' notice" >&2; exit 1; }
done
for text in GPL-2.0 GPL-3.0 LGPL-2.1 LGPL-3.0; do
  [ -s "$RESOURCES/licenses/$text.txt" ] || { echo "licenses/$text.txt missing" >&2; exit 1; }
done

# 5. Signed ad hoc, from the inside out: Apple Silicon runs no unsigned code, and the bundle's signature covers every
# file beside the executable (.NET's .dll and .json too, signed in their extended attributes), as Avalonia documents.
find "$APP/Contents/Frameworks" -type f -name "*.dylib" -print0 | xargs -0 -n 1 codesign --force --sign - --timestamp=none
find "$APP/Contents/MacOS" -type f ! -name Mira -print0 | xargs -0 -n 1 codesign --force --sign - --timestamp=none
codesign --force --sign - --timestamp=none "$APP"
codesign --verify --deep --strict --verbose=1 "$APP"
# libmpv loaded here from the bundle, as Mira loads it: whatever dyld refuses (a library missing, an rpath twice) stops
# the build with its reason, instead of a player that never starts on every Mac.
python3 -c 'import ctypes, sys; ctypes.CDLL(sys.argv[1])' "$APP/Contents/Frameworks/libmpv.2.dylib"

# 6. The disk image: Mira and a link to Applications, to drag one onto the other.
STAGE="$OUT/dmg"; mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"; ln -s /Applications "$STAGE/Applications"
# hdiutil sometimes answers « Resource busy » while macOS still scans the new files (seen on GitHub's Macs): try again.
DMG="$OUT/Mira-$VERSION-mac-arm64.dmg"
for attempt in 1 2 3 4 5; do
  if hdiutil create -volname "Mira $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG"; then break; fi
  rm -f "$DMG"
  if [ "$attempt" -eq 5 ]; then echo "hdiutil failed 5 times" >&2; exit 1; fi
  sleep $((attempt * 5))
done
# 7. Its SHA-256, published beside it: in the download folder, « shasum -a 256 -c Mira-x.y.z-mac-arm64.dmg.sha256 » checks
# the copy. shasum is on every Mac (md5sum is not).
(cd "$OUT" && shasum -a 256 "$(basename "$DMG")" > "$(basename "$DMG").sha256")
du -sh "$APP" "$DMG"
cat "$DMG.sha256"
