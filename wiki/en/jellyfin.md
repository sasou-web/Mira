---
title: Set up Jellyfin
short: Set up Jellyfin
description: Let Mira install Jellyfin on a Windows PC, name your videos so Jellyfin recognises them, and get the server ready for your other devices.
platforms: Windows (server PC)
---

Jellyfin is the server: it keeps your films and series, their artwork and everyone's progress. Mira is the player. Jellyfin runs on one computer that stays on; Mira runs on every device you watch on.

Already have Jellyfin 10.9 or later? Skip to [Get the server ready](#get-the-server-ready).

## Let Mira install Jellyfin

On the Windows PC that will keep your videos, in Mira's sign-in screen, choose **No server yet: Mira installs Jellyfin on this PC**, then:

1. **Videos folder**: `Videos\Jellyfin` by default, or another one with **Browse** (a storage drive, for example). Mira creates **Movies**, **Shows** and **Anime** in it.
2. **Username** and **password**: they become Jellyfin's administrator account.
3. **Install**. Mira downloads Jellyfin's official installer from `repo.jellyfin.org`, checks its SHA-256 against the one Jellyfin publishes, and runs it. Windows asks for permission once: Jellyfin installs for the whole PC, as a service that starts with Windows.

Mira then fills in Jellyfin's first-run wizard: your administrator account, three libraries (Movies, Shows, Anime), access from the Internet turned off. Then it signs you in.

> [!TIP]
> If Jellyfin's installer shows "Could not start the Jellyfin Server service", choose **Ignore**. The service usually starts right after, and Mira carries on as soon as Jellyfin answers.

Jellyfin's service runs as Windows' **Network Service** account, which cannot read your personal folders. Mira gives it read-only access to Movies, Shows and Anime, and nothing else in the folder you chose.

## Name your videos

Jellyfin finds the title, poster and summary of each video from its folder and file name. Put one folder per film, one folder per series with a folder per season:

```text
Movies\Spirited Away (2001)\Spirited Away (2001).mkv
Shows\Arcane\Season 01\Arcane - S01E03.mkv
Anime\Frieren\Season 01\Frieren - S01E01.mkv
```

The year in brackets avoids mix-ups between films of the same name. Subtitle files next to a video, with the same name and a language (`Spirited Away (2001).en.srt`), are found too.

New files appear after Jellyfin's next scan. **Refresh** in Mira's library asks Jellyfin to scan right away (administrator account).

> [!NOTE]
> Copy or move videos into these folders with File Explorer: they take the folder's access rights. A file moved by another program can keep the rights of its old folder and stay invisible to Jellyfin; copy it with File Explorer instead.

## Get the server ready

On the PC that runs Jellyfin, signed in with Jellyfin's administrator account, open the guide (**?** on the left, or <kbd>F1</kbd>). The **Mira on your iPhone** card ticks what is ready and numbers what is left:

- **Mira's extension on Jellyfin**: it serves your party link, guests without an account, automatic subtitles and a few other features.
- **Windows Firewall**: lets your devices reach Jellyfin, on its port only, from your home network and Tailscale, never on a public network. If Windows counts your home Wi-Fi as public, **Make it private** changes it (only at home: a café's network must stay public).
- **Away from home (optional)**: Tailscale on this PC.
- **From power-on**: Jellyfin starts with Windows, before anyone signs in, and restarts after an error.

**Set everything up** does all that Mira can, with a single Windows permission, then gives the address to type on your other devices. **Close Jellyfin to the network**, under the steps, removes Mira's firewall rule; **Set everything up** puts it back.

### Without Mira for Windows

With a Mac (Mira 0.9.6 or later), signed in with Jellyfin's administrator account: **Settings → Account & server → Install**. Mira checks the extension's signature first, then Jellyfin restarts for a few seconds, only once nobody is watching.

Otherwise, to add Mira's extension from Jellyfin's web page:

1. **Dashboard → Plugins → Repositories → +**, name `Mira`, URL:
   ```text
   https://github.com/sasou-web/Mira/releases/latest/download/jellyfin-manifest.json
   ```
2. **Catalog** → **Mira** → **Install**, then restart Jellyfin (**Dashboard → Restart**).

Jellyfin then updates the extension with each version of Mira. With a Windows PC, prefer **Set everything up**, and with a Mac its **Install** button: Mira checks the extension's signature before installing it.

## Keep Jellyfin safe

In Mira for Windows, on the Jellyfin PC: **Settings → Maintenance → Jellyfin on this PC** shows its version and last backup (Jellyfin 10.11 or later).

- **Back up now** makes Jellyfin's built-in backup: accounts, everyone's progress, libraries, artwork and settings, not your videos. Mira can copy each backup to another drive and keeps the three most recent there. It reminds you after 30 days without one.
- When a newer Jellyfin comes out, Mira says so once. **Update Jellyfin** backs up first, downloads the official installer, checks it, then Windows asks for permission. It is refused while someone is watching.

> [!WARNING]
> A backup holds the sign-in tokens of your devices. Keep it in a folder of your own, never a shared one.

To restore one: install Jellyfin (same version or later), copy `jellyfin-backup-<date>.zip` into `C:\ProgramData\Jellyfin\Server\data\backups`, open `http://127.0.0.1:8096` → **Dashboard → Backups**, and restore it.
