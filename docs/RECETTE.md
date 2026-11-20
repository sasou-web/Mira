# Real-device checklist

What CI can’t prove: each box is a feature that has never run on a real device. Tick it once you’ve seen it work; if it fails, say so in the thread with a screenshot (and, once it exists, the report from Settings → “Export a report”). Each failure becomes a PR. No need to do everything at once.

Key: 🖥 your PC · 🗄 Jellyfin’s PC · 📱 iPhone · 🍎 a friend’s Mac · 👥 with Sam

## Jellyfin’s PC 🗄
- [ ] Settings → Security → “Your server”: on the Dell, “Your home and Tailscale only” and “Private networks only”; if a line says “Too open”, “Close it…” turns it green (public Wi-Fi: one Windows permission prompt), the phone still reaches Jellyfin at home and over 4G through Tailscale, a party by link still works
- [ ] Install Jellyfin on a new PC: “Install” greyed out under 10 characters, green gauge beyond
- [ ] “Set everything up”: a single Windows permission prompt, then every step of the phone card ticked
- [ ] After “Set everything up”: the phone reaches Jellyfin at home (Wi-Fi) and over 4G through Tailscale
- [ ] Notice “Jellyfin now converts with your …” (Intel card), then a conversion that doesn’t stutter
- [ ] Jellyfin backup made on its own (Settings → Jellyfin server: date of the last one)
- [ ] Jellyfin update in one click (when a version comes out): service back, Jellyfin answers
- [ ] Restart the PC (or after a Windows update) then sign in: Mira comes back on its own near the clock, no window in front nor in the taskbar; a download asked from the iPhone starts without opening Mira; click on the icon = window in its usual place (maximized if it was); Settings → Jellyfin server says “Ready”
- [ ] Downloads: a public magnet through TorLink, sorted into the library
- [ ] A two-season pack numbered end to end (“(Season 1-2) [Batch]”, “- 01” to “- 37”): first everything in season 1, then within the next 10 to 20 minutes (TheTVDB on) the season 2 episodes renamed into `Season 02`, and season 2 is no longer “missing”
- [ ] A `.torrent` from C411 through qBittorrent, sorted (hard link), qBittorrent keeps seeding
- [ ] qBittorrent and a USB drive: unplug the drive of its download folder during a download, the line says “disk probably unplugged” (the queue stays); plug it back, it resumes on its own in under a minute (Downloads page open)
- [ ] Playback comes before qBittorrent: a big download running on the movies drive, start a movie from another device, it starts and doesn’t stop to load; in qBittorrent (icon near the clock), the reduced-speed turtle lights up during playback and goes off a minute after
- [ ] qBittorrent with its port taken (rare, hard to cause by hand): in Task Manager, never a qBittorrent restarting in a loop; if the Downloads page reports a qBittorrent failure, write it down as is
- [ ] A download that lands on the 6 TB USB drive mounted in `MEDIAS` (as soon as it’s there)
- [ ] Follow a series: the next episode arrives on its own (let it run for a week)
- [ ] Automatic subtitles on: a movie without subtitles gets some, well synced
- [ ] Upcoming episodes (TheTVDB) on: on a series, a thin line “E3–4 · not in your library” between the episodes, or the next one as an empty line at the end of its season; a missing season in the season list
- [ ] Metadata beside the files: a few hours after opening Mira, a `movie.nfo` and a `folder.jpg` in a movie’s folder, a `tvshow.nfo` in a series’ folder; a title corrected with “Identify…” keeps its correction in its NFO
- [ ] Sealed secrets (0.9.16): after the extension update, `Mira.home-tailnet.json`, `Mira.invites.json`, `Mira.subtitles.json` and `Mira.proof.key` in `plugins\configurations` no longer hold a secret in clear (`dpapi:…`), no more `"password"` in `Mira.invites.json`; the home away from the house, a party link and a guest link already given still work, automatic subtitles too
- [ ] “Find a better version”: the old file goes to the Recycle Bin, the title keeps its progress
- [ ] Recycle Bin on Jellyfin’s PC: an episode (right-click) with its subtitles, a season (“…” of the Episodes line), on a test file; the window says what goes; everything comes back from the Windows Recycle Bin; on the WD mounted in a folder

