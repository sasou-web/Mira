---
title: Privacy
short: Privacy
description: What Mira sends, to whom and when. No account, no analytics, no telemetry.
platforms: Windows, Mac, iPhone
---

Mira has no account, no ads, no analytics and no telemetry. It talks to your Jellyfin server, and to a few services only when a feature needs them, each listed below. The code is open: [see for yourself](https://github.com/sasou-web/Mira).

> [!TIP]
> The same list is in the apps: **Settings → Privacy** on Windows, Mac and iPhone, each connection with what it sends and its switch, where it has one (the update check, Built-in Tailscale, and on Windows the check for a newer Jellyfin).

## Always

| Who | To | What |
| --- | --- | --- |
| Mira, every device | **your Jellyfin server** | your library, playback and progress, with your session's token in a header, never in an address (except the iPhone's player stream, which Apple's player cannot send otherwise) |
| Mira for Windows and Mac | **GitHub** (`api.github.com`) | the update check, at startup then every 6 hours: the request itself (your IP address, Mira's version), nothing else. Turn it off in **Settings → Updates → Update automatically** |

Before you sign in, Mira looks for Jellyfin on this computer, on your home network (Jellyfin's discovery question, never beyond your network) and on your Tailscale devices. Nothing identifying is sent; your password goes only to the address you sign in to.

## Only when you use a feature

| Feature | Contacts | What |
| --- | --- | --- |
| **Built-in Tailscale**, once you sign in to it | Tailscale's coordination servers and relays; your router (UPnP, NAT-PMP, PCP), asked to open a port for a direct connection rather than through a relay | like the Tailscale app: the account you chose, the device's name, its system and IP addresses, its public key. Videos stay end-to-end encrypted. No logs sent |
| **The mpv engine** (Windows) | SourceForge | the download of the engine, SHA-256 checked |
| **Installing or updating Jellyfin** (Windows) | `repo.jellyfin.org`, GitHub (winget's catalogue, once a day on the Jellyfin PC, off in **Settings → Privacy**) | the download, and whether a newer version exists |
| **Automatic subtitles** | OpenSubtitles, SubDL, from your server | the file's fingerprint and size, IMDb/TMDb ids, season, episode, title, year, language. Never the file's name |
| **Search for subtitles…** with another Jellyfin plugin | that plugin's service, from your server | what the plugin sends; Mira tells you before |
| **Your party link** through Tailscale Funnel | Tailscale's relays, then your guests | your guests' traffic to Mira's page, encrypted to your PC. Your PC's Tailscale name becomes public |
| **A trailer from the web**, at your click | the trailer's site (often YouTube), in your browser | what your browser sends (your IP address, the site's cookies). Mira loads nothing from it before your click and has no web view; a trailer kept on your server goes through it alone |
| **AltStore** (iPhone) | Apple, `miracinema.app`, GitHub | your Apple ID to Apple only, for the free certificate; Mira's source and app |

## On your devices

- Your Jellyfin password is never stored: only the session. Windows protects it for your account; Mac and iPhone keep it in the keychain (on iPhone, this device only, never in backups).
- Built-in Tailscale's keys and state are encrypted on the device. Its log stays there and holds no sign-in page.
- Nothing about you or your library is ever collected.

Full details, for each request: [SECURITY.md](https://github.com/sasou-web/Mira/blob/main/SECURITY.md).

## Report a problem

Found a security issue? Use **Security → Report a vulnerability** on [Mira's GitHub repository](https://github.com/sasou-web/Mira/security), never a public issue. Never post a token, a session file or a password anywhere public.
