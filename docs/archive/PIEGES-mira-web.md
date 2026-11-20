# Pitfalls of Mira web (archived)

> Moved out of `docs/PIEGES.md` on 2026-11-20: they concern Mira web, retired on 2026-11-05, and its browser checks. Kept for history only.

- 12.1’s `PlaybackInfo` with `StartTimeTicks` (Jellyfin and test media): Mira web got neither a file nor a conversion there (“no playable stream”): asked again at full quality (`uncappedBitrate`), file as is, subtitles at `0`.
- HLS segments (Jellyfin and test media): hls.js (Mira web’s player) did not care about TS segments at 10 s, fMP4 at 0 or WebVTT at `MPEGTS:900000`; Apple’s player does.
- 12.1’s SyncPlay (Jellyfin and test media): In Chromium, `setOffline` does not close an open WebSocket: `check.mjs` cuts it with `routeWebSocket`. (Mira web’s browser checks.)
- SyncPlay, one request at a time: Mira web did it in `send` of `together.js`.
- SyncPlay Buffering: `check.mjs` delays a phone’s Buffering by 0.8 s. (Mira web’s browser checks, gone from `check.mjs`.)
- Tests (Jellyfin and test media): Safari may wait for a gesture (start from the “Play” button); Playwright’s Chromium without H.264 or AAC (WebM);
- Quick Connect: a 401 also ended Mira web’s session.
