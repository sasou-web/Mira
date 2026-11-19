---
title: Install on iPhone and iPad
short: iPhone and iPad
description: Mira for iPhone is a native app installed with AltStore Classic and a free Apple ID, refreshed each week by a Windows PC or a Mac at home.
platforms: iOS 26 or later
---

Mira for iPhone and iPad is a real app, with your library, search, its own player (Picture in Picture, AirPlay, next episode), the sleep timer, Watch together and, away from home, Built-in Tailscale. It is not on the App Store: you install it with **AltStore Classic**, free, which signs it on your iPhone with your own Apple ID.

## What it means

- **iOS 26 or later** (iPhone 11 and later).
- **A free Apple ID is enough.** Apple then signs at most 3 apps at a time (AltStore counts as one), and each app **expires after 7 days** unless it is refreshed. AltStore refreshes it in the background when your iPhone is on the same Wi-Fi as a computer running **AltServer**: the PC that runs Jellyfin, always on, is perfect for this.
- AltServer runs on **Windows** and on **macOS 11 or later**. Each step below says what changes between the two.

> [!PRIVACY]
> You type your Apple ID and password in AltServer, which sends them to Apple only, to create the free certificate. AltStore downloads Mira's source from `miracinema.app` and the app from GitHub. Mira itself talks only to your Jellyfin, and to Tailscale only if you turn Built-in Tailscale on.

## 1. Prepare the computer (once)

### On Windows

On the PC that runs Jellyfin, with your usual Windows account:

1. Install **iTunes** and **iCloud** from Apple's website, **not** from the Microsoft Store (AltServer cannot find the Store versions):
   - [iTunes 64-bit](https://www.apple.com/itunes/download/win64)
   - [iCloud](https://updates.cdn-apple.com/2020/windows/001-39935-20200911-1A70AA56-F448-11EA-8CC0-99D41950005E/iCloudSetup.exe)
   Uninstall any Microsoft Store version first. **Don't sign in to iCloud**: this version refuses anyway ("Update iCloud for Windows"), click **OK** then **Cancel**, and don't update it.
2. Download [AltServer for Windows](https://cdn.altstore.io/file/altstore/altinstaller.zip), unzip it and run `Setup.exe`.
3. Search for **AltServer** in Windows, right-click → **Run as administrator**. If the firewall asks, allow **private networks** only. A diamond icon appears near the clock.
4. If its menu offers **Launch at Login**, tick it. Refreshing only works while a Windows session is open on the PC with AltServer running.

### On a Mac

Nothing else to install: on a Mac, Finder talks to the iPhone, no iTunes or iCloud needed.

1. Download [AltServer for macOS](https://cdn.altstore.io/file/altstore/altserver.zip) (macOS 11 or later), open the zip and move **AltServer** to the **Applications** folder.
2. Open AltServer from Applications. It has no window: a diamond icon appears in the **menu bar**, at the top right. If macOS asks, allow its **notifications**: that is how it tells you AltStore is installed.
3. So that it starts by itself: **System Settings → General → Login Items** → **+** under **Open at Login** → **AltServer**.
4. Refreshing only works while the Mac is awake with AltServer running. On a Mac that stays at home, keep it from sleeping: **System Settings → Energy** → **Prevent automatic sleeping when the display is off** (on a laptop: **Battery → Options** → **Prevent automatic sleeping on power adapter when the display is off**).
5. Recent AltServer no longer needs Mail's plug-in. If an **Install Mail Plug-in…** alert still appears, follow [AltStore's steps](https://faq.altstore.io/altstore-classic/how-to-install-altstore-macos/enable-mail-plug-in).

## 2. Install AltStore on the iPhone (once)

1. Plug the iPhone into the computer, unlock it and tap **Trust** (on a Mac, also click **Trust** if the Mac asks).
2. Turn on syncing over Wi-Fi, which is what allows refreshing without a cable:
   - **Windows**: open **iTunes**, sign in with your Apple ID, click the iPhone icon, then in **Summary** tick **Sync with this iPhone over Wi-Fi** and click **Apply**.
   - **Mac**: open **Finder**, click the iPhone in the sidebar, then in **General** tick **Show this iPhone when on Wi-Fi** and click **Apply**.
3. AltServer icon (near the clock on Windows, in the menu bar on a Mac) → **Install AltStore** → your iPhone. Enter your Apple ID and password.
4. Wait for AltServer's notification. If AltStore's icon does not appear, restart the iPhone.
5. On the iPhone: **Settings → General → VPN & Device Management**, tap your Apple ID, then **Trust**.
6. **Settings → Privacy & Security → Developer Mode**: turn it on. The iPhone restarts and asks you to confirm.
7. **Settings → General → Background App Refresh**: leave it on for **AltStore**.

## 3. Install Mira

1. Open **AltStore** → **Sources** → **+**, and add:
   ```text
   https://miracinema.app/altstore.json
   ```
2. Open the **Mira** source → **Mira** → **FREE**. AltStore downloads the app, signs it and installs it.
3. Open Mira and sign in to Jellyfin. The first time on your home Wi-Fi, iOS asks for access to the **local network**: tap **Allow**, or Mira cannot see Jellyfin at home.

On Windows, the **Mira on your iPhone** card of the guide (and on Mac, **Settings → Account & server**) shows a **QR code**: scan it with the iPhone camera and Mira opens with your server already found; you only type your account. You can also sign in with a code: **Sign in with a code** shows six digits, which an administrator approves in Mira for Windows or Mac (**Settings → Friends & guests → Allow a device**) or in Jellyfin's web page. Quick Connect must be turned on in Jellyfin.

## Every day

- **Updates**: AltStore shows each new version of Mira in **My Apps**; tap **Update**.
- **Expiry**: **My Apps** shows the days left. As long as the computer is on and awake with AltServer and the iPhone visits its Wi-Fi once a week, AltStore refreshes by itself. Otherwise, open AltStore at home and tap **Refresh All**. An expired app no longer opens but loses nothing.
- **Away from home**: see [Away from home](away.md#on-iphone-and-ipad).

## If it goes wrong

- **"Could not find AltServer"** while refreshing: the iPhone and the computer are not on the same Wi-Fi, AltServer is not running (or the Mac is asleep), or Wi-Fi sync is off (iTunes on Windows, Finder on a Mac). A school or public Wi-Fi can block it: plug the cable in.
- **AltStore installed from Safari**: that is AltStore PAL (European Union), which only installs apps notarised by Apple, so not Mira. Delete it and install AltStore Classic with AltServer.
- **AltServer doesn't see the iPhone**: on Windows, check that iTunes and iCloud come from Apple's website; on a Mac, check that the iPhone shows in Finder's sidebar. Then unplug and plug the unlocked iPhone again.
- **Errors 503 or -80046**: update AltServer (its icon → **Check for Updates…**), then run **Install AltStore** again, without deleting AltStore.
- AltStore's own guides: [install on Windows](https://faq.altstore.io/altstore-classic/how-to-install-altstore-windows), [install on macOS](https://faq.altstore.io/altstore-classic/how-to-install-altstore-macos), [troubleshooting](https://faq.altstore.io/altstore-classic/troubleshooting-guide).
