#!/bin/bash
# Mira's extension for Jellyfin: the plugin as Jellyfin installs it, and the repository manifest that points to it.
# Each release attaches both; Jellyfin installs and updates the extension from the manifest (Mira for Windows adds it).
#   tools/web/package.sh 0.6.1 [download base]
#   → dist/web/mira-jellyfin-0.6.1.zip and dist/web/jellyfin-manifest.json
# The download base defaults to the release's assets on GitHub (another base serves local tests).
set -euo pipefail
# No usage data from the .NET SDK (nor from Avalonia's build, see Mira.Mac.csproj), on a Mac as in the CI.
export DOTNET_CLI_TELEMETRY_OPTOUT=1
VERSION="${1:?version x.y.z}"
BASE="${2:-https://github.com/sasou-web/Mira/releases/download/v$VERSION}"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Version x.y.z expected, got $VERSION" >&2; exit 1; }
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/dist/web"; BUILD="$OUT/build"
rm -rf "$OUT" && mkdir -p "$BUILD"

dotnet publish "$ROOT/src/Mira.Jellyfin/Mira.Jellyfin.csproj" -c Release -o "$BUILD" -p:Version="$VERSION" -p:DebugType=none -nologo -v q
ZIP="mira-jellyfin-$VERSION.zip"
(cd "$BUILD" && zip -q -X "$OUT/$ZIP" Mira.Jellyfin.dll)

# Jellyfin loads a plugin only if its own version is at least targetAbi: 10.9, the oldest Mira supports.
# The checksum is the zip's MD5, which Jellyfin checks before installing it (computed here: macOS has no md5sum).
VERSION="$VERSION" BASE="$BASE" ZIP="$ZIP" OUT="$OUT" python3 - > "$OUT/jellyfin-manifest.json" <<'PY'
import datetime, hashlib, json, os
version = os.environ["VERSION"]
with open(os.path.join(os.environ["OUT"], os.environ["ZIP"]), "rb") as archive:
    checksum = hashlib.md5(archive.read()).hexdigest()
print(json.dumps([{
    "guid": "4ea89259-3350-45b0-8045-f1ab627214dd",
    "name": "Mira",
    "description": "What Mira for Windows, Mac and iPhone ask of this server: the party link, guests without an account, downloads on the server PC, subtitles found automatically.",
    "overview": "The server side of Mira for Windows, Mac and iPhone.",
    "owner": "sasou-web",
    "category": "General",
    "versions": [{
        "version": version + ".0",
        "changelog": f"https://github.com/sasou-web/Mira/blob/v{version}/CHANGELOG.md",
        "targetAbi": "10.9.0.0",
        "sourceUrl": f"{os.environ['BASE']}/{os.environ['ZIP']}",
        "checksum": checksum,
        "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }],
}], ensure_ascii=False, indent=2))
PY
rm -rf "$BUILD"
ls -la "$OUT"
