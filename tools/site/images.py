"""Images of the showcase site (site/img) from the README's screenshots: WebP at three or four widths. The tour's still
(site/img/film-still-*.webp, Piltover, given by ogsas) is the site's own. Elsewhere: the share cards
(tools/site/share.mjs), the party page (tools/site/party.mjs), the tour (tools/site/tour.py).

Run from the repository's root after gallery.yml has refreshed docs/screenshots/readme: python3 tools/site/images.py
"""
from pathlib import Path
from PIL import Image

SOURCE = Path("docs/screenshots/readme")
OUT = Path("site/img")
# The captures of the wall: two widths in a band (at most 544 points for Windows, 157 for an iPhone), a big one
# when opened; Mira for iPhone opens inside a phone. The share cards take windows-home-2400; party-*.webp (the friend's
# iPhone on the party page) comes from tools/site/party.mjs at the iPhone's widths.
WINDOWS, PHONE = (640, 1280, 2400), (390, 1080)
SHOWN = {f"windows-{n}": WINDOWS for n in ("home", "series", "films", "film", "subtitles")}
SHOWN |= {f"iphone-{n}": PHONE for n in ("home", "together", "series", "film", "movies")}

OUT.mkdir(parents=True, exist_ok=True)
for name, widths in SHOWN.items():
    image = Image.open(SOURCE / f"{name}.jpg").convert("RGB")
    for width in widths:
        copy = image.resize((width, round(image.height * width / image.width)), Image.LANCZOS)
        copy.save(OUT / f"{name}-{width}.webp", "WEBP", quality=80, method=6)

