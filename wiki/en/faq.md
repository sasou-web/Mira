---
title: Questions
short: FAQ
description: Short answers to what people ask most about Mira.
---

## Is Mira free?

Yes, entirely, with no account, subscription or ads. Its code is open, under the MIT licence.

## Does Mira come with films?

No. Mira plays what is on your own Jellyfin server. It provides no films, no sources and no content.

## Do I need Jellyfin?

Yes: Jellyfin is the server that keeps your videos and serves them. If you don't have it, Mira for Windows installs it on your PC and sets it up for you. See [Set up Jellyfin](jellyfin.md).

## Can I use Mira with Plex or Emby?

No, only Jellyfin (10.9 or later).

## Is there Mira for Android, a TV or a browser?

Not for now. Mira web, which played in the phone's browser, ended with 0.9.4 to focus on the apps. On a TV or a console, Jellyfin's own app works with the same server: Mira's guide gives the address to type.

## Why isn't Mira on the App Store?

Publishing there needs a paid Apple developer account. AltStore Classic installs Mira with a free Apple ID instead: see [Install on iPhone](iphone.md).

## Why does Windows or macOS warn me?

Mira's files are not signed by a paid certificate yet. Windows SmartScreen and macOS Gatekeeper warn about any unsigned app. Each file's SHA-256 is published next to it, in a release that can no longer be changed. See [Install on Windows](windows.md#get-past-smartscreen) and [Install on Mac](mac.md#allow-mira-the-first-time).

## Does Mira convert my videos?

At home, no: Mira plays every file as it is, 4K and HDR included. Away from home, if you set a lower **Quality**, Jellyfin converts the file while you watch.

## Can my friends watch my library?

Two ways. Give them an account of their own (**Settings → Friends & guests → Add a friend**, as Jellyfin's administrator): they choose what to watch, when they want, and need Tailscale to reach your server from their home. Or send your [party link](together.md#your-party-link): they watch with you, in your group, with no account and no Tailscale.

## Is it safe to open my server to friends?

Your party link only opens Mira's page and its guest gateway to the Internet, never Jellyfin's dashboard or sign-in. Each guest has a limited hidden account that you can take back at any time. Accounts you create for friends can only see the libraries you chose, without downloads.

## Where is my data?

Your library, progress and accounts are on your Jellyfin server. Mira keeps a cache and your settings on each device, and nothing anywhere else. See [Privacy](privacy.md).

## How do I help?

Tell others, report bugs on [GitHub](https://github.com/sasou-web/Mira/issues/new/choose), share ideas on [Discord](https://discord.gg/Bh3F6PDUvB). Each page of this wiki has **Edit this page on GitHub** at the bottom.
