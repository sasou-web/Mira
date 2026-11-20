# Validation — October 2, 2026

> Archived on 2026-11-20: this record stopped being kept on 2026-10-14, in the days of Mira web. Tests now live in [TESTS.md](../TESTS.md), tries on real devices in [RECETTE.md](../RECETTE.md), and what was never tried for real in each entry of [DECISIONS.md](../DECISIONS.md).

The `.artifacts/...` paths cited below are local validation evidence, excluded from the repository. The public gallery only uses demo mode. The latest results of tests run by hand, dated and without personal data, are in [testing/latest-results.txt](../testing/latest-results.txt); the tests, checks and packages also run in GitHub Actions ([TESTS.md](../TESTS.md)).

## Download to the server PC from another device (unreleased)

- **Need**: start a TorLink download from the phone or another PC so that it happens on Jellyfin’s PC, without remote desktop; TorLink only downloaded on the PC where Mira is open.
- **Done**: Mira web for an administrator (**Settings → Download to the server PC**, or from a search with no results) searches, sends a result or a pasted magnet link, follows, pauses, resumes and cancels; the extension relays (`/Mira/torlink`, in memory); Mira for Windows open on Jellyfin’s PC comes to the relay, searches with `torlnk search` and downloads with `torlnk serve` (TorLink without a window, same queue as the Downloads page, which hands over when nobody uses it and takes it back when shown); the sorting is the usual one. Option “Download from your other devices” (Settings → TorLink), on; on another PC, it opens Mira web.
- **Verified on Jellyfin 12.1 (container, Chromium at iPhone size, fake Mira for Windows at the relay)**: search “--mira check” in Shows cleaned into “mira check” on the PC side, result “1.4 GB · 42 sources”, download followed at 37%, pause then cancel received by the PC, a link that isn’t a magnet refused, pasted magnet link sent; a non-administrator account gets 403; no JavaScript error nor security policy refusal.
- **Verified with the real TorLink 1.9.0 (Linux)**: `TorLinkServeClient` against `torlnk serve` started by `TorLinkRemote.ServeArguments`: wrong token refused (401), add then duplicate (`duplicate`), invalid link refused with TorLink’s reason, pause, resume, cancel, `stop-seed` of a missing share (404, “not this download”); queue saved on Ctrl+C (SIGINT) and found again at the next start, as when the page takes over.
- **Tests**: cases shared by Mira.Core and Mira web (`tests/fixtures/torlink-remote.json`: hexadecimal and base32 magnet links, searches, names); relay (accounts, caps, a single agent, requests taken back after 2 min, cancel before the PC); output of `torlnk search`, TorLink queue, reasons shown (Core, everywhere); `check.mjs` with a fake agent.
- **Not verified**: on a real Windows PC, the switch from the TorLink page to TorLink without a window and back, a real search (the container can’t reach TorLink’s sources), a real sorted download.

## “On Jellyfin right now” in Mira web (unreleased)

- **Need**: see from the phone who is watching what on the server, and why a title is converted, without turning on Mira for Windows; until now only Windows showed it.
- **Done**: for a Jellyfin administrator (`Users/Me`, `Policy.IsAdministrator`), **Settings → On Jellyfin right now** opens `#/en-ce-moment`: `Sessions?activeWithinSeconds=960` read again every 5 s while the screen is shown and the phone in front, lines updated in place; texts from `viewers.js`, identical to those of `Viewers` (Mira.Core); **Stop**, confirmed by a sheet, sends `Sessions/{id}/Playing/Stop`.
- **Verified on Jellyfin 12.1 (container, Chromium at iPhone size)**: a phone plays a movie (converted to VP9 and Opus, 1.4 Mb/s, “file format, video codec, audio codec”) and a TV declares a direct play through the API; the administrator sees both, **Stop** only for the phone; the line follows the time without being redrawn; screen left, no more reading of sessions; **Stop** confirmed closes the phone’s player and the list only has the TV left; another account doesn’t have the entry. No JavaScript error nor security policy refusal.
- **Tests**: cases shared by Mira.Core and Mira web (`tests/fixtures/viewers.json`: sessions, places in IPv4 and IPv6, Tailscale in IPv6 included, durations); `check.mjs` stops the phone’s playback from this screen, on another device.

## Graphics card checked again after an update (unreleased)

- **Need**: an update of the card’s driver or of Jellyfin can break conversions that worked when “Use the graphics card” kept the card; the phone then only gets “Can’t play this” for any title to convert, and nothing said so on the PC.
- **Fix**: for an administrator of a Jellyfin on this PC that converts with a card, Mira compares Jellyfin’s version and the cards’ drivers to those of the last successful test, two minutes after it starts then every half hour; if they changed and nobody is watching, it runs the conversion test again; two failures in a row put back the processor, with a notice.
- **Verified on Jellyfin 12.1 (container, without a graphics card)**: through `JellyfinClient` and `GpuRecheck` themselves, `nvenc` set: first test `False` (500 from Jellyfin), `TryAgain`; second `False`, `Processor`, `HardwareAccelerationType` read back `none`; next test with the processor `True`.
- **Tests**: fingerprint (order, duplicates, unknown version or cards), decision (processor, unreadable setting, nothing changed, someone watching), verdicts after a test, notice, version read in `System/Info/Public` (Core, everywhere).
- **Not verified**: on a real PC, the drivers read through WMI, a real driver update, the notice in Mira for Windows.

## Mira web up to date the same day (unreleased)

- **Need**: after a Mira update, Jellyfin only took the new version of Mira web at its update check (at its start, then every 24 h), then at its next restart: sometimes days later.
- **Fix**: once the repository has moved forward, Mira for Windows starts Jellyfin’s `PluginUpdates` task and waits for it to end, then offers “Restart Jellyfin” when no session has a title playing, checked again every 5 min.
- **Verified on Jellyfin 12.1 (container)**: extension 0.7.8 installed from a local repository, repository moved to 0.7.9, task started through the API: done in 1 s, `/Plugins` gives 0.7.8 `Superseded` and 0.7.9 `Restart`; after `System/Restart`, 0.7.9 `Active`. Then through `JellyfinClient` itself (0.7.9 → 0.7.10): `UpdatePluginsNowAsync` true in 1.1 s, 0.7.10 pending, served 4 s after the restart. The task also runs at Jellyfin’s start and every 24 h (`StartupTrigger`, `IntervalTrigger`).
- **Tests**: versions read from `/Plugins` (served, pending, disabled, missing), task started and waited for, task already running waited for without restarting it, task too long, guest account (Core, everywhere).
- **Not verified**: the “Restart Jellyfin” notice in Mira for Windows on a real PC.
- **New in Mira, on the phone**: signed in on a 0.7.7 extension, Jellyfin moved to 0.7.8 by its task then restarted, Mira web opened again shows “Mira 0.7.8” with only its points for the phone (Watch together, Sleep timer), once only; nothing at sign-in, nor on an invite (the sheet waits for Home), nor for a sign-in made during the opening; an older session (seen version unknown) sees the current version (Chromium, Jellyfin 12.1 in a container, no error nor security policy refusal). Not yet seen on a real iPhone.

## The server PC doesn’t fall asleep during playback elsewhere (unreleased)

- **Need**: a sleeping PC is unreachable, and Tailscale can’t wake it; neither Mira nor Jellyfin 12.1 kept it awake during playback on another device.
- **Fix**: for a Jellyfin on this PC, Mira reads its sessions every minute (`Sessions?activeWithinSeconds=960`) and, as long as another device has a title playing, paused included, keeps the PC awake (`SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` from the UI thread; closing the lid or asking for sleep still puts it to sleep). The card reads the active power plan when plugged in (powrprof: sleep, hibernation, lid closed if the PC has one); **No sleep when plugged in** goes through `powercfg` with the person’s rights, then reads again.
- **Tests**: texts and steps of `ServerSleep` (laptop, desktop, hibernation only, lid closed only, never), PC kept awake or not depending on the sessions (Core, everywhere).
- **Not verified**: on a real PC, the values read by powrprof, `powercfg /change` without administrator rights, sleep really held off during playback.

## Tailscale without a session and expiry of its connection (unreleased)

- **Need**: after a restart without a session (Windows Update at night), Tailscale without “Run unattended” stays off: Jellyfin runs, but the phone can’t find it any more, even at home (the QR code gives the Tailscale address), and the card said “Available as soon as the PC turns on”. Same thing the day this PC’s key expires (180 days by default).
- **Fix**: for a Jellyfin on this PC and Tailscale connected, Mira runs `tailscale status --json` then `tailscale debug prefs` (the `tailscale.exe` from Program Files, with the person’s rights); `TailscaleNode` (Core) reads `ForceDaemon` and `Self.KeyExpiry`/`Expired`. Step “Tailscale goes off without your session” with **Keep Tailscale connected** (`tailscale set --unattended=true`, read back), step and notice at start two weeks before expiry, with Tailscale’s Machines page.
- **Sources read** (tailscale/tailscale, `main`): `ForceDaemon` as `omitempty` in `ipn/prefs.go`; `--unattended` in `tailscale set` (Windows); `KeyExpiry` of `Self` missing when expiry is disabled (`peerStatusFromNode`); on Windows, the LocalAPI gives read and write to whoever can connect to it (`actor.Permissions`).
- **Tests**: `TailscaleNode.Parse` (without a session or not, expiry far, close, past, disabled; Tailscale silent, error, unreadable prefs), steps of the card (Core, everywhere).
- **Not verified**: on a real PC, the outputs of `tailscale.exe`, `tailscale set --unattended` without administrator rights, the `TS_UNATTENDEDMODE` policy of a managed PC.

## Jellyfin stopped on this PC (unreleased)

