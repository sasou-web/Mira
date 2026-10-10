#!/bin/bash
# Test media for Mira web's checks, made with ffmpeg in the layout Jellyfin reads:
#   WebM (VP9, Opus): played as they are by browsers without H.264 (Chromium as tests use it); the film has French
#   and English audio tracks and subtitles in both languages beside it, for the choice of tracks;
#   Matroska (H.264, AAC) with French subtitles beside it: converted by Jellyfin into HLS, the way an iPhone plays;
#   an anime-like Matroska with its ASS and SRT subtitles inside it;
#   with « formats », the kinds of files a home library holds whose stream Apple's player may refuse (see below).
#   tools/web/media.sh <folder> [formats]
set -euo pipefail
ROOT="${1:?folder}"
q() { ffmpeg -loglevel error -y "$@"; }
mkdir -p "$ROOT/Films/Lueur Web (2020)" "$ROOT/Films/Aube Test (2021)" "$ROOT/Séries/Courte Web/Season 01" "$ROOT/Séries/Courte HLS/Season 01"

q -f lavfi -i testsrc2=duration=40:size=854x480:rate=24 -f lavfi -i sine=frequency=330:duration=40 -f lavfi -i sine=frequency=550:duration=40 \
  -map 0:v -map 1:a -map 2:a -metadata:s:a:0 language=fre -metadata:s:a:1 language=eng -disposition:a:0 default -disposition:a:1 0 \
  -c:v libvpx-vp9 -deadline realtime -cpu-used 8 -b:v 800k -c:a libopus -shortest "$ROOT/Films/Lueur Web (2020)/Lueur Web (2020).webm"
for lang in fr en; do
  printf '1\n00:00:01,000 --> 00:00:35,000\n%s\n' "$([ $lang = fr ] && echo 'Lueur en VF.' || echo 'Glow in English.')" > "$ROOT/Films/Lueur Web (2020)/Lueur Web (2020).$lang.srt"
done
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
# An anime the way they come: H.264 and Japanese sound in Matroska, its subtitles inside it. ASS in French (the
# dialogue, by default), ASS signs in French (forced), SRT in English, SRT with no language. Jellyfin never converts
# ASS by profile: Mira asks for it as WebVTT, beside the video or in the HLS stream of Apple's player. Lines at 5-8 s
# and 35-38 s, in the first and the second 30 s subtitle segment of Jellyfin's HLS stream. No -shortest here: the
# shortest stream would be the signs, 3 s long.
SIGNS="$ROOT/Films/Signes Test (2022)"
mkdir -p "$SIGNS"
TMP="$(mktemp -d)"
ass() {
  printf '[Script Info]\nScriptType: v4.00+\nPlayResX: 640\nPlayResY: 360\n\n[V4+ Styles]\n'
  printf 'Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n'
  printf 'Style: Default,Arial,24,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1\n\n[Events]\n'
  printf 'Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n'
  for line in "$@"; do printf 'Dialogue: 0,%s\n' "$line"; done
}
ass '0:00:05.00,0:00:08.00,Default,,0,0,0,,{\i1}Cinq secondes{\i0}' '0:00:35.00,0:00:38.00,Default,,0,0,0,,Trente-cinq' > "$TMP/dialogue.ass"
ass '0:00:01.00,0:00:03.00,Default,,0,0,0,,PANNEAU' > "$TMP/signs.ass"
printf '1\n00:00:05,000 --> 00:00:08,000\nFive seconds\n\n2\n00:00:35,000 --> 00:00:38,000\nThirty-five\n' > "$TMP/english.srt"
printf '1\n00:00:10,000 --> 00:00:12,000\nSans langue\n' > "$TMP/unknown.srt"
q -f lavfi -i testsrc2=duration=45:size=640x360:rate=24 -f lavfi -i sine=frequency=660:duration=45 \
  -i "$TMP/dialogue.ass" -i "$TMP/signs.ass" -i "$TMP/english.srt" -i "$TMP/unknown.srt" \
  -map 0:v -map 1:a -map 2 -map 3 -map 4 -map 5 -metadata:s:a:0 language=jpn \
  -metadata:s:s:0 language=fre -metadata:s:s:1 language=fre -metadata:s:s:2 language=eng \
  -disposition:s:0 default -disposition:s:1 forced -disposition:s:2 0 -disposition:s:3 0 \
  -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -c:s:0 copy -c:s:1 copy -c:s:2 srt -c:s:3 srt -t 45 "$SIGNS/Signes Test (2022).mkv"
