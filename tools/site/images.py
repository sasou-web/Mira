"""Images of the showcase site (site/img) from a folder of captures of Mira: WebP at the widths the page asks for, and
the wall of posters behind the title (« Le vidéoclub »).

  python3 -I tools/site/images.py [captures]

[captures] (by default docs/screenshots/readme) is searched in depth for each capture by name, PNG or JPEG
(windows-home.png, iphone-home.jpg…), and for the posters of the demo library (poster-<title>.jpg, the artwork/ folder
of a gallery run): each becomes site/img/p/<title>.webp, and the wall is written again between <!-- wall --> and
<!-- /wall --> in site/index.html and site/fr/index.html. Without posters, the wall is left as it is.
The tour's still (site/img/film-still-*.webp) and the tour are the tour's own (tools/site/tour.py); the share cards
come after, from these images (tools/site/share.mjs).
"""
import re
import sys
from math import gcd
from pathlib import Path
from PIL import Image, ImageOps

SOURCE = Path(sys.argv[1] if len(sys.argv) > 1 else "docs/screenshots/readme")
OUT = Path("site/img")
# The window that comes out of the wall is as wide as a 4K screen at 175 % can show it (1840 points × 1.75 > 2880,
# the capture's own width); the two windows side by side and the iPhones are smaller. The share cards take
# windows-home-2000.
SHOWN = {
    "windows-home": (1280, 2000, 2880),
    "windows-film": (640, 1280, 2400),
    "windows-player": (640, 1280, 2400),
}
SHOWN |= {f"iphone-{n}": (390, 1080) for n in ("home", "movies", "series", "together")}
# A poster of the wall is at most ~230 points wide on a large screen, under a dark veil.
POSTER = (360, 540)
# The wall: 14 columns on a computer, 7 on a phone (site.css); 84 posters fill both.
WALL = 84


def find(name):
    for ext in ("png", "jpg", "jpeg"):
        found = sorted(SOURCE.rglob(f"{name}.{ext}"))
        if found:
            return found[0]
    raise SystemExit(f"{name}: no capture in {SOURCE}")


OUT.mkdir(parents=True, exist_ok=True)
for name, widths in SHOWN.items():
    image = Image.open(find(name)).convert("RGB")
    for width in widths:
        copy = image.resize((width, round(image.height * width / image.width)), Image.LANCZOS)
        copy.save(OUT / f"{name}-{width}.webp", "WEBP", quality=80, method=6)

posters = sorted(SOURCE.rglob("poster-*.jpg"))
if posters:
    shelf = OUT / "p"
    shelf.mkdir(exist_ok=True)
    for old in shelf.glob("*.webp"):
        old.unlink()
    titles = []
    for poster in posters:
        title = poster.stem.removeprefix("poster-")
        image = ImageOps.fit(Image.open(poster).convert("RGB"), POSTER, Image.LANCZOS)
        image.save(shelf / f"{title}.webp", "WEBP", quality=72, method=6)
        titles.append(title)
    # An order where no two neighbours, side by side or one above the other (14 or 7 columns), are the same poster:
    # a step prime with the number of posters, that neither 7 nor 14 steps bring back to the start.
    count = len(titles)
    step = next(s for s in range(5, count) if gcd(s, count) == 1 and (7 * s) % count and (14 * s) % count
                and min((7 * s) % count, count - (7 * s) % count) > 3)
    wall = [titles[(i * step + 7) % count] for i in range(WALL)]
    for page, base in (("site/index.html", "img"), ("site/fr/index.html", "../img")):
        path = Path(page)
        text = path.read_text(encoding="utf-8")
        images = "".join(f'<img src="{base}/p/{t}.webp" alt="" width="360" height="540" decoding="async" fetchpriority="low">'
                         for t in wall)
        after, n = re.subn(r"<!-- wall -->.*?<!-- /wall -->", f"<!-- wall -->{images}<!-- /wall -->", text, flags=re.S)
        if n != 1:
            raise SystemExit(f"{page}: no <!-- wall --> … <!-- /wall -->")
        path.write_text(after, encoding="utf-8")
    print(f"{count} posters, wall of {WALL} written")
