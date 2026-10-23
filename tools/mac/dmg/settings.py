# Mira's disk image window, for dmgbuild (tools/mac/package.sh): the picture of background.png (and its @2x, which
# dmgbuild joins into one TIFF for Retina screens), Mira and Applications where its arrow expects them, no toolbar,
# sidebar nor status bar, and Mira's icon on the volume. dmgbuild writes the window's .DS_Store itself: no Finder and
# no AppleScript, which a Mac without a session (GitHub's) may refuse.
#   dmgbuild -s tools/mac/dmg/settings.py -D dir=tools/mac/dmg -D app=<Mira.app> -D icon=<Mira.icns> "Mira x.y.z" <file.dmg>
# dmgbuild runs this file with `defines` (the -D values) in its globals, and without __file__: hence dir.
import json
import os.path

here = defines["dir"]  # noqa: F821 (given by dmgbuild)
with open(os.path.join(here, "layout.json")) as f:
    layout = json.load(f)
app = defines["app"]  # noqa: F821

format = "UDZO"
files = [app]
symlinks = {"Applications": "/Applications"}
# No hide_extensions: SetFile would mark Mira.app with Finder information, which its signature refuses (codesign
# --strict: « detritus not allowed »); Finder hides « .app » by itself.
icon = defines["icon"]  # noqa: F821
background = os.path.join(here, "background.png")

# Finder places the window from the top left corner of the screen, and counts its title bar (28 points on macOS 15) in
# its height: the picture fills what is left.
window_rect = ((200, 140), (layout["width"], layout["height"] + 28))
default_view = "icon-view"
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False
arrange_by = None
show_icon_preview = False
show_item_info = False
label_pos = "bottom"
icon_size = layout["iconSize"]
text_size = layout["textSize"]
icon_locations = {"Mira.app": tuple(layout["app"]), "Applications": tuple(layout["applications"])}
