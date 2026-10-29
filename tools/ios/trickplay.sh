#!/bin/sh
# Trickplay pictures for the stand-in Jellyfin of the iPhone's tests (mock-jellyfin.mjs), as Jellyfin makes them: a
# thumbnail every 2 s, 10 × 10 to a tile image, and the TrickplayInfo that describes them. By ffmpeg (FFMPEG, else the
# one on the PATH): AVFoundation cannot decode a video on the macOS runner, which has no video decoder.
#   tools/ios/trickplay.sh <video> <folder> [width]
set -eu
video=$1 folder=$2 width=${3:-320}
ffmpeg=${FFMPEG:-ffmpeg}
frames=$(mktemp -d)
trap 'rm -rf "$frames"' EXIT
"$ffmpeg" -loglevel error -i "$video" -vf "fps=1/2,scale=$width:-2" -q:v 3 "$frames/%04d.jpg"
count=$(find "$frames" -name '*.jpg' | wc -l | tr -d ' ')
# « … precision 8, 320x180, components 3 »: the last size that file(1) gives is the picture's.
size=$(file -b "$frames/0001.jpg" | grep -oE '[0-9]+x[0-9]+' | tail -1)
height=${size#*x}
mkdir -p "$folder/$width"
"$ffmpeg" -loglevel error -framerate 1 -i "$frames/%04d.jpg" -vf tile=10x10 -q:v 4 -start_number 0 "$folder/$width/%d.jpg"
printf '{"Width":%s,"Height":%s,"TileWidth":10,"TileHeight":10,"ThumbnailCount":%s,"Interval":2000,"Bandwidth":0}\n' \
  "$width" "$height" "$count" > "$folder/info.json"
echo "$count thumbnails of $width x $height, $(find "$folder/$width" -name '*.jpg' | wc -l | tr -d ' ') tile(s)"
