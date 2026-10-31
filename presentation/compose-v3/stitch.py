# The iPhone's pages joined: each screen after a slow drag lined up on the one before (the shift that makes their
# content match), the content laid on one tall strip; fixed bars (status, title bar, tab bar) left to the screens.
#   python3 -I stitch.py <shots dir> <out dir>
import json, os, re, sys
import numpy as np
from scipy import ndimage
from PIL import Image, ImageDraw
src, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
files = sorted(f for f in os.listdir(src) if f.endswith('.jpg'))
pages = {}
for f in files:
    m = re.fullmatch(r'([a-z]+)-(\d\d)\.jpg', f)
    if m: pages.setdefault(m.group(1), []).append((int(m.group(2)), f))
TOP = {'home': 230, 'movies': 430, 'default': 340}
SKIP = {'search'}
# the screens shot with the tab bar folded (seen on the shots)
FOLDED = {'home': {1, 2, 3}, 'arcane': {1, 2, 3, 4, 5}, 'movies': {1, 2, 3, 4, 5, 6}}
BOT = 360
result = {}
for name, shots in pages.items():
    if name in SKIP: continue
    shots.sort()
    ims = [np.asarray(Image.open(os.path.join(src, f)).convert('RGB')) for _, f in shots]
    clean = os.path.join(out, f'{name}-00-clean.jpg')   # a first screen mended (fix-home.py)
    if os.path.exists(clean): ims[0] = np.asarray(Image.open(clean).convert('RGB'))
    h, w = ims[0].shape[:2]
    if h < w: continue
    top = TOP.get(name, TOP['default'])
    gray = [im.astype(np.float32).mean(2)[:, ::6] for im in ims]
    offsets = [0]
    L = 540
    for a, b in zip(gray, gray[1:]):
        # a band just under the fixed top of the later screen, found again lower in the earlier one
        y0 = top + 60
        band = b[y0: y0 + L]
        best, bd = None, 0
        for d in range(0, h - BOT - y0 - L, 2):
            err = np.abs(a[y0 + d: y0 + d + L] - band).mean()
            if best is None or err < best: best, bd = err, d
        for d in range(max(0, bd - 2), bd + 3):
            if y0 + d + L > h - BOT: continue
            err = np.abs(a[y0 + d: y0 + d + L] - band).mean()
            if err < best: best, bd = err, d
        offsets.append(offsets[-1] + bd)
        print(name, len(offsets) - 1, 'shift', bd, 'err', round(float(best), 2))
    total = offsets[-1] + h
    strip = np.zeros((total, w, 3), np.uint8); filled = 0
    for i, (im, o) in enumerate(zip(ims, offsets)):
        a0 = 0 if i == 0 else max(top, filled - o)
        a1 = h - BOT if i < len(ims) - 1 else h
        if a1 > a0: strip[o + a0: o + a1] = im[a0:a1]; filled = max(filled, o + a1)
    Image.fromarray(strip).save(os.path.join(out, f'{name}-strip.jpg'), quality=93)
    # The tab bar of each screen alone, cut out along its pills (alpha), so the strip shows around them: Mira's bar
    # is spread out (all tabs) at rest and folded (the open tab) while a page scrolls.
    BAR = 786 * 3
    def bar_mask(spread):
        m = Image.new('L', (w * 2, (h - BAR) * 2), 0); d = ImageDraw.Draw(m)
        y0, y1 = (2370 - BAR) * 2, (2548 - BAR) * 2
        d.rounded_rectangle((60 * 2, y0, (876 if spread else 428) * 2, y1), radius=(y1 - y0) // 2, fill=255)
        d.rounded_rectangle((970 * 2, y0, 1146 * 2, y1), radius=62 * 2, fill=255)
        m = m.resize((w, h - BAR), Image.LANCZOS)
        return ndimage.gaussian_filter(np.asarray(m, np.float32), 1.2)
    folded = FOLDED.get(name, set())
    bars = []
    for i, im in enumerate(ims):
        alpha = bar_mask(i not in folded)
        rgba = np.dstack([im[BAR:], alpha.clip(0, 255).astype(np.uint8)])
        f = f'{name}-bar-{i:02d}.png'; Image.fromarray(rgba, 'RGBA').save(os.path.join(out, f)); bars.append(f)
    result[name] = {'shots': [f for _, f in shots], 'offsets': [o / 3 for o in offsets], 'height': total / 3, 'top': top / 3, 'bottom': BOT / 3, 'strip': f'{name}-strip.jpg', 'bars': bars, 'barTop': 786}
json.dump(result, open(os.path.join(out, 'pages.json'), 'w'), indent=1)
