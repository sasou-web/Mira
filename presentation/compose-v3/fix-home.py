# The iPhone's Home was shot while its carousel was still sliding: the top of the banner is rebuilt from the
# title's backdrop at the settled place, its shading taken from the shots.
#   python3 -I fix-home.py <raw shots> <backdrop> <out dir>
# Writes home-00.jpg (as the phone shows it), home-00-clean.jpg (without the shade under the status bar nor the
# status bar, for the strip) and status.png (the status bar's glyphs, from a screen where they are on black).
import sys
import numpy as np
from PIL import Image
src, backdrop, out = sys.argv[1:4]
# the shade Mira lays under the status bar (rows of 3x pixels -> what is left of the picture), measured on two shots
SHADE_Y = [5, 15, 25, 35, 45, 55, 65, 75, 85, 95, 105, 115, 125, 135, 145, 155, 165, 175, 185, 195, 205, 215, 225]
SHADE_F = [.25, .283, .322, .364, .398, .431, .462, .492, .541, .572, .606, .635, .657, .692, .727, .761, .799, .835, .87, .905, .943, .967, 1]
shade = np.interp(np.arange(2622), SHADE_Y, SHADE_F)
L = lambda n: np.asarray(Image.open(f'{src}/{n}.jpg').convert('RGB'), np.float32)
O, N, M = L('home-00'), L('home-01'), L('movies-00')
H, W = O.shape[:2]; dx = 193
Nc = N / shade[:, None, None]            # home-01 without its shade
bd = Image.open(backdrop).convert('RGB')
def render(Hd, ox, oy):
    Wd = round(1920 * Hd / 1080)
    im = np.asarray(bd.resize((Wd, Hd), Image.LANCZOS), np.float32)[:, ox:ox + W]
    canvas = np.zeros((H, W, 3), np.float32)
    y0 = max(0, oy); canvas[y0:Hd + oy] = im[y0 - oy:]
    return canvas
Bold = render(1448, 716, 0)       # where the sliding banner was
B = render(1444, 680, -3)         # where it settles
true = np.zeros_like(O); true[:, :W - dx] = O[:, dx:]
gain = np.ones((H, 3))
for y in range(150, 840):
    t = true[y]; b = Bold[y]; m = b.mean(1) > 25; m[W - dx - 4:] = False
    if m.sum() > 80: gain[y] = np.median(t[m] / np.maximum(b[m], 1), 0)
gain[150:240] /= shade[150:240, None]
gain[:150] = gain[150]
for c in range(3):
    g = gain[:840, c]; k = np.ones(41) / 41; gain[:840, c] = np.convolve(np.pad(g, 20, mode='edge'), k, 'valid')
cut = 826
def settled(y):
    b = B[y]; t = Nc[y - 704]; m = b.mean(1) > 25; m[90:900] = False
    return np.median(t[m] / np.maximum(b[m], 1), 0)
gs = np.mean([settled(y) for y in range(832, 870, 2)], 0)
corr = gs / gain[cut]
for y in range(cut + 1):
    k = np.clip((y - 450) / (cut - 450), 0, 1); k = k * k * (3 - 2 * k)
    gain[y] = gain[y] * (1 + (corr - 1) * k)
fill = B * gain[:, None, :]
# what is left of the step at the seam, column by column, faded in from above
ref = Nc[cut - 704 + 1:cut - 704 + 4].mean(0); have = fill[cut + 1:cut + 4].mean(0)
k = np.ones(81) / 81
ratio = np.stack([np.convolve(np.pad(ref[:, c], 40, mode='edge'), k, 'valid') /
                  np.maximum(np.convolve(np.pad(have[:, c], 40, mode='edge'), k, 'valid'), 1) for c in range(3)], 1)
ratio = np.clip(ratio, 0.5, 2)
for y in range(560, cut + 6):
    w = np.clip((y - 560) / (cut - 560), 0, 1); w = w * w * (3 - 2 * w)
    fill[y] *= 1 + (ratio - 1) * w
R = O.copy()
R[:cut] = fill[:cut]
R[cut:1460] = Nc[cut - 704:1460 - 704]
for i in range(6):
    y = cut + i; a = (i + 1) / 7; R[y] = Nc[y - 704] * a + fill[y] * (1 - a)
# below the banner, home-00 itself, without its own shade (only the banner reaches up there)
Image.fromarray(np.clip(R, 0, 255).astype(np.uint8)).save(f'{out}/home-00-clean.jpg', quality=95)
S = M[:150]; alpha = S.max(2) / 255.0
rgba = np.zeros((150, W, 4), np.float32)
rgba[..., :3] = S / np.maximum(alpha, 1 / 255)[:, :, None]; rgba[..., 3] = alpha * 255
Image.fromarray(np.clip(rgba, 0, 255).astype(np.uint8), 'RGBA').save(f'{out}/status.png')
R = R * shade[:, None, None]
R[:150] = S + (1 - alpha[:, :, None]) * R[:150]
Image.fromarray(np.clip(R, 0, 255).astype(np.uint8)).save(f'{out}/home-00.jpg', quality=95)
