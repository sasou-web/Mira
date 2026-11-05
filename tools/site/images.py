"""Images of the showcase site (site/img) from the README's screenshots: WebP at three or four widths. The tour's still
(site/img/film-still-*.webp, Piltover, given by ogsas) is the site's own. Elsewhere: the share cards
(tools/site/share.mjs), the party page (tools/site/party.mjs), the tour (tools/site/tour.py).

Run from the repository's root after gallery.yml has refreshed docs/screenshots/readme: python3 tools/site/images.py
"""
from pathlib import Path
from PIL import Image

SOURCE = Path("docs/screenshots/readme")
OUT = Path("site/img")
# The captures the sentence names: small in the line, at the capture's own width when opened (1440 points at twice their
# pixels); Mira for iPhone opens inside a phone. The share cards take windows-home-2400.
WINDOWS, PHONE = (960, 1600, 2400, 2880), (390, 720, 1080)
SHOWN = {"windows-home": WINDOWS, "windows-series": WINDOWS, "windows-subtitles": WINDOWS, "iphone-home": PHONE}

OUT.mkdir(parents=True, exist_ok=True)
for name, widths in SHOWN.items():
    image = Image.open(SOURCE / f"{name}.jpg").convert("RGB")
    for width in widths:
        copy = image.resize((width, round(image.height * width / image.width)), Image.LANCZOS)
        copy.save(OUT / f"{name}-{width}.webp", "WEBP", quality=80, method=6)