## Your PC 🖥
- [ ] Slow player opening (from 0.9.15): a heavy movie away from home (conversion); after a second, a spinner on black instead of a black screen; Esc during the wait brings back the title page (not Home); started again right away, it opens. During the end-of-episode countdown left to run, Esc while the next one is being prepared: the player doesn’t come back. C key on a converted movie with PGS subtitles: off then back; the next episode keeps the choice.
- [ ] Extras kept out of “Continue watching” (from 0.9.15): start a Monogatari opening (Extras pill), stop it halfway; it isn’t in “Continue watching”, neither during the mini player nor after restarting Mira; same for a trailer before a movie stopped midway
- [ ] Subtitles kept on Jellyfin at once (from the version after 0.9.16): in an episode, subtitles → “Keep on Jellyfin” on, “Load a file…” with a `.fr.srt`: “Sending…” then “Subtitles kept on Jellyfin, in French…” on the video; the next time the episode plays (or on the iPhone), the track is offered; switch off: nothing sent
- [ ] Mira update offered and done (at the next version)
- [ ] A series’ extras (“The pill”): on Monogatari Series, “Extras” pill after season 14; a click shows Openings and Endings, one row each, “Show all” expands them; a thumbnail plays the opening/ending; a movie keeps its “Extras” row (Mac and iPhone the same)
- [ ] Player: “Cinema” picture without stutter on a 4K movie; Info says “Cinema”
- [ ] An HDR10 movie with Windows HDR on, then off: right colors both times; ⋮ → Info, “Colors” line: “sent to the screen in HDR” then “converted to SDR”
- [ ] A Dolby Vision movie (profile 5 or 8): no green or purple; “Colors” says “Dolby Vision profile 5” or “8”
- [ ] Audio output: “Test” plays each speaker; passthrough to an amplifier if you have one
- [ ] Picture in picture: the small window stays on top, subtitles readable, back without a black picture
- [ ] Shrink and enlarge the player: smooth, no black at either end
- [ ] Scrolling Movies and changing page: smooth at 240 Hz
- [ ] Narrator: Home, title page and player read with names (not “button”)
- [ ] Snap Layouts when hovering the maximize button
- [ ] Built-in Tailscale (without the Tailscale app, from another network): sign in, play a movie, sign out
- [ ] Couch mode on the TV (HDMI): Settings → General → Couch mode, full screen readable from the couch; arrows everywhere (rail included), Enter, Backspace; an Xbox controller (A, B, Y, LB/RB, in the player A, X, LB/RB); the TV remote if an HDMI-CEC adapter is there
- [ ] Movies filters: Filter → Picture 4K + Audio language French + Unwatched: only your unwatched 4K movies with a French track; “Filters · 3”; Reset brings everything back; “Shortest first”
- [ ] Trailers: a movie with a `trailers` folder (or `…-trailer.mkv`): the title page’s “Trailer” button, played in Mira; a movie without one: the button says “on YouTube, in your browser” and opens the browser; Settings → Playback → “Trailer before the film”: it plays first, “Skip trailer” goes to the movie, nothing on resume; “From the beginning” on a movie left midway: the trailer, then the movie from its start
- [ ] Notification area menu (right-click on Mira’s icon): during an episode, the card (picture, title, progress, buttons) above the icon; pause, ±10 s, next episode and stop respond without closing the menu; outside playback, only “Open Mira”, “Minimize…” and “Quit Mira”; Mira tucked away, no more “Minimize…”; taskbar at the top or on a side, second screen at another scale: the menu stays on the screen of the click; Esc, a click elsewhere, and the Menu key (Shift+F10) on the icon
- [ ] Remote control: from the iPhone, “…” of a movie → “Play on…” → this PC: Mira opens and plays it (window minimized or in the notification area included); the iPhone’s remote pauses, ±10 s, volume, mute, Stop; Jellyfin web (“Play on”) too; Settings → On Jellyfin right now from another device: “Stop” stops this PC
- [ ] Movie in two files (`Film - cd1.mkv` and `Film - cd2.mkv` in the same folder): part 2 follows part 1 without closing the player, “Part 2 of 2” under the title; stopped in part 2, the title page’s “Play” resumes there
- [ ] Settings → Security (administrator): a test account created without a password is shown there as “No password”; “Protect” → “Let Mira lock it” closes it; the device of a test account disconnected with “Sign out” must sign in again; “Fix all…” asks one question at a time. Never on the family’s real accounts without ogsas’s agreement

