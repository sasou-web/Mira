---
title: Install on Mac
short: Mac
description: Install Mira on a Mac with the disk image or Homebrew, allow it the first time, and let it update itself.
platforms: macOS 15 or later, Apple silicon
---

Mira for Mac plays your Jellyfin library with the same mpv player as on Windows. It needs a Mac with Apple silicon (M1 or later) on macOS 15 Sequoia or later, and a Jellyfin server you already have: Mira for Mac does not install Jellyfin.

## With the disk image

1. Download [Mira-__VERSION__-mac-arm64.dmg](https://github.com/sasou-web/Mira/releases/download/v__VERSION__/Mira-__VERSION__-mac-arm64.dmg).
2. Open it and drag **Mira** onto **Applications**.
3. Open Mira from Applications. macOS stops it the first time: see below.

## With Homebrew

If you use [Homebrew](https://brew.sh), one command in Terminal replaces steps 1 and 2:

```sh
brew install --cask sasou-web/mira/mira
```

It comes from Mira's own tap, [sasou-web/homebrew-mira](https://github.com/sasou-web/homebrew-mira): Homebrew's main catalogue only takes apps notarised by Apple. Homebrew checks the image's SHA-256 and keeps macOS's protection, so the first opening is the same as below. Mira then updates itself; `brew uninstall --cask --zap mira` removes it with its settings.

## Allow Mira the first time

Mira is not signed with a paid Apple developer account, so macOS cannot check it and refuses to open it the first time. Once only:

1. Open Mira, then close the message.
2. Go to **System Settings → Privacy & Security**.
3. At the bottom, next to "Mira was blocked", click **Open Anyway**, then confirm.

If macOS says Mira is "damaged", or the button does not appear, type this in Terminal:

```sh
xattr -dr com.apple.quarantine /Applications/Mira.app
```

It only removes the "downloaded from the Internet" mark from Mira. It changes no security setting of your Mac. To check the image before opening it: [Verify your download](verify.md).

## First sign-in

Type your server's address (`192.168.1.20`, `nas:8096`…), your username and password. Mira finds the right port and protocol. Away from home, use the server's Tailscale address, or let [Built-in Tailscale](away.md) reach it without the Tailscale app.

Your password is never stored: Mira keeps only the session Jellyfin opens, in your Mac's keychain (item "Mira"). The rest is in `~/Library/Application Support/Mira`, readable by your macOS account only.

## Updates

Mira updates itself. When it opens, then every 6 hours, it asks GitHub for a new version, downloads the disk image and keeps it only if its size and SHA-256 match the release's signed manifest. It installs it when you quit Mira, when the Mac shuts down or restarts, or right away with **Restart** in the notice. With the window closed (Mira in the Dock), it installs a couple of minutes later, when nothing is playing: Mira quits and opens again in the Dock, hidden.

> [!NOTE]
> When Mira updates itself, the old version hands your session over to the new one as it quits, so macOS does not ask for anything (from the version after 0.9.4: the update to it still asks once). After an update made another way (Homebrew, or the disk image dragged by hand), macOS asks once for your login password so that Mira can read its own keychain item: choose **Always Allow**. This happens because Mira is not signed by an Apple developer account. If you refuse, Mira simply asks you to sign in to Jellyfin again.

Mira cannot replace itself when it runs from the disk image, from the Downloads folder (macOS runs a protected copy), or from a folder your account cannot change. It still downloads and checks the new version, and **Open the disk image** lets you drag it to Applications.

To stop the automatic check, turn off **Settings → Updates → Update automatically**.

## Window and menus

Mira behaves like the Mac's own apps:

- The window reopens where you left it, on the same screen, at the same size (centred when that screen is gone).
- <kbd>⌘</kbd> + <kbd>W</kbd> or the red button closes the window, Mira stays in the Dock: a click on its icon brings it back. What was playing stops, and where you were is kept. <kbd>⌘</kbd> + <kbd>Q</kbd> quits Mira. Mira never holds up a shutdown, restart or logout of the Mac: what was playing is paused and kept, and sent to Jellyfin then or the next time Mira opens.
- **Help** opens this guide, **Keyboard Shortcuts** (<kbd>⌘</kbd> + <kbd>/</kbd>) and **Export a Report…**, the file to attach when you report a problem.

## Trackpad

Mira follows your trackpad settings (natural scrolling, swipe between pages).

- In the library: swipe with two fingers to go back and forward (the page follows your fingers, as in Safari; let go early to stay), pinch to change the size of the posters, force click a poster for its menu.
- In the player: swipe left or right to seek, up or down for the volume; spread two fingers for full screen, pinch to leave it, then to shrink the player to the mini player.

## HDR films

On a Mac, Mira shows an HDR film (HDR10, HDR10+, HLG, Dolby Vision) in SDR: mpv converts it (tone mapping) to the ordinary range before it reaches the screen. The colours stay right and nothing is converted by Jellyfin, but the extra brightness of HDR is lost, even on a MacBook Pro or a Pro Display XDR.

This is a limit of how Mira draws the video on a Mac (an 8-bit OpenGL view, without the Mac's extended range), not of your file or your screen. In the player, **⋮ → Info**, the **Colours** line says it: "HDR10 → SDR (rendering limit)".

> [!NOTE]
> On Windows, Mira sends HDR to the screen as it is when Windows' HDR is on. On iPhone, Apple's player shows HDR and Dolby Vision.

## Start over

Sign out first (**Settings → Account & server**), quit Mira, then delete `~/Library/Application Support/Mira`.
