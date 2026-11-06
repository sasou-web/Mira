---
title: Troubleshooting
short: Troubleshooting
description: The messages Mira shows when something is wrong, what they mean and what to do.
platforms: Windows, Mac, iPhone
---

Mira says what is wrong in its own words, with a button that helps when there is one. Find the message below, or search this wiki for a word of it.

## Can't reach Jellyfin

| Message or symptom | What to do |
| --- | --- |
| Works at home, not away | The home address (`192.168…`) only works on your Wi-Fi. Use [Tailscale](away.md): on the Jellyfin PC, the guide's **Set everything up**; on your device, Built-in Tailscale or the Tailscale app. |
| "… isn't connected to Tailscale: it's off, asleep or offline" | The Jellyfin PC left Tailscale. Turn it on or wake it. Mira reloads Home as soon as it is back. |
| Unreachable after a while without using the Jellyfin PC | It went to sleep, and Tailscale cannot wake it. **Set everything up** keeps it awake on mains power; on a laptop, set closing the lid to **Do nothing** on mains power. |
| Unreachable after the Jellyfin PC restarted, until someone signs in to Windows | Tailscale waits for a session: **Set everything up** turns on **Run unattended**, or Tailscale's menu → **Preferences** → **Run unattended**. |
| Tailscale asks to sign in again on the Jellyfin PC | Its key expired (every 180 days by default). Sign in again, then in the [admin console](https://login.tailscale.com/admin/machines), **⋯ → Disable key expiry** on that PC. |
| "Waiting for approval in Tailscale's admin console" | Your Tailscale network approves each new device: **Open Tailscale's admin console** and approve the device. |
| Everything shows "Connecting…", on every device | Tailscale itself may be down: check [status.tailscale.com](https://status.tailscale.com). See [When Tailscale itself is down](away.md#when-tailscale-itself-is-down). |
| "Windows Security blocks mira-tailscale.exe" | An antivirus blocks Built-in Tailscale. Install the Tailscale app on this PC instead: Mira then goes through it. |
| "macOS blocks mira-tailscale" | The "downloaded from the Internet" mark stayed on Mira.app: **Copy the command** in Built-in Tailscale's settings, paste it in Terminal, restart Mira. |
| The iPhone doesn't see Jellyfin at home | iOS's local network access is off: **Settings → Apps → Mira → Local Network**. |
| "Jellyfin is stopped on this PC" | **Start Jellyfin**, in the notice or on the guide's card. If it stops again, Mira shows the cause from Jellyfin's log (port 8096 taken, disk full…). |

## Can't sign in

| Message | What to do |
| --- | --- |
| "This isn't a Jellyfin server" | Something else answers at that address (router, NAS, Emby): check the port, 8096 by default. |
| "Mira needs Jellyfin 10.9" | Update Jellyfin. |
| "HTTPS certificate not recognised" | For a server at home, type its full `http://` address (Mira suggests it). Otherwise, install a valid certificate. |
| "This address goes over the Internet, in http://" | Your password would travel unencrypted. Use the `https://` or Tailscale address. |
| "This Jellyfin already has an administrator account" | Its setup was started with another account: finish it on `http://127.0.0.1:8096`, then sign in. |
| "Another program already answers on port 8096" | It isn't Jellyfin: close that program, or install Jellyfin on another PC. |

## Can't play

| Symptom | What to do |
| --- | --- |
| Library shows, nothing plays (Windows) | **Settings → Playback**: install the mpv engine, or pick a 64-bit libmpv DLL. |
| "The video engine couldn't be downloaded" | Check the Internet connection, then try again from **Settings → Playback**. It comes from `downloads.sourceforge.net`. |
| Stutters away from home | Lower **Quality** in the player's **⋮** menu, or choose **Auto**. |
| Picture stutters or stays black (Windows) | Turn off **Hardware decoding** in **Settings → Playback**. |
| Grey or black picture (Mac) | In the player, **⋮ → Grey or black picture? Repair**. Each repair goes one step further; Mira keeps the one that works. |
| "Can't play this" | Mira reopened the stream and Jellyfin still could not serve the file. Jellyfin's administrator finds the reason in **Settings → Maintenance → Reported playback failures**. |
| A file was added but doesn't show | Check it is in **Movies**, **Shows** or **Anime**, and named the way Jellyfin expects (see [Name your videos](jellyfin.md#name-your-videos)). Then **Refresh** in the library. |

## Updates

| Symptom | What to do |
| --- | --- |
| Windows: an update doesn't install | **Settings → Mira on this PC** shows the last error; `data\updates\update.log` has the details. Mira's folder must stay writable. |
| Mac: Mira asks for the keychain after an update | Only after an update made by Homebrew or by hand, once per version: choose **Always Allow**. See [Install on Mac](mac.md#updates). |
| iPhone: Mira no longer opens | It expired after 7 days. Open AltStore at home and tap **Refresh All**. Nothing is lost. |

## Still stuck?

Ask on [Discord](https://discord.gg/Bh3F6PDUvB), or [open an issue](https://github.com/sasou-web/Mira/issues/new/choose) with the versions of Mira, your system and Jellyfin. Never attach a password, a token, Mira's `data` folder or personal media.

On Mac, a player log helps: in Terminal,

```sh
MIRA_MPV_LOG=$HOME/Desktop/mpv.log /Applications/Mira.app/Contents/MacOS/Mira
```

writes `mpv.log` on your desktop.
