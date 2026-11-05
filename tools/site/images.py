"""Images of the showcase site (site/img) from the README's screenshots: WebP at three or four widths. The opening's still
(site/img/film-still-*.webp, Piltover, given by ogsas) is the site's own; its
cinema-wide band (film-scope-*.webp, 2.39:1) is cut here from the widest one. Elsewhere: the share cards
(tools/site/share.mjs), the party page (tools/site/party.mjs), the tour and its still of Spring (tools/site/tour.py).

Run from the repository's root after gallery.yml has refreshed docs/screenshots/readme: python3 tools/site/images.py
"""
from pathlib import Path
from PIL import Image

SOURCE = Path("docs/screenshots/readme")
OUT = Path("site/img")
# The captures the pages show. Mira for Windows goes edge to edge: up to the capture's own width (1440 points at twice
# their pixels); Mira for iPhone is inside a phone.
WINDOWS, PHONE = (960, 1600, 2400, 2880), (390, 720, 1080)
SHOWN = {"windows-home": WINDOWS, "windows-series": WINDOWS, "windows-subtitles": WINDOWS,
         "iphone-home": PHONE, "iphone-film": PHONE, "iphone-movies": PHONE, "iphone-series": PHONE, "iphone-together": PHONE}
# The opening's band: the rows of film-still-2000.webp (2000 x 1125) kept at 2.39:1, the towers and the light of the sky.
SCOPE = (150, 987)

OUT.mkdir(parents=True, exist_ok=True)
for name, widths in SHOWN.items():
    image = Image.open(SOURCE / f"{name}.jpg").convert("RGB")
    for width in widths:
        copy = image.resize((width, round(image.height * width / image.width)), Image.LANCZOS)
        copy.save(OUT / f"{name}-{width}.webp", "WEBP", quality=80, method=6)

still = Image.open(OUT / "film-still-2000.webp").convert("RGB").crop((0, SCOPE[0], 2000, SCOPE[1]))
for width in (960, 1440, 2000):
    copy = still.resize((width, round(still.height * width / still.width)), Image.LANCZOS) if width < still.width else still
    copy.save(OUT / f"film-scope-{width}.webp", "WEBP", quality=82, method=6)
