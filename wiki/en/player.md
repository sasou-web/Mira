---
title: The player
short: The player
description: Shortcuts, intros skipped, picture in picture, sleep timer, picture and sound settings, and playback away from home.
platforms: Windows, Mac, iPhone
---

On Windows and Mac, Mira plays with **mpv**, in its own window: almost every file plays as it is, with its audio tracks, subtitles and chapters. On iPhone, Mira has its own player built on Apple's.

## The controls

The bar shows the title and episode on the left, the chapter and time on the right. Below: play/pause, next episode and volume on the left; **⋮** (quality, sleep timer, subtitle delay, information), audio and subtitles, mini player, picture in picture and full screen on the right. Controls hide after a few seconds without moving the mouse.

- The seek bar is cut into chapters; hovering shows the time, the chapter and, when Jellyfin has prepared them, a preview of the picture.
- Volume goes up to 200 % for a title recorded too low: past 100 %, the sound is amplified then limited, without clipping.
- A click on the picture pauses or resumes, a double click goes full screen. A click near the bars only shows the controls, never pauses by mistake.

### Keyboard

| Action | Windows | Mac |
| --- | --- | --- |
| Play / pause | <kbd>Space</kbd> or <kbd>K</kbd> | <kbd>Space</kbd> or <kbd>K</kbd> |
| Back / forward 10 seconds | <kbd>←</kbd> / <kbd>→</kbd>, <kbd>J</kbd> / <kbd>L</kbd> | <kbd>←</kbd> / <kbd>→</kbd>, <kbd>J</kbd> / <kbd>L</kbd> |
| Back / forward 1 minute | <kbd>Shift</kbd> + <kbd>←</kbd> / <kbd>→</kbd> | <kbd>⇧</kbd> + <kbd>←</kbd> / <kbd>→</kbd> |
| Jump to 0 %, 10 %… 90 % | <kbd>0</kbd> to <kbd>9</kbd> | <kbd>0</kbd> to <kbd>9</kbd> |
| Volume | <kbd>↑</kbd> / <kbd>↓</kbd>, mouse wheel | <kbd>↑</kbd> / <kbd>↓</kbd> |
| Mute | <kbd>M</kbd> | <kbd>M</kbd> |
| Subtitles off / on | <kbd>C</kbd> | <kbd>C</kbd> |
| Full screen | <kbd>F</kbd>, <kbd>F11</kbd>, double click | <kbd>F</kbd>, <kbd>⌃</kbd> + <kbd>⌘</kbd> + <kbd>F</kbd>, double click |
| Mini player | <kbd>I</kbd> | <kbd>I</kbd>, <kbd>⌘</kbd> + <kbd>[</kbd> |
| Skip the intro, recap or credits | <kbd>S</kbd> | <kbd>S</kbd> |
| Next episode | <kbd>N</kbd> | <kbd>N</kbd> |
| Back, leave full screen | <kbd>Esc</kbd> | <kbd>Esc</kbd> |

On Mac, **Help → Keyboard Shortcuts** (<kbd>⌘</kbd> + <kbd>/</kbd>) lists them all, the library's too.

### Media keys

The keyboard's play and track keys, a headset's button and the system's own controls work the player even while Mira is behind another window, the mini player and picture in picture included:

- **Windows**: the media keys, the media overlay that shows with the volume, and the play, ±10 s and next-episode buttons of Mira's taskbar thumbnail and notification-area icon.
- **Mac**: <kbd>F8</kbd> (play / pause), <kbd>F7</kbd> (back 10 seconds), <kbd>F9</kbd> (next episode, when there is one), the button of AirPods or a headset, and **Now Playing** in Control Center and the menu bar, with the title, its poster, ±10 s and a bar to move through the film.

In a Watch together group, they act for the group, as the player's own keys do. When the player closes, the keys go back to your other apps.

### On iPhone

Tap to show the controls. Double tap on a side to go back or forward 10 seconds (taps add up). Swipe up or down on the left for brightness, on the right for volume, across to seek. Press and hold to play at ×2, pinch to fill the screen, swipe down to shrink the player. The lock keeps stray touches away.

## Intros, credits and the next episode

**Skip intro**, **Skip credits** and recap buttons appear from Jellyfin's media segments (Jellyfin 10.10 or later, from the server or a plugin like Intro Skipper) or from the file's chapter names. A chapter named "Intro" followed by one named as the opening ("Opening", "OP") is the cold open, part of the episode: no button over it. With **Settings → Playback → Skip intros and recaps**, Mira skips them without asking, once each: going back into one plays it again.

**Next episode** appears during the end credits or in the last 20 seconds. With **Play episodes back to back**, a 10-second countdown starts; **Cancel** stays on this episode.

After 3 episodes in a row with nothing touched (keyboard, mouse, trackpad, controller, or the screen on iPhone), Mira does not start the next one: it stops at the end of the episode and asks **Are you still watching?**, so a night asleep in front of a series does not mark ten episodes watched. **Keep watching** plays the next one. Never in a watch party. To turn it off: **Settings → Playback → Ask if you’re still watching**.

Mira resumes a little before where you stopped, 5 seconds by default, to pick up the thread (**Settings → Playback**). **From the beginning** on a title's page starts over.

## Mini player and picture in picture

- **Mini player**: the video keeps playing against the right edge of the library while you browse. It tucks itself away, a sliver of the video showing. Rest the pointer on it to bring it out, click the picture to get the whole player back, drag it up or down to change its height.
- **Picture in picture** (Windows and Mac): the video in a small window of its own, above your other windows, with its subtitles. On iPhone, Apple's Picture in Picture.

## Sleep timer

In the player's **⋮** menu: pause in 15 minutes, 30 minutes, 1 hour, or at the end of the episode. On iPhone, it is on the title's page. It is cleared when the player closes.

## Picture and sound

- **Picture** (Settings → Playback, Windows and Mac): **Standard** (mpv's own rendering, the lightest), **Cinema** (the default: smooth motion on any screen, no banding in gradients) and **Maximum** (with the finest scaling). If the computer drops frames, Mira steps down one level by itself and tells you.
- **Audio output** (Windows and Mac, Settings → Audio & subtitles): the output device, the number of channels, and **Sound sent to the amplifier as is** for an amplifier or sound bar that decodes Dolby and DTS. On Windows, over HDMI, Dolby TrueHD, Atmos and DTS-HD too; on a Mac, Dolby Digital and DTS only (macOS sends nothing larger untouched), the rest decoded by Mira. **Test the audio output** plays a tone on each speaker. In the player, **Audio** switches the output, and **⋮ → Info** says where the sound goes and whether it reaches the amplifier undecoded.
- **HDR** (Windows and Mac): **⋮ → Info**, line **Colours**, says the film's range (SDR, HDR10, HDR10+, HLG, Dolby Vision) and what reaches the screen. On Windows, HDR goes to the screen as it is when Windows' HDR is on, and is converted to SDR otherwise; on a Mac, it is always converted to SDR ([why](mac.md#hdr-films)).
- **Night sound** (Windows and Mac, in the player's **Audio and subtitles** menu, under the audio tracks): loud passages come down and voices come up, to watch at a low volume late at night. It applies at once and stays on for the next videos. It does nothing while the sound goes to the amplifier as is, and isn't on iPhone.
- **Audio delay** (⋮ menu): if lips and voices don't match, in steps of 0.1 s, until the player closes.

## Away from home: quality

At home, Mira plays the file as it is. Away, a 4K film may not fit your connection: **Quality** (Settings → Playback and the player's **⋮** menu) sets a limit, or **Auto** measures the connection to Jellyfin. Under the limit, Jellyfin converts the file while you watch. If the connection drops, or the video stops coming for 15 seconds, Mira waits for Jellyfin, reopens the stream once where it was, and only then says it cannot play. When the computer sleeps (a laptop's lid closed), the film pauses, and on waking Mira reopens it where it was, still paused.

Converting is heavy for the server. On the Jellyfin PC, Mira can have the graphics card do it: **Settings → Maintenance → Convert with the graphics card**, tried first on one of your titles.
