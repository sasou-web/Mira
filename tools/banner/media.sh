#!/bin/bash
# The library of the release banners, the one of the README's captures: animated films and series, named as Jellyfin
# matches them on TMDb, which gives their real posters, backdrops and summaries (© their rights holders, as in the
# README). The videos are black with silent audio, at each title's real length: only their pages are captured.
#   tools/banner/media.sh <folder>
set -euo pipefail
ROOT="${1:?folder}"
q() { ffmpeg -loglevel error -y "$@"; }
work="$(mktemp -d)"
# Ten minutes of 1080p video and stereo AAC, looped (copied, never encoded again) to each title's length.
q -f lavfi -i color=c=black:s=1920x1080:r=1 -f lavfi -i anullsrc=r=48000:cl=stereo -t 600 \
  -c:v libx264 -preset ultrafast -tune stillimage -crf 51 -g 600 -pix_fmt yuv420p -c:a aac -b:a 32k "$work/master.mkv"
video() { # <file> <minutes>
  mkdir -p "$(dirname "$1")"
  q -stream_loop -1 -i "$work/master.mkv" -t $(($2 * 60)) -c copy -metadata:s:a:0 language=eng "$1"
}
film() { video "$ROOT/Films/$1/$1.mkv" "$2"; } # <name as Jellyfin reads it> <minutes>
series() { # <name> <season> <episodes> <minutes>
  local short="${1% (*}"
  for e in $(seq 1 "$3"); do video "$ROOT/Séries/$1/Season $(printf %02d "$2")/$short S$(printf %02d "$2")E$(printf %02d "$e").mkv" "$4"; done
}
film "Your Name. (2016)" 106
film "Spider-Man Into the Spider-Verse (2018)" 117
film "Spider-Man Across the Spider-Verse (2023)" 140
film "Princess Mononoke (1997)" 134
film "Teenage Mutant Ninja Turtles Mutant Mayhem (2023)" 99
film "Nimona (2023)" 101
film "Robot Dreams (2023)" 102
film "Isle of Dogs (2018)" 101
film "The Mitchells vs. the Machines (2021)" 114
film "Spirited Away (2001)" 125
film "The Wild Robot (2024)" 102
film "Wolfwalkers (2020)" 103
film "Klaus (2019)" 97
film "Puss in Boots The Last Wish (2022)" 102
film "Coco (2017)" 105
film "Howl's Moving Castle (2004)" 119
film "My Neighbor Totoro (1988)" 86
film "The Boy and the Heron (2023)" 124
film "Flow (2024)" 85
film "Suzume (2022)" 122
series "Arcane (2021)" 1 9 40
series "Arcane (2021)" 2 9 40
series "Invincible (2021)" 1 3 45
series "Blue Eye Samurai (2023)" 1 3 45
series "Love, Death & Robots (2019)" 1 3 15
rm -rf "$work"