- **Need**: a Jellyfin stopped on the server PC (three failed Windows restarts, stop from its dashboard) was only noticed on the phone, through “Server unreachable”; Mira for Windows said “temporarily unavailable”, as for a distant server.
- **Fix**: for a Jellyfin on this PC that doesn’t answer (no answer or 503), Mira reads the state of its service (`QueryServiceStatus`, without rights) or, without a service, whether `jellyfin.exe` is running; `LocalJellyfin.Judge` (Core) turns that into stopped, starting, or stops after each start (two failed starts in a row). **Start Jellyfin**: `sc start` through the elevated script of **Set everything up**, or its program with `--datadir`, like its notification area app; Mira waits for its answer three minutes at most and counts a failed start if it stops again.
- **Tests**: the cases of `LocalJellyfin.Judge` and `Says`, and the “web” step of `PhoneSetup` for each (Core, everywhere).
- **Not verified**: on a real PC, the service state read without rights, elevated `sc start`, the program started without a service, the cause read in the log.

## Jellyfin install that stayed stuck (unreleased)

- **Reported defect** (2026-10-14, then when Jellyfin moved to another PC): the first install from Home shows an error and stays stuck; restarting Mira and starting again finishes the setup without downloading again. Same picture as at the first test of 0.5.5 (below).
- **Cause, read in `jellyfin.nsi` (jellyfin-server-windows)**: the last step of the silent installer is `nssm start JellyfinServer`; when it returns before the service runs, `ShowError` opens “Could not start the Jellyfin Server service” as `MB_ABORTRETRYIGNORE`, even when silent, and the installer waits. Mira waited for it to end, without a Back button. Retry runs `nssm start` again on a service that is running: new failure, same window. The 0.5.5 fix only judged the exit code, which only comes after the answer.
- **Fix**: `JellyfinSetup.AwaitInstallerAsync` returns as soon as Jellyfin is ready, even if the installer is still waiting; its window is put in front of Mira, with “choose Ignore”, repeated by a notice until it closes.
- **Tests**: Jellyfin answering while the installer waits, installer finishing before Jellyfin, installer already finished (Core, everywhere). The whole path (`WaitAsync`, wizard, sign-in, libraries, filters) run again on a new Jellyfin 12.1 in a container, started as soon as it starts: ready in 11 s, set up in 16 s.
- **Not verified**: the installer and its window on a real PC; `SetForegroundWindow` to a window of an administrator program.

## Mira 0.7.4: Mira on your phone, in one click

- **Reported defect** (2026-10-10, PC of a friend of the user): Tailscale open on the PC and the phone, `http://100.x.y.z:8096/Mira` shows nothing, nor even `http://100.x.y.z:8096`.
- **Causes, on this PC** (PowerShell outputs noted by the user): no firewall rule for `jellyfin.exe` (Windows prompt never accepted, Jellyfin without a service), so Jellyfin blocked everywhere; Jellyfin refusing Tailscale (`EnableRemoteAccess` false, `LocalNetworkSubnets` empty); Mira web missing (`http://localhost:8096/Mira` not found). Fixed by hand with the guide’s buttons and two `New-NetFirewallRule` rules.
- **General mechanism**: without a service, Jellyfin only has the rules of the “Allow access” prompt (allowed on the ticked profile, blocked on the others, nothing if it’s closed; Jellyfin’s installer and its notification area app create none, read in `jellyfin-server-windows`). Tailscale marks its network adapter private (`setPrivateNetwork`, `wgengine/router/osrouter/ifconfig_windows.go`), and the router is often counted public: one of the two networks is refused. On top of that the guide asked for three parts and five buttons in the right order, and the QR code only gave the home address.
- **Fix**: a “Mira on your phone” card with ticked steps, **Set everything up** (one Windows permission prompt: firewall for home and Tailscale, service if there is one; then Tailscale in Jellyfin, then Mira web), QR code at the Tailscale address once ready, “Server unreachable” that says what to check.
- **Tests (container, Mira.Core logic, then Windows CI)**: firewall judged for Tailscale and for home (prompt with public or private router, prompt closed, Mira’s rule, firewall off, allowed by default, “block all incoming connections”, fifteen unrelated rules and ten that block, `LocalSubnet` and ranges containing the PC); steps of `PhoneSetup` (the friend’s new PC, all ready, without Tailscale, all blocked, unreadable firewall, service to set up, account other than the administrator, Jellyfin on another computer, PC without a network) and QR code address.
- **QR code page** (Chromium, served from disk): with `?partout=http://100.101.102.103:8096`, the Tailscale address first, “At home only” as a choice; an address other than Tailscale passed the same way is ignored. Added to the CI Chromium check.
- **Real firewall (Windows CI, administrator)**, first run of the Tailscale-only version: the rules of Windows apps (no program, “allow all” for their package) were counted as allowing any program, and their package can’t be read through COM (`LocalAppPackageId` empty, Windows 26100): a rule without a program now only counts as a port rule (named ports, without a service), verified on the runner’s ones. “Any profile” block rule created by `netsh` for a test program, read back through COM; opening removes the home and Tailscale profiles from it and keeps it for the others, adds Mira’s two rules, then the judgment read back is “open”; a second run duplicates nothing; everything is removed at the end.
- **CI of commit `a2e85b9`**: Windows 79 tests passed (including the real firewall: the runner’s Ethernet adapter in the public profile, blocked then opened), Mac, Chromium. Safari 33/34: a click on the “Play” button of a title page redrawn in the meantime (`stale element`, HDR10 file; the title page isn’t touched by this change); the Safari check now finds the element again, three times at most. Screenshot `19-guide-phone` added to the gallery: the whole card.
- **Adversarial review (5 defects, fixed)**:
  - “Set everything up” asked again for the Windows permission endlessly for what it can’t do: Jellyfin’s program not found (another account, Docker), block rule without a program (its own, or a security suite’s), unreadable firewall without a service (Mira’s rule was only read with the service). These cases are now a step for the person, which says where to act (the rule named); Mira adds its rule even if it can’t read the firewall.
  - Jellyfin restarting more than 90 s after Mira web was installed, or stopped, passed for “Mira web to install” and “the administrator account is needed”: `System/Ping` first, the card waits for it to answer then reads again.
  - Wi-Fi blocking everything, Tailscale open: the QR code gave the home address, blocked. Tailscale’s verdict is kept separately.
  - Mira back in front during the final re-read of “Set everything up”: a second read that erased the message and prevented the QR code from opening.
  - Jellyfin on this PC reached by its network address (`http://192.168.1.20:8096`): taken for another computer.
  - Tests added: each of these cases in `PhoneSetup`, a single address with a full mask (`/255.255.255.255`) that doesn’t count for home, and on the real firewall a port block rule without a program, named and kept.
- **CI of commit `867b8ef`** (review fixes): all green. Windows 79 tests passed (including the port rule without a program on the real firewall, named and kept) and 38 interface views, Mac, Chromium, Safari with the 8 formats.
- **Not verified**: “Set everything up” on a real PC (Windows permission, then the phone); the interface hasn’t been run with a real Tailscale.

## Mira web 0.7.3: feedback from the third iPhone test

- **Defects reported by the user** (iPhone on iOS 27, Home Screen app):
  - some titles show “This device can’t play this stream”, and a lower quality changes nothing;
  - “Discreet playback” hides the Dynamic Island but cuts the sound;
  - switching from one tab to another is abrupt and the screen reloads several times;
  - the bottom bar moves up a little in Search;
  - the favorite’s vibration doesn’t happen (useless anyway).
- **Established causes** (sources of Jellyfin 12.1, jellyfin-web and WebKit, tests on Jellyfin 12.1):
  - Mira only retried a file played as is: a refused HLS stream went straight to the message;
  - a lower quality changes nothing, because Jellyfin copies the video as long as the file stays under the cap, and the audio at every quality;
  - the profile named no picture range: Jellyfin copied HDR10 and Dolby Vision without checking, and interlaced video as is;
  - Dynamic Island: iOS only shows it for a non-mixable audio session. The only mixable session WebKit gives a page, `ambient`, is muted by silent mode, and no type gives both;
  - tabs: the screen left was removed all at once, then came the empty slots, the grid (19 px offset) and the posters in waves;
  - Search: the field took focus without a gesture, which makes it glow without a keyboard. Probable cause of the offset, not observed for lack of an iPhone: the bug of Home Screen apps that keep a shorter screen after the keyboard.
- **Reproduction in Apple’s real player** (Safari CI on Mac, commit `cb31b87`, before fixes), with 8 format samples:
  - MP4 with DTS, interlaced H.264 in TS, HEVC x265 and H.264 with open GOPs, MPEG-2 in TS whose clock starts at 50,000 s, HEVC hev1 in MP4, Matroska with irregular keyframes resumed at 150 s: they play and move on after a seek;
  - **the copied 10-bit HDR10 HEVC is refused** (“error 3 Media failed to decode”), with the user’s message.
- **After fixes**:
  - Chromium CI **54/54** and Safari CI **34/34** on PR sasou-web/Mira#25;
  - HDR10 is played converted, after a single refused request;
  - the offset TS is played with its subtitles at 2.3 s;
  - Jellyfin deinterlaces the interlaced one (`yadif` in its log).
- **New points of the Chromium check**:
  - a stream cut twice plays at the third request (direct play, video copied, audio copied: 111, then 001, then 000);
  - cut every time, “Can’t play this” shows with the browser’s error;
  - Movies arrives whole, without an empty slot or offset: the tab lights up at 12 ms, the screen arrives at 212 ms with a first page slowed to 150 ms;
  - tab fade, Home kept;
  - Search only takes the keyboard at the second tap;
  - Settings isn’t redrawn;
  - Home updates in place: 6 cards kept out of 9.
