"""The showcase site's tour (site/video/tour.mp4), from Mira's presentation, v4 (mira-presentation-v4-1080p60.mp4,
1920 x 1080 at 60 fps, 80.85 s, 4851 frames; it is not in the repository). Needs ffmpeg with libx264:

    python3 -I tools/site/tour.py <mira-presentation-v4-1080p60.mp4> <end.mp4>

The site's tour has no sound: the presentation's music (Nujabes, "Counting Stars") is not ours to publish. Its end
card credits that music in its small print, on the same line as Spring's CC BY credit, which must stay: the end is
rendered again from the presentation's sources with that line cut to "Spring and Sprite Fright © Blender Foundation,
CC BY 4.0." (compose/film4.js), from 78.3 s, before the small print fades in at 78.32 s (beat 124):

    PRESET=veryfast CRF=10 QUALITY=0.97 node render.mjs video 60 end.mp4 78.3 80.85

and laid over the presentation from frame 4698 on (the frames before are the same). One file, 1080p60 (20 MB): the
screens it shows stay sharp on a wide screen; the page fetches nothing until the tour is opened.
The frame and times below are those of v4: another presentation is refused until they are checked again.
"""
import hashlib, json, pathlib, subprocess, sys

DIGEST = '976b94dcf2c730eb29cee3645272b7ec2ebf3ea51c6495a36a56e199bcaf13e1'  # SHA-256 of v4
SPLICE = 4698  # first frame (78.3 s at 60 fps) taken from the end rendered again
FRAMES = 4851  # 80.85 s at 60 fps

root = pathlib.Path(__file__).resolve().parent.parent.parent
video = root / 'site/video'


def frames(path):
    out = subprocess.run(['ffprobe', '-v', 'error', '-select_streams', 'v:0', '-count_frames', '-show_entries',
                          'stream=nb_read_frames,width,height,r_frame_rate', '-of', 'json', str(path)],
                         check=True, capture_output=True, text=True).stdout
    s = json.loads(out)['streams'][0]
    return int(s['nb_read_frames']), (s['width'], s['height']), s['r_frame_rate']


if len(sys.argv) != 3:
    sys.exit(__doc__)
source, end = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
digest = hashlib.sha256(source.read_bytes()).hexdigest()
if digest != DIGEST:
    sys.exit(f'{source.name}: not the presentation these frames were measured on (SHA-256 {digest}).')
n, size, rate = frames(end)
if (n, size, rate) != (FRAMES - SPLICE, (1920, 1080), '60/1'):
    sys.exit(f'{end.name}: {n} frames of {size[0]} x {size[1]} at {rate}, not the {FRAMES - SPLICE} frames of '
             '1920 x 1080 at 60 fps from 78.3 s to 80.85 s.')

video.mkdir(parents=True, exist_ok=True)
join = (f'[0:v]trim=end_frame={SPLICE},setpts=PTS-STARTPTS[film];[1:v]setpts=PTS-STARTPTS[end];'
        '[film][end]concat=n=2:v=1,format=yuv420p[out]')
# H.264 High, level 4.2 (1080p60): what every phone, tablet and computer decodes.
subprocess.run(['ffmpeg', '-v', 'error', '-y', '-i', str(source), '-i', str(end), '-filter_complex', join,
                '-map', '[out]', '-an', '-map_metadata', '-1', '-map_chapters', '-1', '-c:v', 'libx264', '-preset', 'slow',
                '-crf', '27', '-profile:v', 'high', '-level:v', '4.2', '-movflags', '+faststart', str(video / 'tour.mp4')],
               check=True)
n, _, _ = frames(video / 'tour.mp4')
if n != FRAMES:
    sys.exit(f'site/video/tour.mp4: {n} frames, not {FRAMES}.')
print('site/video/tour.mp4 written.')
