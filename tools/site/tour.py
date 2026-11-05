"""The showcase site's tour (site/video/tour.mp4), from Mira's
presentation, v3 (mira-presentation-v3-1080p60.mp4, 1920 x 1080 at 60 fps, 80.85 s; it is not in the repository).
Needs ffmpeg with libx264:

    python3 -I tools/site/tour.py <mira-presentation-v3-1080p60.mp4>

The site's tour has no sound: the presentation's music (Nujabes, "Counting Stars") is not ours to publish. Its end card
credits that music in a line at the bottom: that line is covered by the posters just above it, mirrored. One file,
1080p60 (15 MB): the screens it shows stay sharp on a wide screen; preload="none" fetches nothing until it plays.
The times and places below are those of v3: another presentation is refused until they are checked again.
"""
import hashlib, pathlib, subprocess, sys

DIGEST = '5974f11dc9149043d7137a6d5d891ec37c768c78fd410efc709e7b49e5ac05e1'  # SHA-256 of v3
END_CARD = 71.5  # seconds: the posters of the end card fill the screen, before its credit line fades in (74.4 s)
CREDIT = (1016, 28)  # first row and height of the band that holds the credit line (its text: rows 1023 to 1037)

root = pathlib.Path(__file__).resolve().parent.parent.parent
video = root / 'site/video'

if len(sys.argv) != 2:
    sys.exit(__doc__)
source = pathlib.Path(sys.argv[1])
digest = hashlib.sha256(source.read_bytes()).hexdigest()
if digest != DIGEST:
    sys.exit(f'{source.name}: not the presentation these times were measured on (SHA-256 {digest}).')

video.mkdir(parents=True, exist_ok=True)
y, height = CREDIT
cover = (f'[0:v]split=2[film][above];[above]crop=1920:{height}:0:{y - height},vflip,boxblur=2:1[patch];'
         f"[film][patch]overlay=0:{y}:enable='gte(t,{END_CARD})'")
# H.264 High, level 4.2 (1080p60): what every phone, tablet and computer decodes.
subprocess.run(['ffmpeg', '-v', 'error', '-y', '-i', str(source), '-filter_complex', cover + ',format=yuv420p[out]',
                '-map', '[out]', '-an', '-map_metadata', '-1', '-map_chapters', '-1', '-c:v', 'libx264', '-preset', 'slow',
                '-crf', '27', '-profile:v', 'high', '-level:v', '4.2', '-movflags', '+faststart', str(video / 'tour.mp4')],
               check=True)

print('site/video/tour.mp4 written.')
