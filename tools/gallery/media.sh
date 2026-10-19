#!/bin/bash
# The library of the README's captures: free films (the Blender Foundation's open movies, Creative Commons) and films
# in the public domain, named as Jellyfin matches them on TMDb, which gives their real posters, backdrops and
# summaries. The videos are black with silent audio, at each film's real length: only their pages are captured.
#   tools/gallery/media.sh <folder>
set -euo pipefail
ROOT="${1:?folder}"
q() { ffmpeg -loglevel error -y "$@"; }
work="$(mktemp -d)"
# Ten minutes of 1080p video and stereo AAC, looped (copied, never encoded again) to each film's length.
q -f lavfi -i color=c=black:s=1920x1080:r=1 -f lavfi -i anullsrc=r=48000:cl=stereo -t 600 \
  -c:v libx264 -preset ultrafast -tune stillimage -crf 51 -g 600 -pix_fmt yuv420p -c:a aac -b:a 32k "$work/master.mkv"
film() { # <library folder> <name as Jellyfin reads it> <minutes>
  mkdir -p "$ROOT/$1/$2"
  q -stream_loop -1 -i "$work/master.mkv" -t $(($3 * 60)) -c copy -metadata:s:a:0 language=eng "$ROOT/$1/$2/$2.mkv"
}
film Films "Big Buck Bunny (2008)" 10
film Films "Elephants Dream (2006)" 11
film Films "Sintel (2010)" 15
film Films "Tears of Steel (2012)" 12
film Films "Cosmos Laundromat (2015)" 12
film Films "Agent 327 - Operation Barbershop (2017)" 4
film Films "Spring (2019)" 8
film Films "Coffee Run (2020)" 3
film Films "Sprite Fright (2021)" 10
film Films "Charge (2022)" 4
film Films "Wing It! (2023)" 4
film Films "Sita Sings the Blues (2008)" 82
film Films "A Trip to the Moon (1902)" 13
film Films "The Cabinet of Dr. Caligari (1920)" 77
film Films "The Kid (1921)" 68
film Films "Nosferatu (1922)" 94
film Films "Sherlock Jr. (1924)" 45
film Films "The Phantom of the Opera (1925)" 93
film Films "The General (1926)" 78
film Films "Metropolis (1927)" 148
film Films "His Girl Friday (1940)" 92
film Films "Detour (1945)" 68
film Films "Plan 9 from Outer Space (1957)" 79
film Films "Charade (1963)" 113
film Films "Night of the Living Dead (1968)" 96
# A series under a Creative Commons licence (Pioneer One, 2010), for a series' page and its episodes.
mkdir -p "$ROOT/Séries/Pioneer One (2010)/Season 01"
for e in 1 2 3 4 5 6; do
  q -stream_loop -1 -i "$work/master.mkv" -t $((32 * 60)) -c copy "$ROOT/Séries/Pioneer One (2010)/Season 01/Pioneer One S01E0$e.mkv"
done
rm -rf "$work"
