---
title: Install on Windows
short: Windows
description: Download Mira for Windows, get past SmartScreen, add the video engine and keep Mira up to date.
platforms: Windows 10, Windows 11
---

## Download

The [latest release](https://github.com/sasou-web/Mira/releases/latest) has three files for Windows. One is enough.

| File | For |
| --- | --- |
| [Installer](https://github.com/sasou-web/Mira/releases/download/v__VERSION__/Mira-__VERSION__-win-x64-setup.exe) | Most people. Installs Mira for your Windows account only, without administrator rights, with a Start menu shortcut. |
| [Portable](https://github.com/sasou-web/Mira/releases/download/v__VERSION__/Mira-__VERSION__-win-x64-portable.exe) | One file that runs as it is. Keep it in a folder of its own, like `Documents\Mira`: it creates its `data` folder next to itself. |
| [Zip](https://github.com/sasou-web/Mira/releases/download/v__VERSION__/Mira-__VERSION__-win-x64.zip) | The whole application folder. Extract the entire `Mira` folder somewhere you can write, never just `Mira.exe`. |

The .NET runtime is included: there is nothing else to install.

## Get past SmartScreen

Mira's Windows files are not code-signed yet, so Windows SmartScreen may say "Windows protected your PC". Click **More info**, then **Run anyway**. You can check first that your file is the published one: each file has a `.sha256` next to it in the release.

```powershell
Get-FileHash .\Mira-__VERSION__-win-x64-setup.exe -Algorithm SHA256
```

The value must match the one in the `.sha256` file. More ways to check, and why Windows warns: [Verify your download](verify.md).

## First start

1. Open Mira. The first time, a welcome screen explains what Jellyfin and Mira each do.
2. Mira looks for Jellyfin on this PC, on your home network and on your Tailscale devices, and fills in the address it finds. Enter your Jellyfin username and password, then **Sign in**. More in [Sign in to Jellyfin](sign-in.md).
3. No Jellyfin yet? Choose **No server yet: Mira installs Jellyfin on this PC**. See [Set up Jellyfin](jellyfin.md).

## The video engine

Mira plays with mpv. Its engine (31 MB) is not inside Mira's files: Mira downloads a precise build from mpv's Windows builds on SourceForge and checks its SHA-256 before using it.

- With the installer, leave **Download the mpv video engine** ticked.
- Otherwise, Mira offers it at the first playback, or in **Settings → Playback → Install the mpv engine**.
- Already have mpv.net or Jellyfin MPV Shim? Mira reuses their engine and downloads nothing. You can also pick another 64-bit `libmpv-2.dll` in **Settings → Playback**.

## Updates

Mira updates itself. A few seconds after it opens, then every 6 hours, it asks GitHub for a new version, downloads it in the background, checks that it is signed by Mira with the announced size and SHA-256, and installs it when you close Mira. **Restart** in the notice installs it right away.

To stop the automatic check, turn off **Settings → Mira on this PC → Update automatically**. The **Search** button stays.

Your settings and session live in the `data` folder and are never touched by an update.

## Uninstall

**Settings → Apps → Mira**. The uninstaller asks before deleting your data (no by default), then lists what remains on the PC: Jellyfin if Mira installed it, the "Mira - Jellyfin" firewall rule, and Jellyfin's read access to your video folders.