- **Adversarial review** (five reviewers, one skeptic): 22 points, 17 confirmed and fixed, 5 refuted. Among the confirmed ones:
  - with hls.js, the errors of a stream already replaced skipped the “video converted” step;
  - the resume started again from 0:00 when hls.js reset the video;
  - “Try again” started again from 0:00;
  - the last resort encoded a 4K at level 4.1, which Safari refuses. Fixed and verified on a 4K: 1920×1080, AAC;
  - the menus of kept cards acted on an old object;
  - the first kept page never matched the server’s answer;
  - waiting for a screen stayed stuck on a deferred picture;
  - a second tab tap during the wait acted on the screen left.
- **Jellyfin 12.1 can’t convert again to VP9 a VP9 of unknown level** (“level -99”, ffmpeg code 222). That doesn’t concern a phone: the Chromium check of the cascade starts from an H.264 movie.
- **Not yet verified on a real iPhone**:
  - the titles that failed for the user (unknown codecs);
  - the cascade and the fade on iOS 27;
  - the bottom bar in Search;
  - “Hide the Dynamic Island” with silent mode off.

## Mira web 0.7.2: feedback from the second iPhone test

- **Release v0.7.2** (2026-10-10, `tools/release.ps1` on the PC that has the key, commit `515983e`): 11 files online, published and marked as the latest.
  - The three Windows packages match their `.sha256` and `mira-update.json` (size, SHA-256).
  - The manifest’s signature is accepted by the key of `UpdateKeys.Trusted` and refused on a modified copy (OpenSSL, ECDSA P-256).
  - `Mira.exe`, `Mira.dll`, `Mira.Core.dll` and the portable executable carry `0.7.2+515983e…`. The archive contains no profile, no key and no libmpv.
  - `mira-jellyfin-0.7.2.zip`: MD5 identical to `jellyfin-manifest.json` (0.7.2.0, ABI 10.9.0.0), DLL at `0.7.2+515983e…`, the 39 files of `src/Mira.Web` identical to those of `main`. `releases/latest/download/jellyfin-manifest.json` serves this manifest.
  - `Mira-0.7.2-mac-arm64.dmg` (UDIF, 79 MB): built, checked (Self-check, Video probe) and attached by `mac.yml` from the same tag.
- **Defects reported by the user** (iPhone on iOS 26, Home Screen app): the Dynamic Island still appears, Apple’s player menu offers no subtitles, and the interface doesn’t behave like an app (movements, transitions, erratic reactions).
- **Established causes** (sources of Jellyfin 10.9 to 12.1 and WebKit, then tests on Jellyfin 12.1):
  - Jellyfin never converts ASS or SSA through the profile (`MediaStream.SupportsSubtitleConversionTo`): an anime’s subtitles were burned in, the whole video re-encoded, and the menu empty;
  - with “None”, Jellyfin writes `SubtitleMethod=Encode` without an index: the stream had no subtitles;
  - its HLS WebVTT have `X-TIMESTAMP-MAP=MPEGTS:900000`, right for TS segments (at 10 s), not for its fMP4 segments (at 0);
  - WebKit picks by itself any track the page never set, and an unchanged mode doesn’t count;
  - Dynamic Island: WebKit hides it in front of the app by app identifiers, which aren’t those of a Home Screen app; in full screen, it even lifts that mask. Nothing in the page changes anything.
- **Local Chromium check** on Jellyfin 12.1 (Docker): **41/41**. New points:
  - “anime” movie (MKV, French ASS by default, forced ASS, English SRT, SRT without a language): ASS read as text beside the video (“Cinq secondes” at 5.1 s), without burn-in;
  - the same movie requested with an iPhone’s profile: never burn-in for -1, ASS or SRT; in the `/Mira/hls` list, the four subtitles named “Français (ASS)”, “Français (ASS · forcés)”, “Anglais”, “Piste 4”, languages `fra, fra, eng, und`, only the title page’s choice as `DEFAULT=YES`; two WebVTT segments without MPEG-TS mapping, at the file’s times (5 s and 35 s);
  - simulated Apple player closed while the next episode loads: back to the title page, no new full screen (the defect reproduced: Mira asked for full screen again 43 ms after closing);
  - simulated Home Screen app: Back draws iOS’s movement, iOS’s swipe doesn’t get a second one, the small-title bar goes back to the top without opening what it covers;
  - Mira’s full screen (Android): the tracks sheet shows in it.
- **GitHub Actions, on the branch** (commit `31e425e`, then `87dc56d` after the review):
  - **Chromium**: **43/43**, Jellyfin stopped then restarted during the check;
  - **Safari on Mac**: **26/26**, including, in Apple’s real player (WebKit full screen, AVFoundation): the anime with “None”, its four subtitles in the stream, none shown; “Français (ASS)” chosen as the menu would, “Cinq secondes” seen at 5.3 s; after a seek to 33 s, “Trente-cinq” seen at 35.1 s; the next episode still without leaving full screen;
  - **Windows**: build and tests passed (including `HlsPlaylists`: names taken as is, `und`).
  - a Safari failure along the way: Jellyfin refused the list of forced subtitles of a movie it had just added (“HLS Subtitles are not supported for this media”, duration still unknown), and Apple’s player refused the whole stream. The extension now answers an empty list in that case, and `setup.mjs` waits for the durations; `scrollbar-gutter` shrank the video in Safari with classic scroll bars: removed.
- **Adversarial review** (four independent reviewers, one verifier): 20 defects confirmed and fixed, including:
  - a stream error while Jellyfin answers (500, failed conversion) restarted playback endlessly under “Lost the connection to the server”: reproduced (8 playback requests in 24 s), then “Can’t play this” after a single one;
  - after a real outage, the resume started again from 0: reproduced, then resume at 20 s;
  - forced subtitles (`kind="forced"`) were never shown outside Safari (metadata);
  - one title’s speed carried over to the others, the controls stayed shown after a pause during a seek, the big title was half erased at rest, a right-click swallowed the next one, the title page loaded twice when opening.
  Local Chromium check after fixes: **42/42** (new point: forced subtitles can be shown).
- **Publishing in one command** (`tools/release.ps1`): adversarial review (two reviewers: PowerShell, git and gh), 8 defects fixed:
  - `-SkipBuild` published packages built on a commit other than the tag’s;
  - a signed manifest that no longer described the files (interrupted build) passed;
  - a draft left by an interrupted upload made it say “already exists”;
  - a local commit missing from GitHub was only seen at the end, a tag already there never;
  - another default gh account passed for “not signed in”.
  Test bench under PowerShell 7 (local Git repository, fake `gh`, path with spaces and accents): **21/21** scenarios. Windows PowerShell 5.1 and 7 syntax checked by `ci.yml`.
- **Safari check**: the position kept when Apple’s player closes is read again for 8 s at most. Jellyfin stops the conversion before saving it: a read after 1.5 s had failed once in CI (3.0 s read, 8.1 s sent). CI on `e7f7b3e`: Safari 26/26 (8.3 s), Chromium, Windows and Mac passed.
- **Not yet verified**: on a real iPhone, the subtitles menu of a real anime, the next episode in full screen and closing while it loads, the Dynamic Island with “Discreet playback”, haptic feedback.

## Mira web 0.7.1: feedback from the first iPhone test

- **Defects reported by the user** (iPhone, Home Screen app):
  - at the PC’s start, the app showed Safari’s error page and never got out of it on its own;
  - playback in the page made the Dynamic Island appear, like background playback.
- **Established causes**:
  - without a service worker (local HTTP), an app opened before Jellyfin answers can only show Safari’s error, without a button to reload. During its start, Jellyfin 12.1 first cuts the connection, then serves a startup page (503) and even answers `System/Info/Public` (in camelCase): seen on a Jellyfin 12.1 restarted while being queried;
  - read in the Jellyfin installer’s script (`nsis/jellyfin.nsi`, `master` branch): service with automatic start, Network Service account, `AppExit Default Exit`, no firewall rule. A service never gets the firewall’s “Allow” prompt;
  - read in WebKit (`HTMLMediaElement.cpp`, `HTMLVideoElement.cpp`, `MediaElementSession.cpp`): without `playsinline`, an iPhone only plays in full screen; the first gesture lifts `RequireUserGestureForFullscreen`; the `<track>` elements are passed to AVFoundation at load (`outOfBandTrackSources`); removing the `<video>` from the document leaves full screen; on Mac, video in full screen goes through element full screen, which wants a gesture.
- **Local Chromium check** on Jellyfin 12.1 (Docker), with the compiled extension: **29/29**. On top of the 18 points from before:
  - starting tracks from the settings’ languages, changed on the title page, requested from Jellyfin (`AudioStreamIndex` 4, `SubtitleStreamIndex` 1), then played: stream converted with the English track, French subtitle shown (“Lueur en VF.”);
  - Jellyfin stopped: Mira opens from the kept page and shows “Server unreachable”; restarted: everything resumes on its own;
  - simulated Apple player (`webkitPresentationMode` and its event): full screen from Play, next episode without leaving full screen, picture in picture then back, closing to the title page with the position kept.
- **GitHub Actions, on the branch**:
  - **Windows**: build without warnings, **76 tests**. The new test creates a service on the runner (administrator, without a permission prompt), sets it up with the real `sc` and `netsh`, then deletes it. It first checks the delayed start, without restart or rule, then the service ready, and that a second run breaks nothing. The interface gallery (36 views) is unchanged;
  - **Chromium**: **29/29**, Jellyfin stopped then restarted during the check (`docker stop`, `docker start`);
  - **Safari on Mac**: **20/20**. On top of the 11 points from before, Apple’s player (`nativePlayer` setting, WebKit full screen):
    - French subtitles chosen on the title page;
    - HLS playback in full screen, with a tap, as Safari asks on Mac;
    - the subtitle in its menu and shown on the picture (“Bonjour depuis Mira web.”);
    - closed, back to the title page and position kept (8.2 s);
    - next episode without leaving full screen, then back.
  - Two failures of the new Safari check, coming from the script and not from Mira, were fixed:
    - it went on while Back was still sending the stop: its back navigation came after the next navigation;
    - it closed the movie after 1 s, under Jellyfin’s minimum for a resume (5%, i.e. 2 s of this movie).
