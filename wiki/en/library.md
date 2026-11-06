---
title: Your library
short: Your library
description: Home, films and series, search, favourites, collections, and titles kept on your computer to watch offline.
platforms: Windows, Mac, iPhone
---

## Home

Home opens on a large banner of your titles, then **Continue watching**: the films and series you started, the most recent first, one card per series, with the time left. Below come the next episodes, recently added titles and the rows you chose.

- **Home rows** (Settings → Appearance) lets you reorder rows or hide them, including a "New in…" row per library. The layout is kept per device.
- Right-click a card in Continue watching (long press on iPhone) and choose **Remove from Continue watching** to forget it until you play it again. **Undo** puts it back.

## Movies and series

**Movies** and **Shows & anime** each have their own page, with genre chips, filters (genre, year, progress, library) and sorting (added, title, year, rating, last played). **Favorites** is a filter too.

On a series' poster, a number says how many episodes are left to watch: a new episode of a series you follow shows up in the library.

A title's page shows the picture of the file you would play ("4K · HDR10", "1080p") and the time it would end if you started now. On a series, it shows the next episode ("Resume · S01 E07"). Below: the cast and crew (a click shows their titles in your library), the extras (making-of, deleted scenes; trailers have [their own button](library.md#trailers)), similar titles and the other films of the same collection.

The buttons next to **Play** keep their shape and only change state: **Favorite**, **Watched**, and **Keep on this PC** (Windows) or **Keep on this Mac**. Their names show on hover.

## Trailers

When a title has a trailer, a **Trailer** button shows on its page (on iPhone, in **…**). A trailer kept on your server plays in Mira, like a film: put it in a `trailers` folder next to the film, or name it `Film (2020)-trailer.mkv`. Otherwise, Jellyfin often knows one on YouTube (from TMDb): the button says so ("Trailer on YouTube, in your browser") and opens it in your browser, never in Mira.

With **Settings → Playback → Trailer before the film** (Windows and Mac, off by default), a film started from the beginning plays its trailer kept on your server first, as at the cinema; **Skip trailer** goes straight to the film. Never a trailer from the web, never when resuming, never in a group.

## Marking watched

A title counts as watched when it ends, when you stop past 90 %, or when you go to the next episode from the credits. You can also mark it by hand, from its page or its menu (right-click, or long press). With several seasons, **Mark season as watched** marks them all at once.

## Search

<kbd>Ctrl</kbd> + <kbd>K</kbd> or <kbd>Ctrl</kbd> + <kbd>F</kbd> on Windows, <kbd>⌘</kbd> + <kbd>F</kbd> on Mac. From two letters, Mira shows the people (actors, directors) whose name matches, then titles, episodes and collections.

## Collections

On Windows and Mac, a film's or series' menu has **Add to collection…**: tick the collections it belongs to, or type a name to create a new one ("Friday nights", "To show Léa"). A collection can be renamed and deleted from its page; its titles stay in your library and no file moves. Home has a **Collections** row.

You need Jellyfin's administrator account, or an account Jellyfin allows to manage collections.

> [!TIP]
> **Settings → Maintenance → Group my movies into collections** (administrator) asks Jellyfin to group the films of a same saga automatically, from TMDb.

## Watch without a connection

On Windows and Mac, **Keep on this PC** (or **Keep on this Mac**) on a title's page downloads the original file from Jellyfin, with its subtitles, to watch it on a train or a plane. Kept titles are in **On this PC** in the left rail. Mira plays them before asking Jellyfin, and the next episode follows if you kept it too. Your Jellyfin account needs the right to download.

## Keyboard (Windows)

| Action | Keys |
| --- | --- |
| Search | <kbd>Ctrl</kbd> + <kbd>K</kbd> or <kbd>Ctrl</kbd> + <kbd>F</kbd> |
| Home, Movies, Shows, Whole library | <kbd>Ctrl</kbd> + <kbd>1</kbd> to <kbd>Ctrl</kbd> + <kbd>4</kbd> |
| From a poster to its neighbour | arrow keys |
| A poster's menu | <kbd>Shift</kbd> + <kbd>F10</kbd>, or right-click |
| Back | <kbd>Esc</kbd>, <kbd>Alt</kbd> + <kbd>←</kbd>, or the mouse's back button |
| Getting started guide | <kbd>F1</kbd> |
