#!/bin/bash
# Builds Mira.app for Apple Silicon and its disk image, on a Mac with Homebrew's mpv and dylibbundler:
#   tools/mac/package.sh 0.6.0   →   dist/mac/Mira.app and dist/mac/Mira-0.6.0-mac-arm64.dmg
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

# 2. The bundle.
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
  <key>CFBundleDevelopmentRegion</key><string>fr</string>
  <key>LSMinimumSystemVersion</key><string>$MINIMUM</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.entertainment</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSSupportsAutomaticGraphicsSwitching</key><true/>
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

# 5. Signed ad hoc, from the inside out: Apple Silicon runs no unsigned code, and the bundle's signature covers every
# file beside the executable (.NET's .dll and .json too, signed in their extended attributes), as Avalonia documents.
find "$APP/Contents/Frameworks" -type f -name "*.dylib" -print0 | xargs -0 -n 1 codesign --force --sign - --timestamp=none
find "$APP/Contents/MacOS" -type f ! -name Mira -print0 | xargs -0 -n 1 codesign --force --sign - --timestamp=none
codesign --force --sign - --timestamp=none "$APP"
codesign --verify --deep --strict --verbose=1 "$APP"

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
du -sh "$APP" "$DMG"
