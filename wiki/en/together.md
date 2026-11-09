---
title: Watch together
short: Watch together
description: Watch the same film at the same second as friends, each at home, with your party link, even without an account or Tailscale.
platforms: Windows, Mac, iPhone
---

Watch together keeps several devices on the same film, at the same second: when someone pauses, skips or goes to the next episode, everyone follows. It uses Jellyfin's own SyncPlay, so it works between Mira for Windows, Mac and iPhone.

## With your own accounts

For people who already have an account on your Jellyfin (your household, a friend you added):

1. On a title's page, **Watch together → Create a group and invite…**.
2. The others see the group on their Home, under the banner, and **Join**. Or send them the link Mira gives: opened where they have Mira, it brings them into the group. Without Mira's extension on your Jellyfin, it is a `mira://` link, which opens only on a device with Mira; with the extension, it is your party link.
3. **Play** becomes **Play for the group**: it starts for everyone, from your progress. Pick another title while in the group and Mira asks before replacing the group's title.

While you are in a group, the player says what the group is doing ("Waiting for someone in the group…", "Paused for the group"), and an icon shows how many are in it. **Leave the group** when you are done.

## Your party link

Your party is a link, always the same, that brings a friend straight into your group, **even without an account on your Jellyfin and without Tailscale**. Find it in **Settings → Friends & guests → Your party**, and **Copy** it.

Send it in Discord, Messages or anywhere: it shows a card with who invites and what you are watching. Your friend:

1. Opens the link and taps **Open in Mira** (Mira for Windows, Mac or iPhone; the page says where to get it otherwise).
2. Types a first name, which is what the group sees. No account to create.
3. Is in your group, and follows along.

Each guest gets a device of their own. In **Guests**, under your link, **Take back** removes one at once. **Make a new link** stops the old link and all its guests.

If your friend has an account on your Jellyfin, the same link signs them into the group with their own account. And if the link is from **another** Jellyfin (a friend's party on their own server), Mira joins it as a guest, then gives you back your own account when you **Leave the party**.

### How a link works from anywhere

The link goes through **Tailscale Funnel**, which opens only Mira's page and its guest gateway to the Internet, never the rest of Jellyfin: no dashboard, no sign-in page, no other route. On the Jellyfin PC, Mira for Windows opens Funnel by itself. The very first time, Tailscale asks you to allow Funnel on your account: **Allow Funnel on Tailscale** opens that page, once.

Without Funnel, the link still works at home, and for friends on your Tailscale network.

> [!PRIVACY]
> With Funnel open, the name of your Jellyfin PC on Tailscale becomes public: its HTTPS certificate is published in certificate transparency logs, like every Funnel address. Traffic stays encrypted to your PC. Each guest gets a hidden Jellyfin account that can only watch with your group, is removed when you take the guest back, and expires after 7 days without use.

## Good to know

- Use the latest Mira on every device. Groups between your own accounts work on any Jellyfin; the party link and its guests need Mira's extension on the host's Jellyfin (see [Set up Jellyfin](jellyfin.md#get-the-server-ready)).
- Each device plays from Jellyfin on its own. A friend far away needs a connection fast enough for the file, or a lower **Quality** in their player (see [The player](player.md#away-from-home-quality)).
- Someone who loads more slowly holds the group for a moment: Mira says "Loading for the group…" rather than leaving them behind.
