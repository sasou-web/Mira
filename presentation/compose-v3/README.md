# Mira presentation, take 3

Pictures of the real apps (`presentation.yml`: `Mira.exe --demo-shots`, `MiraTour.testShots`), animated at 60 frames a
second here, cut on Nujabes' "Counting Stars" (not in the repository).

1. `python3 -I fix-home.py <shots-iphone> <Across the Spider-Verse backdrop> <out>`: the iPhone's Home banner, shot
   while its carousel was still sliding, rebuilt; the shade under the status bar taken off (`home-00-clean.jpg`).
2. `python3 -I stitch.py <shots-iphone> <out>`: each page's screens lined up in one strip, the tab bar cut out per
   screen, `pages.json`.
3. `compose.js` in `index.html` (Playwright, files served from `/tmp/claude-0` as `http://compose.local`):
   `node render.mjs stills <t,t> <dir>` or `node render.mjs video 60 <out.mp4> <from> <to>` (render in parts, concat,
   then mux the music).