## iPhone 📱
- [ ] Install through AltStore, refreshed after 7 days of doing nothing
- [ ] Install AltStore from a Mac (wiki guide “iPhone and iPad”, Mac steps: AltServer for macOS, Finder Wi-Fi sync, open at login, Mac without sleep), then refresh over Wi-Fi through the Mac
- [ ] Movies filters: Picture, Rating, Audio, Subtitles pills; “4K” + “Audio · French” together, then Reset
- [ ] The same movie in two files: part 2 follows part 1, “Part 2 of 2” under the title, “Play” resumes in the right part
- [ ] QR code of “Mira on your iPhone” (Mira for Windows’ Guide, or Settings → Account on the Mac) scanned by the camera: Mira opens, server filled in, then code or password; then Jellyfin reached at home and over 4G
- [ ] Opening: the “m” then Home, without a white screen
- [ ] A movie and an episode played from start to end; next episode on its own
- [ ] A person’s page: on a movie’s title page, “Cast & crew” (directing first, photos), tapping an actor opens their page (photo, birth, biography, their movies and shows from your library); Search also finds an actor by name
- [ ] “Are you still watching?”: 3 short episodes in a row without touching the iPhone, the 4th doesn’t start and the card asks; “Keep watching” starts it (same test on your PC and a friend’s Mac, without touching keyboard, mouse or controller)
- [ ] Player: double tap ±10 s, swipe for brightness and volume, pinch, swipe down to shrink
- [ ] Apple’s PiP and AirPlay to a TV
- [ ] Subtitles of a real anime (ASS) readable and synced
- [ ] A Dolby Vision profile 8 movie (Jellyfin 12.2 or later) then HDR10: played without conversion, right colors
- [ ] Built-in Tailscale over 4G (without the Tailscale app): playback, then background and back
- [ ] Mobile data: a movie over 4G, ⋮ → Quality says “Mobile data · 2 Mb/s” and the picture stays decent; leave Wi-Fi mid-playback: the movie resumes at the same spot at 2 Mb/s, then goes back to normal quality when Wi-Fi returns
- [ ] Missing and upcoming episodes (TheTVDB extension installed by Mira for Windows): on a followed series, nothing above the seasons; the missing ones as a thin line in their place (See = the list with the days), the next to come out as an empty line with its day, a missing season (“Season 1 · not in library”) = a card; Settings → Playback → “Show missing episodes” off = only the upcoming ones
- [ ] Episodes numbered from the start of the series (Shingeki no Kyojin on the Dell, S02E26–S04E89): no more “E1–12 · Not in your library” nor “Get E1–12” on Windows, Mac and iPhone; on the Dell, the files sorted by Mira renamed to S02E01… (subtitles included), Jellyfin shows their names and pictures
- [ ] Collections: “…” of a movie → “Add to a collection…”: ticked in an existing collection, then a new one named with it; seen in Mira for Windows; same thing with a long press on a poster
- [ ] Trailers: “…” → “Trailer” of a movie that has one on the server, played in Mira; “Trailer on YouTube” opens Safari
- [ ] Opening with Jellyfin’s PC off: a clear message, then Home when it comes back
- [ ] A title that used to fail: write down the error and the media info
- [ ] “Keep on this iPhone”: an MP4 movie and an MKV (converted), then a season; Mira in the background during the copy (iOS progress, resumes on return); in Airplane mode, played from Settings → On this iPhone and Home (tracks, subtitles, next episode, fast-forward in the converted MKV); back home, the resume point is the same on the PC; sign-out = copies removed; copy started on Wi-Fi then Wi-Fi cut (4G/5G): “Waiting for Wi-Fi”, nothing goes over mobile data, resumes on its own when Wi-Fi returns; “Keep over mobile data” on: it continues over 4G/5G

