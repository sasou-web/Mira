---
title: Away from home
short: Away from home
description: Reach your Jellyfin from anywhere with Tailscale, built into Mira on Windows, Mac and iPhone, without opening anything to the Internet.
platforms: Windows, Mac, iPhone
---

At home, Mira reaches Jellyfin directly. Away, it goes through **Tailscale**: a free private network between your own devices, encrypted end to end. Jellyfin stays closed to the rest of the Internet.

> [!WARNING]
> Don't open a port on your router to reach Jellyfin: it would expose it to the whole Internet. Tailscale does the same job without that risk.

## On the Jellyfin PC

The PC that runs Jellyfin needs the real **Tailscale app**, signed in to your Tailscale account.

1. Install Tailscale from [tailscale.com/download](https://tailscale.com/download) and sign in. A free personal account is enough.
2. In Mira for Windows, open the guide (<kbd>F1</kbd>) and click **Set everything up** on the **Mira on your iPhone** card. It opens Jellyfin to your Tailscale devices in the firewall, turns on Tailscale's **Run unattended** (so it works before anyone signs in to Windows), and keeps the PC awake on mains power.
3. The card then gives the Tailscale address to type on your devices. It works at home too.

If the PC sleeps, Tailscale cannot wake it. On a laptop on mains power, set closing the lid to **Do nothing**.

## On your other devices

Each device can either have the Tailscale app, or use **Built-in Tailscale**, which is inside Mira. Built-in Tailscale makes Mira one more device on your Tailscale network, only while Mira is open. When the Tailscale app is connected on a device, Mira uses it and does not offer its own.

### On Windows and Mac

1. On the sign-in screen, type the Jellyfin PC's Tailscale address (`100.x.y.z`) or its name. Mira offers **Sign in to Tailscale**; it also does when it finds no server.
2. Your browser opens Tailscale's sign-in page. Sign in with the account of the Jellyfin PC.
3. If your Tailscale network approves each new device, approve `mira-<computer name>` in its admin console (**Open Tailscale's admin console**).
4. If the firewall asks to allow Mira's Tailscale, accept: the direct connection is faster. Refusing works too, through a Tailscale relay.
5. Sign in to Jellyfin as usual.

Mira then joins your Tailscale network each time it opens and leaves when it closes. **Settings → Account & server → Built-in Tailscale** shows its state and the path Mira takes: **At home (direct)**, **Through the Tailscale app** or **Through Built-in Tailscale**.

### On iPhone and iPad

1. Mira → **Settings → Away from home** → turn on **Built-in Tailscale**, then **Sign in to Tailscale**. Not signed in to Jellyfin yet: on the sign-in screen, **Away from home: Built-in Tailscale**.
2. Tailscale's page opens over Mira: sign in with the account of the Jellyfin PC. Mira closes it once the iPhone is connected. Your Tailscale password stays on Tailscale's page.
3. If your network approves each new device, approve `mira-iphone` in the [admin console](https://login.tailscale.com/admin/machines).
4. From the sign-in screen, Mira finds Jellyfin on your Tailscale network. Already signed in: nothing else to do. When the Wi-Fi doesn't lead to Jellyfin, Mira goes through Built-in Tailscale.

**Settings → Away from home → Path to Jellyfin** says which way Mira goes, and whether the Jellyfin PC is online on Tailscale. No VPN profile is needed, so it works with AltStore Classic and a free Apple ID. Through Built-in Tailscale, a video sent with AirPlay is still played by the iPhone: the TV cannot reach Jellyfin through it.

## Keep it working

- **Key expiry.** A Tailscale device's sign-in expires after 180 days by default. Mira warns you two weeks before. In the [admin console](https://login.tailscale.com/admin/machines), **⋯ → Disable key expiry** on the device avoids it, for the Jellyfin PC too.
- **Signing out.** **Sign out of Tailscale** in Mira's settings tells Tailscale, then erases Built-in Tailscale's keys from the device. The device stays in your console, "Logged out", until you remove it there.

### When Tailscale itself is down

If Tailscale's coordination servers have an outage, devices that are already set up can keep reaching each other from the list of devices they keep. Tailscale only allows this when your network's policy says so. Mira offers it under **Built-in Tailscale** in its settings, with **Copy the line**; in your [network policy](https://login.tailscale.com/admin/acls/file), paste it just below `"nodeAttrs": [` (if there is none, add the whole block after the first `{`):

```json
"nodeAttrs": [
  { "target": ["autogroup:member", "autogroup:tagged"], "attr": ["cache-network-maps"] },
],
```

> [!PRIVACY]
> Nothing goes to Tailscale before you sign in to it. Then, as with the Tailscale app, its servers see the account you chose, the device's name, its system and its IP addresses. Your videos stay end-to-end encrypted, directly or through a relay. Mira sends no logs to Tailscale.

## A friend without Tailscale

To watch with someone who is not on your Tailscale network, send them your [party link](together.md): it opens only Mira to the Internet, through Tailscale Funnel, never the rest of Jellyfin.