- **Defect found along the way**: while the tracks loaded, a change made by the browser passed for a choice and showed the English subtitles instead of the French ones asked for.
- **Not yet verified**:
  - on a real iPhone: full screen without a tap after the first gesture, iOS’s subtitles menu, no Dynamic Island, opening from the kept page with the PC off;
  - on the user’s PC: **Available as soon as the PC turns on**, with the real Windows permission prompt.

## Mira web 0.7.0

- **Extension on a real Jellyfin 12.1** (local):
  - compiled against Jellyfin 10.9, it is loaded by 12.1 (“Loaded plugin: Mira”);
  - installed from a repository as Mira for Windows does it (`JellyfinClient.InstallWebAppAsync`): repository added, extension downloaded and verified (MD5), Jellyfin restarted, `/Mira` served in 5 s;
  - files served from the DLL, resources cached until the next version, security policy on the page.
- **Full check in Chromium at iPhone size** (393 × 852, ×3 screen), on a new Jellyfin 12.1 prepared through its API: **18/18**. It covers:
  - sign-in through the form;
  - Home, movies, search and a series’ title page;
  - playback then the automatic next episode;
  - on Jellyfin’s side, the episode watched, the position kept and the reports (start, progress, end);
  - the resume offered and at the right place;
  - the QR code, the sidebar on a large screen and sign-out, without any JavaScript error.
- **The iPhone’s path, on the server side**: with Safari for iPhone’s answers, Jellyfin offers an fMP4 H.264 and AAC HLS (`CODECS="avc1.64001F,mp4a.40.2"`), video copied without re-encoding. ffmpeg reads its first 6 seconds.
- **In Safari, on a GitHub Mac** (workflow `web.yml`): Jellyfin 12.1 built from its sources, extension installed from its repository, Safari driven by `safaridriver` at iPhone size (430 × 932). **10/10**:
  - the MKV movie is converted by Jellyfin to HLS (`master.m3u8`) and played by Apple’s video player, without error;
  - the French subtitle, an `.srt` file beside the video, is chosen in the tracks menu and shown (“Bonjour depuis Mira web.”);
  - the video’s frame fits the picture (430 × 242 for 1280 × 720), so the subtitle shows on the picture;
  - the 10 s skip forward, then the position kept by Jellyfin on return (18.9 s);
  - the next episode starts on its own, then plays.
- **Defects found and fixed along the way**:
  - a call without parameters failed before reaching the server and read as “Server unreachable”;
  - the server refused `.mjs` modules, so the QR code page didn’t load;
  - the extension without Jellyfin’s base class that records its version was marked “malfunctioned”;
  - packaging used `md5sum`, missing from macOS;
  - in portrait, Safari showed the subtitle at the very bottom of the screen, under the iPhone’s home bar, far from the picture;
  - pages shorter than the screen scrolled by the height of the tab bar;
  - the tracks menu used Jellyfin’s English names (“French - SUBRIP - External”); it now says “Français · SRT · fichier externe”, like Mira for Windows and Mac;
  - while loading, the spinner showed through the pause button, a little lower.
- **Not verified**: a real iPhone. Safari on Mac has the same engine and the same video player, but not iOS full screen, adding to the Home Screen, AirPlay nor the safe areas of a notched screen.

## Mira for Mac 0.6.0

- **On Linux, same code and libmpv 0.37, virtual display (Xvfb, Mesa software OpenGL)**:
  - full check against the **real Jellyfin 12.1**: **21/21** (sign-in through the form with “127.0.0.1”, Home, movies, search, title page, series of 523 episodes in slices of 100, OpenGL then software playback with the picture visible on screen, 10 s skip forward, pause, back, automatic next episode);
  - check against the simulated Jellyfin (the one of the GitHub Mac): **23/23**, including the reports sent (start, progress, stop, volume reported capped at 100) and sign-out;
  - Jellyfin sees the “Mira” session on the “Mac” device, the title playing and its position.
- **Three defects found and fixed along the way**:
  - a file opened before mpv’s render context exists plays without a picture: Mira waits for this context;
  - mpv unbinds the framebuffer after drawing: without binding it again, Avalonia showed black;
  - with Mesa’s software OpenGL, mpv drew black 8 times out of 10 (floating-point buffers); 8-bit buffers on this render type only: 8/8 with a picture.
