#!/bin/bash
# Mira web for Jellyfin: the plugin (with the web app built in) as Jellyfin installs it, and the repository manifest
# that points to it. Each release attaches both; Jellyfin installs and updates Mira web from the manifest.
#   tools/web/package.sh 0.6.1 [download base]
#   → dist/web/mira-jellyfin-0.6.1.zip and dist/web/jellyfin-manifest.json
# The download base defaults to the release's assets on GitHub (another base serves local tests).
set -euo pipefail
VERSION="${1:?version x.y.z}"
BASE="${2:-https://github.com/sasou-web/Mira/releases/download/v$VERSION}"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Version x.y.z expected, got $VERSION" >&2; exit 1; }
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/dist/web"; BUILD="$OUT/build"
rm -rf "$OUT" && mkdir -p "$BUILD"

dotnet publish "$ROOT/src/Mira.Jellyfin/Mira.Jellyfin.csproj" -c Release -o "$BUILD" -p:Version="$VERSION" -p:DebugType=none -nologo -v q
ZIP="mira-jellyfin-$VERSION.zip"
(cd "$BUILD" && zip -q -X "$OUT/$ZIP" Mira.Jellyfin.dll)
CHECKSUM=$(md5sum "$OUT/$ZIP" | cut -d' ' -f1)

# Jellyfin loads a plugin only if its own version is at least targetAbi: 10.9, the oldest Mira supports.
VERSION="$VERSION" BASE="$BASE" ZIP="$ZIP" CHECKSUM="$CHECKSUM" python3 - > "$OUT/jellyfin-manifest.json" <<'PY'
import json, os, datetime
version = os.environ["VERSION"]
print(json.dumps([{
    "guid": "4ea89259-3350-45b0-8045-f1ab627214dd",
    "name": "Mira",
    "description": "Mira sur iPhone, iPad et Android : ouvre http://<adresse du serveur>:8096/Mira dans le navigateur, puis ajoute la page à l’écran d’accueil.",
    "overview": "Mira dans le navigateur du téléphone, comme une app.",
    "owner": "sasou-web",
    "category": "General",
    "versions": [{
        "version": version + ".0",
        "changelog": f"https://github.com/sasou-web/Mira/blob/v{version}/CHANGELOG.md",
        "targetAbi": "10.9.0.0",
        "sourceUrl": f"{os.environ['BASE']}/{os.environ['ZIP']}",
        "checksum": os.environ["CHECKSUM"],
        "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }],
}], ensure_ascii=False, indent=2))
PY
rm -rf "$BUILD"
ls -la "$OUT"