rm -rf "$TMP"

# Files Jellyfin sends to Apple's player in ways it may refuse: the video copied out of an MP4 (Jellyfin lists 6 s
# segments, ffmpeg cuts at keyframes), interlaced video copied as it is, HEVC and x264 open GOPs (segments that do not
# start on an IDR), HDR10, an MPEG-2 TS whose clock starts at 50000 s, HEVC tagged hev1 in an MP4 played directly, and
# a long Matroska with irregular keyframes, resumed far from its start. Read by tools/web/safari.mjs.
if [ "${2:-}" = formats ]; then
  film() { mkdir -p "$ROOT/Films/$1 (2023)"; echo "$ROOT/Films/$1 (2023)/$1 (2023).$2"; }
  # Keyframes 2 to 9 s apart, as in an encode with scene cuts; never on Jellyfin's 6 s grid.
  irregular=(-x264-params keyint=600:min-keyint=24:scenecut=0 -force_key_frames 'expr:gte(t,n_forced*5.3+mod(n_forced*n_forced,4))')
  q -f lavfi -i testsrc2=duration=60:size=640x360:rate=24 -f lavfi -i sine=frequency=440:duration=60 \
    -c:v libx264 -preset veryfast -pix_fmt yuv420p -bf 3 "${irregular[@]}" -af 'pan=5.1|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0' \
    -c:a dca -strict -2 -ar 48000 "$(film 'Format MP4 DTS' mp4)"
  q -f lavfi -i testsrc2=duration=20:size=720x576:rate=50 -f lavfi -i sine=frequency=520:duration=20 \
    -vf 'tinterlace=interleave_top,setfield=tff' -c:v libx264 -preset veryfast -pix_fmt yuv420p -flags +ildct+ilme -x264-params interlaced=1:tff=1 \
    -c:a ac3 -ar 48000 "$(film 'Format Entrelace' ts)"
  q -f lavfi -i testsrc2=duration=40:size=640x360:rate=24 -f lavfi -i sine=frequency=600:duration=40 \
    -c:v libx265 -preset ultrafast -pix_fmt yuv420p -c:a aac "$(film 'Format HEVC' mkv)"
  q -f lavfi -i testsrc2=duration=40:size=640x360:rate=24 -f lavfi -i sine=frequency=640:duration=40 \
    -c:v libx264 -preset veryfast -pix_fmt yuv420p -x264-params open-gop=1:keyint=120:min-keyint=24:scenecut=40 -c:a aac "$(film 'Format Open GOP' mkv)"
  q -f lavfi -i testsrc2=duration=20:size=640x360:rate=24 -f lavfi -i sine=frequency=700:duration=20 \
    -c:v libx265 -preset ultrafast -pix_fmt yuv420p10le -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc \
    -x265-params 'hdr10=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400' \
    -af 'pan=5.1|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0' -c:a eac3 -ar 48000 "$(film 'Format HDR10' mkv)"
  ts="$(film 'Format TS decale' ts)"
  q -f lavfi -i testsrc2=duration=30:size=720x576:rate=25 -f lavfi -i sine=frequency=760:duration=30 \
    -c:v mpeg2video -b:v 2M -c:a mp2 -output_ts_offset 50000 "$ts"
  printf '1\n00:00:02,000 --> 00:00:20,000\nSous-titres du TS.\n' > "${ts%.ts}.fr.srt"
  q -f lavfi -i testsrc2=duration=20:size=640x360:rate=24 -f lavfi -i sine=frequency=820:duration=20 \
    -c:v libx265 -preset ultrafast -pix_fmt yuv420p -tag:v hev1 -c:a aac -movflags +faststart "$(film 'Format hev1' mp4)"
  q -f lavfi -i testsrc2=duration=240:size=640x360:rate=24 -f lavfi -i sine=frequency=880:duration=240 \
    -c:v libx264 -preset ultrafast -pix_fmt yuv420p "${irregular[@]}" -c:a aac "$(film 'Format Reprise' mkv)"
fi
find "$ROOT" -type f | sort