- **End of file without audio output**: mpv reports it as an error (`AO_INIT_FAILED`); it counts as a normal end, without an error message, and the next episode starts.
- **On a GitHub Mac (macOS 14.6 Sonoma, Darwin 23.6, Apple Silicon, virtual machine)**, the packaged app as in the disk image (`Mira.app`, libmpv and its libraries in `Frameworks`, ad hoc signature verified by `codesign --verify --deep --strict`):
  - check against the simulated Jellyfin: **23/23**;
  - sound through CoreAudio, volume at 150% through Mira’s filter (`volume=3.375`, `alimiter`);
  - playback, 10 s skip forward, pause, back, automatic next episode; picture visible on screen (86 to 100% colored pixels in the center);
  - this virtual Mac has no graphics processor: Avalonia draws in software there (“Compositor backend doesn't support GPU interop”) and the player switches to the software video output, which works. The OpenGL path, verified on Linux, remains to be seen on a real Mac.
- **Not verified**: a real Mac (Apple graphics processor, Retina screen, Gatekeeper at first launch); that’s the test planned with a user.

## Volume up to 200% 0.6.0

- **mpv 0.37 and libmpv** on Linux, test sound at −24.1 dB, PCM output:
  - mpv’s volume alone at 200 clips 60% of the samples of a loud sound, because it applies after the filters;
  - Mira’s filter (floating-point gain then `alimiter` at 0.95, with no gain of its own) clips none: peak at −0.4 dB.
- **Mira’s real `MpvEngine`** on libmpv, source paced in real time:
  - 150% from the opening: −13.5 dB (+10.6); back to 100% during playback: −24.1 dB; 200%: −6.0 dB (+18);
  - 60 then 180% set between two files: the next one starts at −8.8 dB (+15.3);
  - at 90%, the filter leaves the chain when the next file opens;
  - no clipped sample.
- **Filter refused** (mpv without `alimiter`, simulated with a filter that doesn’t exist): mpv drops it without cutting the sound, and Mira goes back to mpv’s volume alone (−13.5 dB at 150%). The same filter added before a file opened cut all sound: so Mira only adds it once the file is open.
- **Test**: split between mpv’s volume and gain, cap at 200, filter written with a decimal point, level sent to Jellyfin capped at 100.
- **Not verified**: the slider, the mark and the mouse wheel in the running interface, and listening on Windows.

## Series of more than 500 episodes 0.6.0

- **Real Jellyfin 12.1**, a series of 523 episodes (520 in season 1, 3 in season 2):
  - before: 500 episodes received, season 1 only, nothing after S01E500;
  - after: 523 received, S01E500 leads to S01E501 and S01E520 to S02E001. `isMissing=false` removes no present episode.
- **Tests**:
  - request without a limit and without missing episodes;
  - slices of 100 with the episodes’ numbers (or their position), a single page up to 100;
  - the slice of a given episode.
- **Not verified**: a real missing episode (it needs an online metadata source), and the picker in the running interface.

## Titles of an actor or a director 0.6.0

- **Real Jellyfin 12.1**, three movies whose actors and director come from `.nfo` files:
  - the title page gets each person with their identifier;
  - `personIds` returns exactly their movies: 2 for the actress, 2 for the director, and 1 with the year 2002 and the sort by title on top.
- **Test**:
  - cast without duplicate names, identifiers kept;
  - filter sent to Jellyfin with the other filters;
  - separate cache key per person.
- **Not verified**: the links, the pill and going back in the running interface.

## Window size remembered, Remove from Continue watching 0.5.9

- Release v0.5.9 published (stable): 8 files online. The published `mira-update.json` announces 0.5.9, with the size and SHA-256 of the three packages as GitHub gives them; its signature is accepted by `UpdateSignature.Verify` with `UpdateKeys.Trusted`, and refused on a modified copy. The Windows build of `main` is green.
- **Remove from Continue watching**, verified on a real Jellyfin 12.1 with a favorite movie. `POST UserItems/{id}/UserData` with only `PlaybackPositionTicks`:
  - at 20 s, the movie appears in “Resume”;
  - at 0, it leaves it; the favorite and the “watched” state stay unchanged.
  
  Jellyfin has no “hide from resume” feature: the next episode of a series (NextUp) is hidden by Mira, in the account’s cache (`resume-hidden`), until a more recent playback.
- Tests of the removal:
  - movie removed, and next episode of a removed series;
  - back after a new playback, here or elsewhere;
  - list kept beyond the 30-day cleanup, and “Undo”;
  - request sent to Jellyfin.
- Not verified: the menu and the undo in the running interface.

- Cause of the overflow: `FitToScreen` was called before `InitializeComponent`. The XAML then set 1480 × 930 again, so only the old switch to full screen took effect. The call now comes after `InitializeComponent`.
- Test: a remembered size taller than the screen is brought back to 90%; a size under the minimum is raised to the minimum.
- Not verified: opening on a real screen at 125 or 150%, and the size remembered after closing (the interface wasn’t run).

## Window, left bar and access away from home 0.5.8

- Release v0.5.8 published (stable): 8 files online. The published `mira-update.json` announces 0.5.8, with the size and SHA-256 of the three packages as `package.ps1` showed them; its signature is accepted by `UpdateSignature.Verify` with `UpdateKeys.Trusted`, and refused on a modified copy. The release page carries the short text taken from `WhatsNew.json`.
- **Real Jellyfin 12.1**, set up as Mira does it (access from the Internet disabled). The test addresses are given by `X-Forwarded-For`, with 127.0.0.1 declared as a trusted proxy for this test only:
  - before: a Tailscale address (100.101.102.103) is refused like an Internet address (8.8.8.8), as 503 with the reason `RejectDueToRemoteAccessDisabled`. A router address (192.168.1.20) passes;
  - after `AllowTailnetAsync`, without a restart: the Tailscale address passes, 8.8.8.8 stays refused, the router passes;
  - the other network settings are sent back as they are, and access from the Internet stays disabled;
  - a second call adds nothing, and a guest account can read this setting but not change it.
- **New tests**:
  - Tailscale range;
  - address kept only on the adapter named Tailscale (a 100.64 address from an Internet provider is ignored);
  - list of local networks (default values kept, no duplicate);
  - settings read then sent back whole; refusal for a non-administrator account;
  - window size on screens from 1440p to smaller than the minimum size.
- **Not verified**:
  - a real Tailscale on Windows: exact name of its network adapter, and the Windows firewall for this network. Jellyfin’s installer creates no firewall rule;
  - the interface wasn’t run on a real PC. CI draws the guide, the title page and the Downloads page with the new margins.

## Downloads moved, Refresh and Favorites 0.5.7

- Release v0.5.7 published (stable): 8 files online. The published `mira-update.json` announces 0.5.7, with the size and SHA-256 of the three packages as GitHub gives them; its signature is accepted by `UpdateSignature.Verify` with `UpdateKeys.Trusted`, and refused on a modified copy. The release page carries the short text taken from `WhatsNew.json`: summary, three points and a link to the CHANGELOG.
- **TorLink 1.9.0 seeding**, read in its code (`dist/index.js`):
  - at start, TorLink restarts each share marked `seeding` in `Data/seeds.json`; those marked `paused` stay stopped, without network;
  - a share whose files have disappeared starts downloading again. TorLink only stops it after 10 s (marked “missing”), and what it downloaded in the meantime stays in its folder. Hence the pause, set by Mira before starting TorLink.
  - Its `config.json` only contains `downloadDir` and `trackers`. The TUI has no control API.
- **End-to-end move** (Linux, Mira’s code):
  - an episode and its subtitle are moved and renamed for Jellyfin;
  - the download’s folder, emptied, disappears; TorLink’s one stays;
  - `seeds.json` goes from `seeding` to `paused` for this download only, once;
  - the log keeps the state after reloading.
- **New tests**:
  - between two drives, both modes move without leaving a copy, nor a `.mira-part`;
  - the emptied folder leaves TorLink’s one;
  - pausing the share: old form (identifier only) and new form, half-written file left as is, missing file;
  - download folder chosen only without `config.json`: beside the library folders, never inside, and nothing if two drives or a library at the root;
  - Refresh: scan followed to the end, scan finished between two reads, non-administrator account, scan that doesn’t start.
- **Real Jellyfin 12.1**, set up by Mira:
  - a movie added to `Films`, then Refresh: progress 12%, 48%, 97%, done in 2.7 s, and the list goes from 0 to 1 title;
  - a guest account: task unreadable and scan refused, without closing its session.
- **Not verified on a real PC**:
  - a real TorLink download moved while TorLink seeds it. Node.js opens its files allowing them to be moved, which is expected;
  - the look of the lit Favorites button and of the Refresh button, visible in the CI gallery (05-favorites).

## TorLink from the Downloads page, screens at the screen’s size 0.5.6

- Release v0.5.6 published (stable): 8 files online. The published `mira-update.json` announces 0.5.6, with the size and SHA-256 of the three packages as GitHub gives them; its signature is accepted by `UpdateSignature.Verify` with `UpdateKeys.Trusted`, and refused on a modified copy. The release page carries the short text taken from `WhatsNew.json`: summary, four points and a link to the CHANGELOG.
- **Node.js 24.21.0**:
  - SHA-256 of the win-x64 zip verified against `SHASUMS256.txt`;
  - GPG signature of this file verified with the keys of `nodejs/release-keys` (“Good signature”, Antoine du Hamel’s key);
  - real install by Mira’s code: 3 s after download, 106 MB, without corepack.
- **TorLink**: `npm install torlnk@1.9.0 --prefix … --ignore-scripts`, run with this zip’s npm, installs 226 packages in 11 s. `torlnk --version` answers `torlink v1.9.0` with `TORLINK_NO_WEBRTC`.
- **New tests**:
  - Node.js install from a zip in the official format; only `node.exe`, npm and `LICENSE` are kept;
  - no new download once installed;
  - tampered archive, path leaving the folder or zip without Node.js: refused, no file left;
  - major version read; npm arguments (exact version, `--ignore-scripts`, Mira’s folder); summary of npm errors.
- **Windows CI gallery**: Home, sign-in, Jellyfin install and what’s new requested at 1920 × 1032, 1536 × 826, 1280 × 688 (1080p at 100, 125 and 150%) and 960 × 600. Each fits whole in the window (`AssertFits`, which takes scaling into account). The guide, the Downloads page and the empty library fit in width.
  - The runner’s screen (1024 × 768) caps the window at 1044 × 788: the two largest sizes are rendered there at 1044 × 788, the 150% one at 1044 × 688. The sizes really rendered are written in `sizes.txt`.
  - Previews looked at at 1044 × 688: Home, Jellyfin install, Downloads page.
- **Maximized window**: the gallery maximizes the window and checks that its content covers exactly the screen’s work area, to within 1 pixel (`22-maximized`, measurement in `sizes.txt`). On Windows 10, this offset wasn’t applied; CI runs on a more recent Windows and doesn’t cover this case.
- **Not verified on a real PC**:
  - the full install from Mira (Node.js then npm on Windows) and the opening of TorLink;
  - the window maximized at start on a screen at 125 or 150%.

## “Continue watching” pictures 0.5.6

- Reported by the user: an episode of one season showed the picture of another season of the same series.
  - Cause, read again in Jellyfin 12.1’s code (`DtoService`): an episode without a backdrop inherits the one of the closest parent that has one, almost always the series.
  - Jellyfin doesn’t send the season’s thumbnail with the episode. A series thumbnail even has priority over the season’s in `ParentThumbItemId`.
- Real Jellyfin 12.1, test series with two seasons, with a series backdrop and a thumbnail for season 7 only:
  - the card of the season 7 episode shows the season’s thumbnail;
  - the season 6 one shows the episode’s picture (picture taken from the video by Jellyfin);
  - the banner keeps the series’ backdrop.
- Tests:
  - order of choice: episode thumbnail, season thumbnail then backdrop, episode picture, series backdrop; a movie keeps its backdrop;
  - seasons requested only once;
  - a single request for all season thumbnails, with `null` for a season without a thumbnail.

## Home, guide and what’s new 0.5.5

- Release v0.5.5 published (stable): 8 files online. The published `mira-update.json` announces 0.5.5, with the size and SHA-256 of the three packages as GitHub gives them; its signature is accepted by `UpdateSignature.Verify` with `UpdateKeys.Trusted`. The release page carries the short text taken from `WhatsNew.json`: summary, four points and a link to the CHANGELOG.
- Choice of the screen at opening tested:
  - new profile → welcome;
  - existing profile without a trace (update from 0.5.4 or before) → what’s new in the current version;
  - version seen → nothing; more recent version seen → nothing;
  - skipped versions → the last two, at most.
- Tests: short highlights for each version, entry present for the version being built, icons existing in Mira, release text (summary, points, link to the version’s CHANGELOG, under 1,500 characters), choice of the network address (virtual adapters and 169.254 left out, gateway first).
- Windows CI screenshots (demo gallery, offline): welcome, what’s new (one version, then two in a 1024 × 720 window), guide (top and bottom) and empty library, reviewed before publishing.
- Not verified: opening folders in File Explorer and Jellyfin’s page in the browser, copying the address and detecting the network address on a real PC.

## Jellyfin install: fixes 0.5.5

- First real test, on the user’s PC (Windows 11, 10.0.26200): Jellyfin’s installer showed “Could not start the Jellyfin Server service”, and Mira stopped.
  - Windows’ Application log however only shows one start of the service, successful (nssm: “Démarrage réussi”, 7:37 PM). The installer’s message was therefore a false alarm: `nssm start` returns before the service runs.
  - At the second try, Mira found Jellyfin running and finished the setup: account sign-in and library scan visible in Jellyfin’s log.
  - The same log showed “Token is required” on `/socket` every 30 s: Mira’s real-time listening was refused.
- Real-time listening, reproduced on a real Jellyfin 12.1:
  - `/socket?api_key=…` gets 403 and the log writes “Token is required”. 12.1’s code only accepts `api_key` with `EnableLegacyAuthorization`, disabled by default.
  - With the session in the `Authorization` header, the connection is accepted (“WS request” in the log).
  - New test: a local WebSocket server checks that the address is `/socket` without a token, that the header carries the session and that a library change arrives. It fails on the old code.
- Read in the installer’s script (`jellyfin.nsi`):
  - no `SetRegView 64`: the `Software\Jellyfin\Server` key is written under `WOW6432Node`, which Mira didn’t read;
  - on a silent update of an existing Jellyfin, the installer goes on without asking (`/SD IDOK`);
  - its error messages (`ShowError`) show even when silent.
- Observed on a real Jellyfin 12.1 at start, answers of `System/Info/Public` in order:
  - nothing;
  - 200 in camelCase with `startupWizardCompleted: false`, from its startup page;
  - 503 “Jellyfin Server is loading”;
  - 200 from the server, in PascalCase.
  The old detection saw the second answer as “another program”. The new one goes through nothing, starting, ready. With it, the full test started during the startup succeeds: wait, wizard, sign-in, three libraries. It was done with an accented name and a path containing a comma.
- New tests:
  - detection: nothing, 503 with or without text, Jellyfin 12 startup page, web page, 404, JSON without `Id`, server ready;
  - port taken or free;
  - Jellyfin’s log: cause of a startup failure (Kestrel exception) read, ordinary errors, older failures and old files ignored.
- Not verified: reading the 32-bit registry and the service on Windows (no Windows in the container); the real cause of the failure at the user’s.

## Jellyfin installed by Mira 0.5.4

- Release v0.5.4 published (stable): 8 files online. The published `mira-update.json` announces 0.5.4. The size and SHA-256 of the three packages in it are those GitHub gives. Its signature, downloaded from the release, is accepted by `UpdateSignature.Verify` with `UpdateKeys.Trusted`.
- Installer behavior noted in its NSIS script (`jellyfin-server-windows`): when silent (`/S`), `JellyfinServer` service through nssm under Network Service, started at the end; key `HKLM\Software\Jellyfin\Server\InstallFolder`; elevation required. Address and SHA-256 of `jellyfin_12.1_windows-x64.exe` taken from the `Jellyfin.Server` 12.1 winget manifest.
- Real test of `JellyfinSetup` against a new Jellyfin 12.1, built from its sources on Linux:
  - wizard finished; administrator account created; Films (movies), Séries and Animes (tvshows) libraries on the right paths; language fr/FR; remote access disabled;
  - `MediaLibraries.FromJellyfin` finds the three folders for TorLink;
  - a retry with the same account succeeds; with another password, it is cleanly refused;
  - during its startup, the server answers 503: this case is handled.
- New tests:
  - wizard filled in by a fake server with answers noted on Jellyfin 12.1;
  - retry without a duplicate library; other password refused; failure of a library reported;
  - invalid account refused without a request; path with a comma; server missing or another program on the port.
  - On Windows (CI): the service’s read right on the root, then on a linked file and a moved file; no right added in a folder that doesn’t grant it.
- Not verified:
  - the download from `repo.jellyfin.org` (unreachable from the container);
  - the silent install and the Windows permission on a real PC;
  - the service reading the folders;
  - the screen itself.

## mpv engine installed by Mira 0.5.4

- Pinned build: `mpv-dev-x86_64-20260927-git-a1bf4b6559.7z` (31,487,676 bytes). Downloaded from `downloads.sourceforge.net` (redirect to a mirror): its SHA-1 and MD5 are those SourceForge publishes for this file. Pinned SHA-256: archive `3a80c48d…74ac5c`, `libmpv-2.dll` `0a81c004…de6f6a` (120,812,544 bytes, identical to the extraction by 7-Zip).
- Real test on Linux with Mira’s code: download, checks and extraction in 10 s; only the library, `version.txt` and `SOURCE.txt` remain in `data\mpv`; a second call downloads nothing.
- New tests: install from a small archive in the format of mpv builds, no new download once installed, tampered archive, too long, library with another fingerprint or missing file (404) never installed and with no file left; search order (chosen path, then Mira’s engine).
- Not verified: the installer with the box ticked on a real Windows, Mira loading this `libmpv-2.dll` and a playback (no Windows in the container), the interface (button, notice at first playback).

## Stability 0.5.4

- Code review looking for possible failures. Fixed: SQLite errors bubbling up to the player (error window repeated during playback), send lock never released after a storage error (closing stuck), damaged local database that prevented opening the account, WebSocket listening stopped by an unexpected message or restarted every 2 s, empty device identifier, mpv command on an already destroyed player.
- New tests: storage failing during sync (no exception to the player, error shown, closing in under 10 s), unreadable database set aside and recreated, empty device identifier replaced. The first two fail on the previous code: SQLite exception, then “file is not a database”. A separate test on Linux shows closing **stuck** with the old `SyncService` when storage fails during a session, and finished with the new one.
- `Mira.Core` tests run on Linux; WPF code and test program built without warnings against the Windows Desktop reference assemblies. The pull request’s Windows CI gives the result of the full suite.
- Not verified: WebSocket listening (no test WebSocket server), the guard of `MpvEngine.Command` (libmpv missing from the container) and the interface itself.

## Sign-in, title pages and caches 0.5.3

- GitHub Actions on Windows (manual run of the branch, [run 36787107476](https://github.com/sasou-web/Mira/actions/runs/36787107476)): Release build without error or warning, **51 tests** passed, archive, portable executable and installer produced. New tests: typed addresses (without scheme, with port, IPv6, copied from the web page, server named “web”); server search (order HTTPS → 8096 → HTTP, redirect to HTTPS kept, silent HTTPS dropped after the grace period, Emby, 404, Jellyfin 10.8, certificate refused, server unreachable, only `System/Info/Public` requested); cast, directing, similar titles and sort by last played; disk cache (budget, least recently seen files, abandoned `.tmp`, file read again “touched”); SQLite pruning (old pages forgotten; Home, resume, history and pending sends kept); in-memory pictures (budget respected, the oldest freed then read again from disk without a new download).
- The relevant `Mira.Core` tests were also run on Linux, and the WPF code built against the Windows Desktop reference assemblies during development.
- Release v0.5.3 published (stable): 8 files online. The published `mira-update.json` announces 0.5.3, with the size and SHA-256 of the three packages as GitHub gives them; its signature, downloaded from the release, is accepted by `UpdateSignature.Verify` with `UpdateKeys.Trusted`. The real update of a copy installed at 0.5.2 hasn’t been observed yet.
- Not verified: the interface itself wasn’t run nor captured (“Similar titles” row, “With” / “Directing” column, sign-in labels), nor tried with a real Jellyfin server, a NAS, an HTTPS proxy or a self-signed certificate. The player, motion, Windows and TorLink checks weren’t run again; the player’s code didn’t change. The gallery screenshots don’t show the title pages’ new features yet.

## Automatic updates and playback bar 0.5.2
- Release build without error or warning; **45 tests** passed. New: player labels (“Episode 3 / 12”, tracks named in French, speed, offset); signed manifest accepted, refused if modified, malformed or signed by another key; only a more recent signed version is offered; each redirect checked, resume of an interrupted download, unexpected answer refused; two-step copy with rollback, folder intact if preparation fails, result announced once; install type recognized and folder replaced without touching `data`. The real TorLink test (`--torlink-integration`) wasn’t run again: TorLink didn’t change.
- End-to-end check `tools/update-check.ps1`: **13 checks** passed, without GitHub or the real key. `tools/package.ps1` produces the 0.5.2 and 0.5.3 packages; the 0.5.3 manifest is signed by a throwaway test key and served by a local fake GitHub (127.0.0.1); each Mira has an isolated profile. Archive: 0.5.3 found, downloaded, verified (signature, size, SHA-256) and installed on close; the new version finds itself up to date and deletes its download. Portable executable: replaced in place and reopened at 0.5.3 by “Restart”. Installer: 0.5.2 installed silently in a test folder and updated by its installer on close; reinstalled, then updated by “Restart”, with Inno Setup’s progress and Mira reopening. `data` stays intact each time; silent uninstalls; Start shortcut and Windows identity of `dist/Mira` restored. Results: `.artifacts/update-check/result.txt`.
- Two problems found by these runs, fixed. Inno Setup pads the installer’s product name with spaces, which the version check refused. In the check itself, Inno Setup’s uninstaller deletes `unins000.exe` last, from a temporary copy: an immediate reinstall in the same folder lost its uninstaller, and Mira updated itself there (successfully) as a plain folder. The check now waits for the uninstaller to end and checks that each step goes through its own update type.
- The built-in public key matches the real signing key, kept outside the repository and protected by DPAPI.
- Release v0.5.2 published (pre-release): the 8 files online have the size and SHA-256 of the prepared files (fingerprints given by GitHub), manifest signed by the real key. A copy of the same code numbered 0.5.1, started normally in a test folder, found this release on GitHub, downloaded the archive, verified signature, size and SHA-256, then moved to 0.5.2 on close. Test folder deleted and Start shortcut restored afterwards.
- Independent review of the diff: **21 remarks** fixed before these runs. Among them: checking each redirect, resuming downloads, two-step copy with rollback, locks during an update (`SetupMutex` included), tracking the result in `data/updates/pending.json`, stopping automatic tries after two failures, reopening by Inno Setup even after a failure, player menus as radio buttons and announcements to screen readers.
- Playback bar: **67 player checks** passed on the Release build, then on the installed `dist/Mira` (synthetic chaptered media, external `.srt` track). New: title, second line and time above the bar, buttons below, aligned with the title and the time; time “0:02 / 0:18”; bar cut into 3 chapter segments separated by 3 px; current chapter named above the time (“Opening”, “Partie A”, “Ending”); volume slider expanded when hovering the speaker, then folded; level shown after a keyboard change; subtitles menu (loaded track ticked, “Off” passed to mpv without closing the menu, turned back on, closed by the same button); ⋮ menu (speeds, subtitle offset, playback info). Screenshots: `.artifacts/player-v052-check`.
- `dist/Mira` distribution moved to 0.5.2 from the check’s 0.5.2 archive, backup of 0.5.1 in `.artifacts/previous-mira-0.5.1`. The profile’s 468 files are unchanged after the copy and after the player check (SHA-256); the archive’s 500 files are in place. Project and Start menu shortcuts registered again. This copy is recognized as a folder: it will update from the archive of the next versions.
- Gallery screenshots 07 to 10 (settings) redone in demo mode.
- Not verified: the SmartScreen warning, the install on another PC, a Windows shutdown during an install, the interactive wizard and switching directly from one player menu to the other with the mouse. Copies 0.5.1 and earlier have no automatic update: 0.5.2 must be installed once by hand. Windows (0.4.9), motion (0.5.0), end-to-end TorLink and installer (0.5.1) checks not run again.
## Installer and portable executable 0.5.1

- `tools/package.ps1` produces the archive (75.0 MB), the portable executable (76.4 MB) and the Inno Setup 6.7.3 installer (53.7 MB), each with its `.sha256`. Build without warnings; **40 tests** passed. SHA-256: installer `472d4c9cf1912114e541c52a856791d1f8d56970ac60cbf095dbee4c38a5c6a6`, portable `3ecd66ad3b37eb8cb5e106981a211d5ad3dd810997bb04e29b247192f20262b4`, archive `326b25364e946c2fb5912695bbde7f5eae0943cd3a0dcb1b089b330e29b959d8`.
- The published portable executable is identical, byte for byte, to the one that passed the checks below; the 478 binaries of the archive and the installer are identical to those of the locally installed version.
- Portable executable, copied alone into an empty folder: the 14 demo views show. The TorLink check passes **23 checks with the real keyboard** there: page served from resources, typing, Alt + ←, sorting, reclassifying and stopping. The **50 player checks** pass too, with mpv’s key bindings written in `data/app`. No file appears beside the executable outside the profile.
- Installer, in silent mode in a test folder: **12 checks** passed. Install for the current user without administrator rights, entry in installed apps, Start shortcut and Windows identity, installed Mira starting; reinstall over it that keeps `data`; uninstall that removes the app, its entry, its shortcut and its identity, and keeps `data`. The local `dist/Mira` Start shortcut is then restored. Results: `.artifacts/installer-v051-test/result.txt`.
- With another TorLink open on the PC, the check verifies the page (served from resources, with xterm.js and its stylesheet) and the refusal of a second instance, then stops.
- Not verified: the wizard’s pages and the data deletion question in interactive mode, the SmartScreen warning, and the install on another PC or another Windows account.

## TorLink 0.5.0

- Full Release build without error or warning. **40 tests** passed with `-- --torlink-integration`, 39 without the real TorLink test. New: real release names (movies, episodes, packs, fansubs), subtitles, extras and Windows names, sorting plan into existing folders, hard link and copy between drives without replacing or writing outside the library, nested root never deleted, log (activation, waiting for files, missing folder, reload, no magnet link), file already present never moved by “Sort”, chosen type kept by “Try again”, resume of a partial move, unreadable log set aside, exact files of a `.torrent`, simulated Jellyfin API (one request for all followed titles, anime library recognized by whole word), pseudo-console, and real TorLink 1.1.1 in the pseudo-console with an isolated state (`TORLINK_STATE_DIR`).
- An independent review of the diff raised, then saw fixed: present files adopted then moved by “Sort”, pruning of a nested root, reclassification forgotten by “Try again”, partial move impossible to resume, log overwritten on a read error (all covered by the tests above), as well as a possible second instance through “Open in a window” and reads of TorLink’s state that could make its atomic replacement fail (fixed, without a dedicated test).
- End-to-end check `tools/torlink-check.ps1`: **23 checks** passed with the real keyboard on the first installed version, then **22** on the final installed version (`dist/Mira`) and on the Release build, with browser keyboard events (check window without foreground). Real TorLink in the page, profile, TorLink state and library isolated, demo mode without Jellyfin. Covered: opening from the rail, a single instance, Esc left to TorLink, typing received by TorLink and end shown, restart, movie and subtitle, side files left in TorLink, hard link (two links on the same file), episode, anime, game ignored, “To Jellyfin” list, reclassifying as anime with removal of emptied folders, second pass without duplicates, settings, Alt + ← from the terminal, Esc outside the terminal, stopping TorLink and no WebView2 process left after Mira. Results and screenshots: `.artifacts/torlink-check`.
- WebView2 passes Alt + ← to WPF as a left arrow with Alt held, not as a system key: Mira recognizes both forms. If WPF doesn’t get the key, the page asks for going back itself; this path is verified by browser keyboard events, used when the check window doesn’t have the foreground (22 checks passed on the Release build).
- Read-only on the personal server (Jellyfin 12.1.0) with Mira’s code (`--jellyfin-live`): three libraries read and matched to Films, Séries and Animes, folders present on this PC; the most recent movie video is found by its exact path, as for “See”. No report or change was sent to the server.
- Non-regression: **50 player checks** passed (synthetic chaptered media), renders and **9 motion checks** passed on the real library read-only. In demo mode, the motion check fails at the hover handoff, the same way with the installed version 0.4.9: the fake catalog is too short to scroll by a row. The 24 Windows checks weren’t run again.
- Standalone distribution installed in `dist/Mira`, backup of 0.4.9 in `.artifacts/previous-mira-0.4.9`, then final version reinstalled after the review. The profile’s 468 files are unchanged after the copy and after the TorLink check of the installed version (SHA-256); the 494 distributed files match the prepared version, terminal page and `WebView2Loader.dll` included. Project and Start menu shortcuts registered again.
- A run of the check failed at the terminal’s start, probably because another TorLink was running on this PC then: Mira does refuse a second instance. The check now reports it from the start, and the page offers “Try again” if the terminal doesn’t show within 20 seconds.
- Archive `Mira-0.5.0-win-x64.zip` (SHA-256 `d3dc735e6d4c1393b3ece40f40c31c614d1f9b995daa5a552b5a41bafe3068ca`): 510 entries, no profile file. Its 493 app files are identical to those of the verified installed version; start and export of the 14 demo views passed from the extracted archive. The TorLink check wasn’t run again there: a TorLink was running in this PC’s Mira then, and the check rightly refuses to start a second one.
- Real TorLink state (`history.json`, `queue.json`, `seeds.json`, `config.json`) and `MEDIAS` folders unchanged by the tests.
- Not verified: a real download sorted end to end, `Library/Media/Updated` on the personal server (Jellyfin’s real-time monitoring is on there), the mouse “back” button over the terminal, copy-paste, the absence of WebView2 (“Open in a window”), a Docker or remote server, and a copy to another physical drive or a network share (the copy path is tested on the same NTFS volume).

## Smoothness and resume 0.4.9

- Standalone distribution installed in `dist/Mira`, backup of 0.4.7 in `.artifacts/previous-mira-0.4.7`. The profile’s 184 files are unchanged after the copy (SHA-256), the 478 distributed files match the prepared version. Project and Start menu shortcuts verified: installed executable, dedicated logo and AppUserModelID `Mira.Desktop`.

- Release build without error or warning; **30 tests** of library and sync passed. Additions: chronological order, priority of a resume over NextUp, a series’ activity after the end of an episode, persistence and merge without overwriting a more recent server date.
- **50 player checks** passed on silent synthetic media: the new checks go through playback → stop → row rendered, then playback → mini player. They check that the last movie watched moves to the left and that horizontal scrolling goes back to the start. Results: `.artifacts/player-v049-check/result.txt`.
- **9 motion checks** passed: same animated value for outline and zoom, interpolation, reversal mid-way without a jump, hover handoff under a fixed pointer during inertia, stable card dimensions. Results: `.artifacts/visual-v049-final/motion-checks.txt`.
- Library renders at 1024, 1440 and 1920 WPF units; sign-in and account screens inspected at 1440 × 900, sign-in at 1024 × 700. Rounded carousel indicators and intermediate progress verified. Renders: `.artifacts/visual-v049-final`.
- Mouse test in the validation window: hovering a “Continue watching” card, mouse wheel from that card, handoff to a poster then to the next row without moving the pointer. No personal media played or changed.
- **24 Windows checks** passed again, with the real media broker and the synthetic video. Results: `.artifacts/windows-v049-check/result.txt`. The limits of Explorer’s visual check described below still apply.
- The sorting uses the available Jellyfin dates and keeps new playbacks locally. It doesn’t rebuild the missing dates of episodes finished before this version. The checks verify the continuity of animations; they aren’t a frame-rate measurement on every configuration.

## Windows integration 0.4.8

- Release build without error or warning; **27 tests** of library, sync and behavior passed.
- **24 Windows checks** passed through the real Windows media broker and libmpv on silent synthetic video: publishing the title, episode and readable poster, repeated idempotent pause/resume, system seek back and forth, ten-second commands, next available, playing the next episode, minimizing to the notification area, command while the window is hidden, restore by a second process and cleanup on stop/close. Results: `.artifacts/windows-v048-final/result.txt`.
- **48 player checks** passed, including full screen, mini player, shortcuts, volume and marking watched when moving to the next one. Results: `.artifacts/player-v048-check/result.txt`.
- The Windows tests don’t register a Start shortcut and publish a separate validation identity. No personal episode played or changed by these tests.
- Limit of the automated check: the Windows media card and events are checked through the system API; the look of the taskbar preview, Explorer menus, a physical keyboard key and Bluetooth devices aren’t covered by this check. The glyph colors follow the system theme, but a theme change wasn’t triggered during the tests.

## Fix 0.4.7

- Release build without error or warning; **28 tests** passed, including libmpv playback + authenticated HTTP on the fake server. New tests: end credits before 90%, persistence after reopening, resume of marking watched without sending again the stop already accepted.
- **48 player checks** passed with a silent synthetic video: added the real “next episode” command paths mid-episode, the credits shortcut, the countdown deadline and the natural end with an old native position. The first ones keep the resume, the last three mark watched and clear the resume.
- **9 motion checks** and renders at 1024, 1440 and 1920 WPF units passed. Real pointer movement immediately lifts the scroll guard. Hovering resume cards and posters, then mouse wheel from a card, verified with the mouse on a separate profile.
- New logo and 36 Mira vector icons: check sheet in `.artifacts/visual-v047/mira-icon-family.png`, multi-size PNG and ICO from the same drawing. Centering of the side buttons, right margin of posters and compact genre filter fixed.
- Startup veil declared from the XAML, lifted after preparing the first pictures with a bounded wait; card cascades are removed at opening. Lifting the veil is verified by the visual check. The perceived duration of the first launch without cache still depends on the server.
- No personal episode marked watched during the tests. Old progress isn’t converted in bulk: progress alone doesn’t tell whether the episode was really finished.

## Fix 0.4.6

- Windows open, close and minimize animations: the window keeps the native caption style (`WS_CAPTION`), entirely covered by the custom frame. The player check verifies this style, the absence of a native title bar (client area equal to the window), then its removal in full screen and its return when leaving: 43 checks passed. Visual check and 8 motion checks passed.
- The animation itself is played by Windows and wasn’t observed automatically; it depends on the “Animation effects” setting, on on this PC.

## Fix 0.4.5

- Cause of the “two-step” hover of the player buttons measured with the `--perf-probe` probe of the player check (window 1480 × 930 WPF units, DPI 200%, 240 Hz screen). Each mouse movement recreated the clipping of the controls layer and moved its transparent window again: during a hover, the animation fell from 231 to **107 frames/s** (27% of a core). After the fix: **231 frames/s**, 9% of a core. The transparent window itself isn’t limiting (231 frames/s, 240 in mini player); reading an mpv property costs 0.01 ms.
- Other causes of jerks removed: play / pause icon changed on click (and not at the next reading), a single curve for the hover’s background and scale, SQLite write of progress off the UI thread, banner color computed in the background, progress bar redrawn by 0.4-pixel steps.
- Release build without error or warning. **26 tests** passed, including libmpv + HTTP integration. New: title stopped beyond 90% counted as watched, refused report (404) that doesn’t block the queue, stop report sent before closing.
- Player check: 40 checks passed, including the loading indicator and busy cursor lifted at the first frame, the “Next episode in 10 s” countdown during the credits and its cancellation.
- Visual check and 8 motion checks passed; empty search state, title page with “Mark as watched” button verified on the renders.
- Not verified automatically: the context menu opened with the mouse, the Alt + ← shortcuts and the mouse “back” button, automatic saving of settings, going back to the title page after a real Jellyfin playback, the episode offered by `Shows/NextUp` on the personal library and marking watched / unwatched on the server side (no change was made to the personal library).

## Fix 0.4.4

- Release build without error or warning. **23 tests** passed, including libmpv + authenticated HTTP integration with the fake server. New tests: typed `/MediaSegments` request and filtering, classification of chapter titles (opening, ending, recap, preview, credits by position, cold open excluded), priority of server segments, active section and switch to the next episode.
- Player check (`--player-check`): 37 checks passed on the built version and on `dist/Mira`, with `.artifacts/lecture-chapitres.mkv` (18 s, chapters “Opening”, “Partie A”, “Ending” added by ffmpeg). New features covered: icons not cropped and centered in each button, bar moving between two mpv readings, Space pausing even when the event targets the +10 s button, right arrow skipping forward 10 s from any button, hiding controls and cursor while paused, excluding the controls layer from screenshots, chapter dots, “Skip intro” button and jump to the end of the section, mini player rounded in its corner, snapping to the nearest corner, tucking behind the tab with the video kept, back through the tab.
- Visual check (`--visual-check --demo`) and 8 motion checks passed; poster hover no longer has a caption and can come back on the poster under the pointer after scrolling. Renders of the controls layer in `.artifacts/player-v044-check-result`.
- Not verified automatically: the real mouse drag gesture (the check calls the drag end directly), the rounded corners of the native video and the shadow as composed by Windows, the real render of a Win + Shift + S screenshot, and Jellyfin segments on the personal library (no playback or change was made there).

## Fix 0.4.3

Native rounded frame and outline verified on Windows 11 (DPI 200%), in the library and with a silent synthetic video. Maximize, full screen and back to windowed tested with mouse and keyboard. Windows’ invisible resize margins are compensated in maximized mode to keep the buttons in the visible area. The 23 WPF/libmpv checks pass with the new frame.

## Fix 0.4.2

- Release build without error or warning; 23 WPF/libmpv assertions passed. Vertical alignment of volume/slider/pause button, bottom margins, interruption of the fade and keeping the native surface included.
- Renders of the real library at 1024, 1440 and 1920 WPF units passed; 8 motion/navigation checks passed. Results in `.artifacts/player-v042-check-result` and `.artifacts/visual-v042`.

- Real mouse tests on a silent synthetic video, separate profile: hidden controls come back without Esc in windowed and full screen. `GetCursorInfo` confirms the system cursor hidden during inactivity, then visible after movement.
- Same controls surface observed before/after switching from windowed to full screen 3840 × 2160 (DPI 200%), then to the mini player 380 × 214 WPF units. The video keeps its instance and its position.
- Pause click, settings panel, closing the panel with Esc, minimizing with its button, scrolling with the mini player and built-in hover verified in the test window.
- Hover seen on a poster then removed on the mouse wheel; no panel covers the other posters. The top bar stays transparent while scrolling.
- The opt-in log `player-ui-probe.jsonl` only records interface states for `--qa-window --demo --test-media`, in the test profile. It isn’t on in normal use.

## Fix 0.4.1

Cleaner banner: removed the redundant shortcuts, the mention above the title and the carousel’s pause button. Settings cover the whole window, including under the rail, with the content’s animation independent of the background.

## Results of 0.4

- Release build: no error or warning at the last check.
- Automated tests: **20 passed**, including one with the mpv.net library really installed (`libmpv-2.dll`).
- Version 0.4 inspected on the real library: Home, catalog, title pages, hover previews and settings, at 1024, 1440 and 1920 pixels wide. Renders made by the app off the desktop with a separate validation profile.
- Synthetic mouse wheel routed from a “Continue watching” card: event handled by the page, move observed from about 67 to 108 pixels during the interpolation.
- Banner rotation, duration segment at 50%, card instances kept after refresh, preview at the bottom edge, reduced motion mode and fast page changes verified in WPF.
- Title page title shown before any network wait; 1 ms observed in the preloaded profile. This measurement concerns the initial presentation, not the full load nor the PC’s display rate.
- Menus, Ctrl+K shortcut and “Samurai” search verified in the Windows window in 0.2 (one result: Samurai Champloo).
- Real facets: 21 genres and 16 years fetched. The menus include an extra “All” option.
- Playback of a 1280 × 720 synthetic video at 24 frames/s in the built-in mpv surface: picture shown and progress active.
- Pause verified, then move from 13.25 s to 3.25 s with the −10 s command.
- Library loaded in the app from an isolated test HTTP server.
- HTTP stream requiring authentication: loaded in libmpv, resume at 2 s, pause, move to 8 s, audio track detection and stop.
- Reception of the `Sessions/Playing`, `Sessions/Playing/Progress`, `Sessions/Playing/Stopped` reports in this order, with the expected positions.
- The personal library was viewed read-only for the visual validation. The playback and sync tests use a separate fake server.
- 0.4 player: 18 WPF/libmpv assertions passed on synthetic media: seek, mini player, navigation, maximize, physical dimensions of full screen and restore, mute/unmute (including zero volume), speed, subtitles, menu placement at 200%, native shortcuts and continuity of modes between videos.
- Mouse check: full screen on the 3840 × 2160 monitor at 200%, back to windowed, speed menu and 1.25× selection, panel within the player’s bounds, minimizing into the library and navigation keeping the video.
- Video controls overlaid seen in windowed and mini mode; disappearing during idle playback and reappearing on pause. Silent synthetic media only.
- Embedded Nunito Sans font resolved by WPF; renders exported at the real DPI, not resampled to 96 DPI.
- Previews: zoom reduced to 2.5%, compact panel, lower bounds respected. White palette and accent computed per artwork.

## Tests covered in 0.4

1. Address validation and normalization preserving a proxy sub-path.
2. JSON authentication, session header and search encoding.
3. Persistence, order and coalescing of the sync queue.
4. Concurrent old response with more recent progress.
5. Local resume and isolation between accounts.
6. Responsive save despite a slow server.
7. Reconnection and ordered delivery after an outage.
8. Session storage protected by DPAPI.
9. Progress bounds and episode numbering.
10. libmpv + authenticated HTTP + sync integration.
11. Filter parameters, encoding, pagination and separation of filtered caches.
12. Recursive loading of genres and extraction of years.
13. Migration of old settings and persistence of appearance/resume preferences.
14. Equivalent interpolation with time steps simulating 30, 60 and 144 frames/s.
15. Scroll reversal and bounds after the content size shrinks.
16. Carousel duration, pause, resume and reset after selection.
17. Sharing of preload requests and metadata invalidation.
18. Retry after a temporary preload error.
19. Extraction of distinct, readable tints for red and blue pictures.
20. White fallback for neutral and transparent pictures.
21. Jellyfin segments: requested types, unknown or too short sections ignored.
22. Chapter titles turned into sections to skip.
23. Merging of sections, active section and “Next episode” at the end of playback.
24. Local stop beyond 90%: title watched, resume at zero.
25. Permanently refused report dropped without blocking the queue.
26. Stop report delivered while the sync service closes.

## Reproduce

The commands for the tests, the Mira web checks and the app’s harnesses are in [TESTS.md](../TESTS.md).

## To check with the user

Resume between Mira and its other clients, subtitles usually used, possible HDR, screen(s) and audio device. Full screen was verified on the current monitor. Real episode paths and multi-screen setups remain to be tried with the usual content and devices.
