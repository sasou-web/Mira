---
title: Audio and subtitles
short: Audio and subtitles
description: Your languages remembered series by series, subtitle style, files of your own, and subtitles found and synced by the server.
platforms: Windows, Mac, iPhone
---

## Your languages, series by series

In **Settings → Audio & subtitles**, choose your audio language and your subtitle language among 30. Mira picks from the tracks of each file: your language first, then English. **Japanese first** puts a Japanese track ahead of the others, as most anime are best in their original language. Until you choose, Mira starts with your device’s language (Windows, Mac or iPhone). On Windows, **Custom order** takes language codes in your own order (`ger,deu,de,eng,en`).

Change the audio or subtitles of an episode, and Mira remembers it for the whole series: the next episodes get the same choice, matched by language and track name ("Full" rather than "Signs", subtitles off…). An episode without a similar track falls back on your preferred languages. These choices follow your account to your other devices.

The same page sets the size and style of subtitles (dark background, white or yellow, bold, height on screen), with a preview.

## In the player

The **audio and subtitles** menu shows both lists side by side, the current track ticked, with its language, codec and channels.

- **Load a file…** plays a subtitle file of your own (`.srt`, `.ass`, `.vtt`). Then **Keep on Jellyfin** sends it to Jellyfin, so your other devices have it too.
- **Search for subtitles…** appears when Jellyfin has a subtitle plugin, like Open Subtitles or Mira's own automatic subtitles. Mira says first what will be sent to the service.
- **Subtitle delay** (⋮ menu) shifts them if they come too early or too late, until the player closes. On iPhone, it is in the subtitles menu, in steps of 0.5 s.

## Automatic subtitles

Mira's extension on Jellyfin can find subtitles by itself for each new film or episode, in the languages you choose, check that they fit the video and sync them to it. On Windows and Mac, signed in as Jellyfin's administrator: **Settings → Maintenance → Automatic subtitles**.

1. Choose your **Languages**, and whether subtitles for the deaf and hard of hearing (SDH) are preferred, either, or avoided.
2. Choose the sources: **OpenSubtitles** works without an account (an account only raises the daily quota); **SubDL** needs a free API key of yours.
3. **Test the sources**, then turn on **Search automatically**.

When a title arrives, Mira's extension looks for a match, then tries again now and then for a month if nothing is reliable enough. It never searches while something plays. It keeps a candidate only when it is sure: a file whose fingerprint matches yours, or a release from the same group and source. Before keeping it, it checks its length against the video, and **Sync to the video** aligns it on the file's own text track or on its speech. **Log** shows what was kept, set aside and why.

> [!PRIVACY]
> The sources receive the file's fingerprint and size (OpenSubtitles), the title's IMDb or TMDb ids, season and episode number, title, year and language. Never the file's name, never anything about you. Everything runs on your Jellyfin server: your devices send nothing.

**Also search for what is already there** goes through your whole library once per language. It uses your daily quota, so it can take several days.
