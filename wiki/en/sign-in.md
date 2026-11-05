---
title: Sign in to Jellyfin
short: Sign in
description: Which address to type, how Mira finds your server, and why it sometimes asks before sending your password.
platforms: Windows, Mac, iPhone
---

## Mira looks for your server

As long as no address is known, the sign-in screen looks for Jellyfin for a couple of seconds: on this computer, on your home network (Jellyfin's own discovery), and on your devices that are online on Tailscale. It fills in the best address it finds and says where it found it; the other servers are offered next to it. **Search again** looks once more. An address you typed is never replaced.

The iPhone cannot discover servers on the home network (Apple only allows it to paid developer accounts): type the address, or use Tailscale.

## Which address to type

Mira accepts the address in almost any form:

| You type | Mira tries |
| --- | --- |
| `192.168.1.20` | the PC at home, port 8096 |
| `nas:8096` | a name on your network, with its port |
| `100.101.102.103` or `jellyfin-pc` | the PC's address or name on Tailscale |
| `https://jellyfin.example.com` | a server on the Internet |
| an address copied from Jellyfin's web page | the same server |

Without `http://` or `https://`, Mira tries HTTPS, then port 8096, then HTTP, and shows the address it kept.

> [!TIP]
> On the Jellyfin PC itself, the address is `http://127.0.0.1:8096`. For your other devices, the guide's **Mira on your iPhone** card gives the right one. A Tailscale address works at home as well as away.

## When Mira asks before signing in

Mira checks the address before your password leaves:

- **"This isn't a Jellyfin server"**: something answers, but not Jellyfin (a router, a NAS page, Emby). Check the port, 8096 by default.
- **"Mira needs Jellyfin 10.9"**: update Jellyfin.
- **"HTTPS certificate not recognised"**: Mira never falls back to `http://` by itself, since your password would travel in clear. For a server at home, type its full `http://` address.
- **"This address goes over the Internet, in http://"**: your password would cross the Internet unencrypted. Use the server's `https://` address or its Tailscale address. **Sign in anyway** sends it regardless.

At home, on this PC and over Tailscale, nothing is asked: Tailscale already encrypts everything.

## Sign in with a code

Mira for iPhone can sign in without typing a password: **Sign in with a code** shows six digits. An administrator approves them in Mira for Windows or Mac (**Settings → Friends & guests → Allow a device**), choosing the account, or in Jellyfin's web page. Jellyfin's **Quick Connect** must be on (Dashboard → General).

## What Mira keeps

Your password is never stored. Mira keeps only the session Jellyfin opens: protected by Windows for your account, in the keychain on Mac and iPhone. **Sign out** closes the session on Jellyfin too. On Windows and Mac, **Also erase this account's data on this PC** removes the cache and artwork of that account.

## Your choices follow you

Your preferred languages, **Skip intros and recaps**, the audio and subtitles chosen for each series and the titles removed from Continue watching are kept on your Jellyfin account, and follow you to your other devices. The most recent choice wins. Appearance and home rows stay on each device.
