# Install Mira for iPhone (AltStore Classic)

Mira for iPhone and iPad is a native app (SwiftUI, iOS 26, Liquid Glass). It isn’t on the App Store: you install it with **AltStore Classic**, which is free and signs it on your iPhone with your own Apple ID. It has your library, search, favorites, its own player (Picture in Picture, AirPlay, next episode), the Sleep timer, Watch together with Mira’s other devices and, away from home, **Built-in Tailscale** (without the Tailscale app). To watch with you, your friends don’t need to install anything but Mira: your party link lets them in without an account.

## What this means

- **iOS 26 or later** (iPhone 11 and later).
- **A free Apple ID** is enough. Apple limits what it signs: **3 apps** at a time (AltStore counts as one), and each app **expires after 7 days** if it isn’t refreshed. AltStore refreshes it by itself in the background when your iPhone is on the same Wi-Fi as a PC running **AltServer**: the server PC, always on, takes care of it.
- **Privacy**: your Apple ID and its password are typed into AltServer, which sends them only to Apple (to create the free certificate). AltStore downloads Mira’s source from `miracinema.app` and the app from GitHub. Mira itself only talks to your Jellyfin server, and to Tailscale only if you turn on Built-in Tailscale (see step 4): no account with us, no statistics.
- AltStore (Riley Testut, [source code](https://github.com/altstoreio/AltStore)) has been around since 2019; its text is in English, hence the English button names below.

## 1. Prepare the server PC (once)

On the PC where Jellyfin runs, with your usual Windows account:

1. Install **iTunes** and **iCloud** from Apple’s website, **not** from the Microsoft Store (AltServer can’t find the Store versions):
   - iTunes 64-bit: <https://www.apple.com/itunes/download/win64>
   - iCloud: <https://updates.cdn-apple.com/2020/windows/001-39935-20200911-1A70AA56-F448-11EA-8CC0-99D41950005E/iCloudSetup.exe>
   If a Microsoft Store version is already there, uninstall it first (Settings → Apps).
   **Don’t sign in to iCloud**: AltServer only needs what it installs. This version refuses to sign in anyway (“Update iCloud for Windows to the latest version to sign in”): click **OK** then **Cancel**, and above all don’t update it through the Microsoft Store. You type your Apple ID into AltServer in step 2.
2. Download **AltServer for Windows**: <https://cdn.altstore.io/file/altstore/altinstaller.zip>. Unzip `altinstaller.zip`, then run `Setup.exe`.
3. In Windows search, type **AltServer**, right-click → **Run as administrator**. If the firewall asks, allow **private networks** only. A diamond-shaped icon appears near the clock.
4. So that AltServer starts again after a restart: if its menu (click the icon) offers **Launch at Login**, tick it; otherwise, start it again as in step 3 after each restart. Refreshing only works while a Windows session is open on the PC and AltServer is running.

## 2. Install AltStore on your iPhone (once)

1. Plug your iPhone into the server PC over USB, unlock it and tap **Trust** this computer.
2. Open **iTunes**, sign in with your Apple ID, click the iPhone icon, then in **Summary** tick **Sync with this iPhone over Wi-Fi** and click **Apply**. That’s what allows refreshing without a cable.
3. Click AltServer’s icon → **Install AltStore** → your iPhone. Type your Apple ID and its password.
4. Wait for AltServer’s notification (a few seconds). If AltStore’s icon doesn’t appear, restart the iPhone.
5. On the iPhone: **Settings → General → VPN & Device Management**, tap your Apple ID, then **Trust**.
6. **Settings → Privacy & Security → Developer Mode**: turn it on. The iPhone restarts and asks you to confirm.
7. **Settings → General → Background App Refresh**: leave it on for **AltStore**.

## 3. Install Mira

1. Open **AltStore** → **Sources** tab → **+** → paste the following address, then confirm:
   `https://miracinema.app/altstore.json`
2. Open the **Mira** source → **Mira** → **FREE**. AltStore downloads the app, signs it with your Apple ID and installs it.
3. Open **Mira**, type your server’s address (`192.168.1.42` at home, `100.88.12.34` with Tailscale), your username and your password. Away from home without the Tailscale app, go through **Away from home: Built-in Tailscale** first (step 4). The first time you connect on your home network, iOS asks for **Local Network** access: tap **Allow** (otherwise Mira can’t see Jellyfin over Wi-Fi).

The source offers Mira from 0.8.6, the first version published with the iPhone app. To try a version that isn’t published yet:

1. On the iPhone, in Safari signed in to GitHub, open the “iPhone build” run you want (the repository’s **Actions** tab) and download the **Mira-ios** artifact.
2. In **Files**, tap the downloaded `.zip` to get `Mira-x.y.z-ios.ipa` out of it.
3. In **AltStore** → **My Apps** → **+** (top left) → choose the `.ipa`.

## 4. Away from home: Built-in Tailscale (optional)

At home, Mira reaches Jellyfin directly over Wi-Fi. Elsewhere, without the Tailscale app, **Built-in Tailscale** makes Mira a device on your Tailscale network, only while Mira is open (and as long as a title is still playing in Picture in Picture). No VPN profile: it works with AltStore Classic and a free Apple ID.

1. Mira → **Settings** → **Away from home** → turn on **Built-in Tailscale**, then tap **Sign in to Tailscale**. Not signed in to your Jellyfin account yet: on the sign-in screen, **Away from home: Built-in Tailscale** → **Turn on Built-in Tailscale**.
2. Tailscale’s page opens over Mira: sign in with your Tailscale network’s account (the server PC’s). Mira closes the page once the iPhone is connected. Your password stays on Tailscale’s page: Mira never sees it.
3. If your Tailscale network makes you approve each new device, Mira says so: approve **mira-iphone** in the [Tailscale console](https://login.tailscale.com/admin/machines).
4. From the sign-in screen, Mira looks for Jellyfin on your Tailscale network: tap it, then sign in to your Jellyfin account as usual. Already signed in to your account, there’s nothing else to do: when the Wi-Fi doesn’t lead to Jellyfin, Mira goes through Built-in Tailscale.

After that, in **Settings** → **Away from home**, **Path to Jellyfin** says which way Mira goes (**At home (direct)**, **Through the Tailscale app** or **Through Built-in Tailscale**) and **PC with Jellyfin online** (or **offline**, with since when) if the server PC is connected to Tailscale.

- **Sign in only once.** The sign-in expires after 180 days (Tailscale’s default setting); Mira warns you 14 days before. So that it never expires: Tailscale console, **mira-iphone**’s ⋯ menu → **Disable key expiry**.
- **With the Tailscale app.** If the Tailscale app is connected on the iPhone, Mira goes through it. Both can stay installed.
- **AirPlay.** Through Built-in Tailscale, a video sent with AirPlay is still played by the iPhone: the TV can’t reach Jellyfin through it. At home, nothing changes.
- **Privacy.** Once the option is on, Tailscale’s servers see the iPhone join your network: its name, its system and its IP addresses (public and local, for the direct connection). Your videos stay encrypted end to end, directly or through a Tailscale relay. Mira sends no log to Tailscale: its node’s log stays on the iPhone, out of backups, and its keys in this iPhone’s keychain only.
- **Sign out of Tailscale** (same section) disconnects this iPhone from your Tailscale network; to use it again, you have to go through Tailscale’s page again.

## Day to day

- **Updates**: AltStore shows each new version of Mira in **My Apps**; tap **Update**.
- **Expiry**: **My Apps** shows the days left. As long as the server PC is on with AltServer and your iPhone joins its Wi-Fi at least once a week, AltStore refreshes by itself. Otherwise, open AltStore at home and tap **Refresh All**. An expired app no longer opens but loses nothing: a refresh is enough.
- **Sign out**: Mira → **Settings** → **Sign out** also closes the session on Jellyfin.

## If it gets stuck

- **“Could not find AltServer”** during a refresh: the iPhone and the PC aren’t on the same Wi-Fi, AltServer isn’t running, or iTunes’ Wi-Fi sync is unticked.
- **iCloud asks for an update to sign in**: normal with the version from the link, don’t sign in (see step 1). To keep iCloud signed in on this PC, follow “Windows Store iCloud” in AltStore’s troubleshooting guide below.
- **AltStore installed from Safari**: that’s AltStore PAL (European Union), which only installs apps notarized by Apple, so not Mira. Delete it and install AltStore Classic through AltServer (step 2).
- **AltServer doesn’t see the iPhone**: check that iTunes and iCloud really come from Apple’s website, unplug and plug the unlocked iPhone back in.
- **Mira says Jellyfin can’t be reached**: at home, check the Wi-Fi and Local Network access (Settings → Apps → Mira → Local Network); elsewhere, turn on Built-in Tailscale or the Tailscale app. Mira says what’s missing: Built-in Tailscale off or signed out, expired sign-in, iPhone waiting for approval, Jellyfin’s PC offline on Tailscale.
- **Built-in Tailscale is waiting for approval**: your Tailscale network makes you approve each new device; approve **mira-iphone** in the [Tailscale console](https://login.tailscale.com/admin/machines) (button in Mira’s Settings).
- **PC with Jellyfin offline** (Settings → Away from home): the server PC is off, asleep or disconnected from Tailscale; the line says since when Tailscale last saw it.
- Official guide and troubleshooting: <https://faq.altstore.io/altstore-classic/how-to-install-altstore-windows>, <https://faq.altstore.io/altstore-classic/troubleshooting-guide>.

## Later: AltStore PAL

The day an Apple Developer account (99 USD a year) is taken, the same app can go to **AltStore PAL**: your friends install it without a computer and it no longer expires. Nothing to redo in Mira; details in the project’s `ameliorations/app-iphone.md` study.
