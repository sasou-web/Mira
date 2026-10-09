#!/bin/bash
# Test media for Mira web's checks, made with ffmpeg in the layout Jellyfin reads:
#   WebM (VP9, Opus): played as they are by browsers without H.264 (Chromium as tests use it);
#   Matroska (H.264, AAC) with French subtitles beside it: converted by Jellyfin into HLS, the way an iPhone plays.
#   tools/web/media.sh <folder>
set -euo pipefail
ROOT="${1:?folder}"
q() { ffmpeg -loglevel error -y "$@"; }
mkdir -p "$ROOT/Films/Lueur Web (2020)" "$ROOT/Films/Aube Test (2021)" "$ROOT/Séries/Courte Web/Season 01" "$ROOT/Séries/Courte HLS/Season 01"

q -f lavfi -i testsrc2=duration=40:size=854x480:rate=24 -f lavfi -i sine=frequency=330:duration=40 \
  -c:v libvpx-vp9 -deadline realtime -cpu-used 8 -b:v 800k -c:a libopus -shortest "$ROOT/Films/Lueur Web (2020)/Lueur Web (2020).webm"
q -f lavfi -i testsrc2=duration=40:size=1280x720:rate=24 -f lavfi -i sine=frequency=440:duration=40 \
  -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -shortest "$ROOT/Films/Aube Test (2021)/Aube Test (2021).mkv"
cat > "$ROOT/Films/Aube Test (2021)/Aube Test (2021).fr.srt" <<'SRT'
1
00:00:01,000 --> 00:00:30,000
Bonjour depuis Mira web.
SRT
for e in 1 2 3; do
  q -f lavfi -i testsrc=duration=8:size=640x360:rate=24 -f lavfi -i "sine=frequency=$((300 + e * 100)):duration=8" \
    -c:v libvpx-vp9 -deadline realtime -cpu-used 8 -b:v 500k -c:a libopus -shortest "$ROOT/Séries/Courte Web/Season 01/Courte Web S01E0$e.webm"
done
for e in 1 2; do
  q -f lavfi -i testsrc=duration=8:size=640x360:rate=24 -f lavfi -i "sine=frequency=$((500 + e * 100)):duration=8" \
    -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -shortest "$ROOT/Séries/Courte HLS/Season 01/Courte HLS S01E0$e.mkv"
done
find "$ROOT" -type f | sort
