"""Images of the showcase site (site/img) from the README's screenshots and the featured film's still (film-still.jpg):
WebP at three widths (the share cards: tools/site/share.mjs).

Run from the repository's root after gallery.yml has refreshed docs/screenshots/readme: python3 tools/site/images.py
"""
from pathlib import Path
from PIL import Image

SOURCE = Path("docs/screenshots/readme")
OUT = Path("site/img")
WIDTHS = {"windows": (960, 1600, 2400), "web": (390, 720, 1080), "film": (640, 1280)}

OUT.mkdir(parents=True, exist_ok=True)
for path in sorted(SOURCE.glob("*.jpg")):
    image = Image.open(path).convert("RGB")
    for width in WIDTHS[path.stem.split("-")[0]]:
        copy = image.resize((width, round(image.height * width / image.width)), Image.LANCZOS)
        copy.save(OUT / f"{path.stem}-{width}.webp", "WEBP", quality=80, method=6)