## A friend’s Mac 🍎
- [ ] Install from the `.dmg` (“Open Anyway”) and first launch
- [ ] Automatic update to the next version (“Restart”)
- [ ] Picture neither gray nor black; otherwise ⋮ → Info, “Repaired” line
- [ ] An HDR10 (or Dolby Vision) movie: right colors, not washed out; ⋮ → Info, “Colors” line: “HDR10 → SDR (rendering limit)”
- [ ] Trackpad: scroll (Home: one gesture = the page or the row), two-finger swipe between pages (the page follows the fingers, comes back if let go early), pinch in the grids (posters that slide), three-finger swipe, pinch in the player
- [ ] Picture in picture (small floating window)
- [ ] Audio output: Settings → Audio & subtitles → “Test” plays each speaker (headphones, then speakers or AirPods); ⋮ → Info gives the output; Dolby Digital to an amplifier if Alex has one
- [ ] Couch mode: enlarged full screen, arrows and Backspace, a paired controller (PlayStation or Xbox): A opens, B goes back
- [ ] Built-in Tailscale: sign-in, keychain kept after an update
- [ ] Media keys, Mira behind another window during an episode: F8 pauses and resumes, F7 goes back 10 s, F9 skips to the next episode; the AirPods button pauses; Control Center and menu bar → “Now Playing”: title, poster, moving bar, ±10 s; with the player closed, F8 goes back to Music
- [ ] Notifications: a new episode of a followed series (favorite or started) added to Jellyfin, Mira open or in the Dock: macOS first asks “Mira would like to send you notifications” (Allow), then the “New episode” notification with its picture; a click opens the series; in a Watch together group, Mira behind another window, “X joined the group”; refused, nothing more and no more asking
- [ ] Remote control: from the iPhone, “Play on…” → the Mac: the movie starts, pause, ±10 s, volume, Stop from the iPhone’s remote
- [ ] Apple → Restart with Mira open, then Mira in the Dock (window closed with ⌘W), then during a movie (and in picture in picture): the Mac restarts without “Mira interrupted the restart”; on return, the movie resumes where it was (“Continue watching”)
- [ ] ⌘Q with Jellyfin’s PC off: Mira leaves the Dock in 5 s at most
- [ ] Update from the Dock: a new version ready (notice “it’ll install…”), window closed with ⌘W; a few minutes later, Mira restarts on its own, hidden (the dot under the icon stays); a click on the icon shows the new version (“What’s new”)
- [ ] Sleep mid-movie (from Jellyfin, not a kept copy): lid closed for a few minutes, then opened: the movie is paused where it was, Play starts it again without an endless spinner (Jellyfin unreachable on wake: “Can’t play this” and “Try again”, the player stays); Wi-Fi cut mid-playback: “Lost the connection to the server”, then the movie resumes when Wi-Fi comes back
- [ ] Account photo from an iPhone photo (HEIC, taken in portrait, through AirDrop or Photos): Settings → Account → “Choose a photo…”; it appears upright (not lying down) in the bar, then on the iPhone

## Watch together 👥
- [ ] Your party by the link (Funnel): Sam joins from Windows without an account
- [ ] Same thing from an iPhone, then from the Mac
- [ ] Pause, seek and next episode followed by everyone
- [ ] Already playing, create the group then invite: the guest arrives at the same minute, without restarting
- [ ] 20 min together through Funnel without a pause or playback looping (seeks, audio track change)
- [ ] You in Sam’s party (his Jellyfin), from Mira for Windows
- [ ] Link remade: the old one no longer works
- [ ] The home: set up on the Dell (password, two profiles, one with a PIN), joined from another device, profile opened, switched, device removed (Windows ready; Mac and iPhone when their clients are there)
- [ ] The home on Windows: PC without an account at home → “…’s home” card found on the network, password, “Who’s watching?”, wrong PIN then right one, Ctrl+P, “Leave this home”
- [ ] The home away from home (Windows): Settings → Family & profiles, OAuth client and policy on the real Tailscale, home link opened on a PC outside the house, profile opened through the home’s node, device removed = node gone from the tailnet
- [ ] The “Away from home” guide (Windows, then Mac and iPhone) on the real Tailscale: ogsas’s “All” client flagged, “Redo step 2”, tag and key made from the drawings (console labels right?), secret alone accepted (ID really in the secret?) and found in the clipboard, “See the exact lines”, “Make these changes”, “Undo” within the 10 minutes
- [ ] Hardened “Fix my policy” on the real Tailscale (with ogsas’s agreement): changes listed (shared friends limited to Jellyfin + 443, Funnel on the PC, the PC’s key), written, the friends the PC is shared with still watch (and a party by the Funnel name), no more access to the Dell’s other ports, `keyExpiryDisabled` true in the console (or the refusal noted in the log), “Undo”
- [ ] The home on the Mac (Mac friend): card found on the network, password, “Who’s watching?”, wrong PIN then right one, ⌘P, macOS asking for the keychain at first launch after an update, “Leave this home”; then a home link opened outside the house
- [ ] The home: home link opened from miracinema.app/home (the browser passes `mira://home#…` to Mira, fragment included)
- [ ] The home on the iPhone: at home, sign-in → home card found, password, “Who’s watching?”, wrong PIN then right one, long press on the Settings tab, “Leave this home”; then a home link opened over 4G (the home’s node, with and without your own Built-in Tailscale)
