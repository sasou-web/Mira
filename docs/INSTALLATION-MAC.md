# Install Mira on Mac

Mira for Mac plays your Jellyfin library with the same mpv player as the Windows version: resume, next episode, audio and subtitle tracks, skippable intros, volume up to 200%.

## Requirements

- A Mac with Apple silicon (M1, M2, M3, M4…) running **macOS 15 Sequoia** or later (macOS 14 Sonoma: up to 0.7.4).
- A **Jellyfin 10.9** or later server, on your network or reachable with Tailscale. Mira for Mac doesn’t install it: it connects to the one you already have, for example the one on the Windows PC where Mira runs.

## Installation

1. Download **Mira-x.y.z-mac-arm64.dmg** from the [releases page](https://github.com/sasou-web/Mira/releases/latest).
2. Open the disk image and drag **Mira** onto **Applications**.
3. Open Mira from Applications. macOS stops it the first time (see below); that’s normal.

### Or with Homebrew

If you have [Homebrew](https://brew.sh), one command in **Terminal** replaces steps 1 and 2:

```
brew install --cask sasou-web/mira/mira
```

It comes from Mira’s tap ([sasou-web/homebrew-mira](https://github.com/sasou-web/homebrew-mira)), not from Homebrew’s official catalog, which only accepts apps notarized by Apple. Homebrew checks the image’s hash (the same as its `.sha256`) and keeps macOS’s protection: the first launch goes as below. Mira then updates itself; `brew uninstall --cask --zap mira` removes it along with its settings.

### Allow Mira the first time

Mira isn’t signed by a paid Apple developer account: macOS can’t check it and refuses to open it on first launch. To do once only:

1. Open Mira once, then close the message.
2. Go to **System Settings → Privacy & Security**.
3. At the bottom, next to “Mira was blocked”, click **Open Anyway**, then confirm.

If macOS says Mira is “damaged” or the button doesn’t appear, open **Terminal** and type:

```
xattr -dr com.apple.quarantine /Applications/Mira.app
```

This command only removes Mira’s “downloaded from the Internet” mark; it doesn’t change any of the Mac’s security settings.

## First sign-in

Enter your Jellyfin server’s address (for example `192.168.1.20` or `nas:8096`), your username and your password. Mira finds the right port and protocol by itself. Away from home, use the server’s Tailscale address (`100.x.y.z`) or its name on Tailscale; without the Tailscale app on this Mac, **Built-in Tailscale** (below) reaches it anyway.

Your password is never saved: Mira only keeps the session Jellyfin opened. Its access token goes into your Mac’s keychain (the “Mira” item), the rest into `~/Library/Application Support/Mira`, readable by your macOS account only. **Sign out** (Settings → Account) erases both and also closes the session on Jellyfin; with **Also erase this account’s data on this Mac**, this account’s cache and posters leave the Mac.

## Built-in Tailscale: your Jellyfin away from home, without the Tailscale app

If your Jellyfin is on Tailscale (the PC it runs on has the Tailscale app) and this Mac doesn’t have it, Mira joins your Tailscale network itself, as one more device, only while it’s open. Nothing to install: Mira’s Tailscale node (`mira-tailscale`) is inside Mira.app.

1. On the sign-in screen, type the server’s Tailscale address (`100.x.y.z`) or its name, or let Mira search: under the field, Mira offers **Sign in to Tailscale** (“This server is on Tailscale”, or “Not at home?” when it finds nothing).
2. Click **Sign in to Tailscale**: your browser opens Tailscale’s sign-in page. Sign in with your Tailscale network’s account, the one of Jellyfin’s PC.
3. Back in Mira, the line says “Built-in Tailscale · Connected”: sign in to Jellyfin as usual. With no address typed, Mira searches again for your server among your Tailscale devices and offers it.

After that, the node starts with Mira and stops with it. **Settings → Away from home** shows its state and which way Mira reaches Jellyfin (directly at home, through the Tailscale app, or through Built-in Tailscale). When Jellyfin doesn’t answer, Mira says why: Jellyfin’s PC off or asleep (by its name on Tailscale), Tailscale sign-in expired…, with the button that helps. If the Tailscale app is connected on this Mac, Mira goes through it and doesn’t offer Built-in Tailscale.

- **In your Tailscale console**, the device is called “Mira-<this Mac’s name>”. Its sign-in expires after 180 days (Tailscale’s default setting): Mira warns you two weeks before. So you don’t have to sign in again, choose **Disable key expiry** in this device’s ⋯ menu, in the [console](https://login.tailscale.com/admin/machines).
- **macOS firewall**: if it’s on, macOS may ask you to allow “mira-tailscale” to accept incoming connections. Accept: a direct connection is faster. Refusing works too, through a Tailscale relay.
- **“macOS blocks mira-tailscale”**: the “downloaded from the Internet” mark stayed on Mira.app. In **Settings → Away from home**, click **Copy the command**, paste it into Terminal, then restart Mira: it’s the `xattr` command from the installation, with your Mira’s location. Mira never removes this mark itself.
- **What Tailscale sees**: nothing before **Sign in to Tailscale**. After that, as with the Tailscale app, the account chosen in the browser, the device’s name, its system and its IP addresses (public and local, for the direct connection) go to Tailscale’s servers. Your videos stay encrypted end to end, directly or through a Tailscale relay. Mira sends no log to Tailscale.
- **Where it’s kept**: the node’s identity is in `~/Library/Application Support/Mira/tailscale`, encrypted with a key stored in the keychain, alongside the session (the “Mira” item; “Mira (Tailscale)” before 0.8.8). **Sign out of Tailscale** tells Tailscale (the device stays in your console, “Logged out”, until you delete it there), then erases the key and the folder.

## Shortcuts

| Action | Shortcut |
| --- | --- |
| Search | ⌘F |
| Settings | ⌘, |
| Back | Esc, ⌘[, the mouse’s back button, or two fingers to the right on the trackpad |
| Forward (the page left with Back) | ⌘], the mouse’s forward button, or two fingers to the left |
| Bigger / smaller posters in grids | ⌘+ / ⌘−, or pinch on the trackpad; ⌘0 goes back to the original size |
| Play / pause | Space / K, or a click on the video |
| Back / forward 10 seconds | ← / → |
| Volume (up to 200%) | ↑ / ↓, or the scroll wheel on the speaker |
| Mute | M |
| Full screen | F, or a double-click on the video |
| Skip the intro or the credits | S |
| Next episode | N |

### Trackpad

Mira follows your Mac’s trackpad settings (**System Settings → Trackpad**): the scroll direction (**Natural scrolling** or not) and **More Gestures → Swipe between pages**, read again each time you come back to Mira.

- **In the library**: two fingers to the right = Back, to the left = Forward, as in Safari: the page follows your fingers, the previous one arrives underneath; let go before sliding far enough and it comes back, and a quick flick is enough. Over a row of posters, the gesture first scrolls the row, then changes page once at the end. If your Mac swipes between pages with three fingers, it’s three fingers in Mira too. **Pinch** changes the size of posters in grids (Movies, Shows, Recently added), which slide to their new place, kept from then on. **Force click** (press harder, Force Touch trackpad) on a poster or an episode opens its menu, like a right-click.
- **In the player**: two fingers to the right or left skip forward or back in the film (about 6 seconds for an average gesture, the bar follows right away), up or down change the volume (a mouse wheel too). Spreading two fingers goes full screen; pinching them leaves it, then shrinks the player into the mini player. Spreading on the mini player opens it full size again.

## Updates

Since 0.8.8, Mira updates itself. When it opens and then every six hours, it asks GitHub whether there’s a new version, downloads its disk image and keeps it only if its size and SHA-256 hash are those of the release’s signed manifest (the same check as on Windows). It prepares the new Mira next to itself, then installs it when you quit Mira; **Restart**, in the “Mira x.y.z is ready” notice, installs it right away and reopens it. Your settings and your session stay in place, and macOS doesn’t ask for the first-time permission again: Mira downloaded and checked the new version, not the browser.

Mira can’t replace itself when it runs from its disk image, from the protected copy macOS makes of an app opened straight from Downloads, or from a folder your account can’t change (an administrator account’s Applications, seen from a standard account). It still downloads and checks the new version, and **Open the disk image** lets you drag it into Applications, as at installation. That’s also what it offers if a replacement failed. 0.8.7 and earlier versions don’t have this code: to move to 0.8.8, replace Mira by hand one last time.

Since Mira isn’t signed by an Apple Developer account, macOS doesn’t recognize the new version as the same app: it would ask for your login password before letting it read the session and the Built-in Tailscale key stored in the keychain. Since the version after 0.9.4, when Mira installs itself, the old version hands them to the new one as it quits (in a file in `~/Library/Application Support/Mira` readable by your account only, while the new one opens), and the new one stores them in a keychain item of its own: no more question. After an update done another way (Homebrew, disk image dragged by hand), and the one that leads to this version, macOS still asks once (“Mira wants to use your confidential information stored in “Mira” in your keychain”): choose **Always Allow**, it’s Mira’s own item. If you refuse, Mira simply asks you to sign in to Jellyfin again, and Built-in Tailscale waits: **Try again** in its Settings asks again.

So that Mira no longer contacts GitHub on its own, turn off **Settings → Updates → Update automatically**; the **Check for updates** button stays available.

Each disk image is published with its SHA-256 hash (a `.sha256` file). To check the one you downloaded, also download its `.sha256` file into the same folder (here Downloads), then in Terminal:

```
cd ~/Downloads
shasum -a 256 -c Mira-x.y.z-mac-arm64.dmg.sha256
```

`OK`: the file is identical to the release’s.

## What stays Windows-only

TorLink runs on Jellyfin’s Windows PC: from the Mac, the **Downloads** page (Jellyfin administrator account) sends it your searches and downloads, and shows their progress. Installing Jellyfin from Mira and the taskbar integration only exist on Windows.

## If something doesn’t work

- **No picture, but sound**: in Terminal, `MIRA_SOFTWARE_VIDEO=1 /Applications/Mira.app/Contents/MacOS/Mira` starts Mira with software video output. If it works, report it in an [issue](https://github.com/sasou-web/Mira/issues).
- **For a detailed report**: `MIRA_MPV_LOG=$HOME/Desktop/mpv.log /Applications/Mira.app/Contents/MacOS/Mira` writes the player’s log to the desktop; attach it to the issue.
- **Start from scratch**: sign out first (Settings → Account), then quit Mira and delete the `~/Library/Application Support/Mira` folder. Without signing out, also delete the “Mira” item (and “Mira (Tailscale)” from a version before 0.8.8) in Keychain Access, and the “Mira-<this Mac’s name>” device in the Tailscale console.
