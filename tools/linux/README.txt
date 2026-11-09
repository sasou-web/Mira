Mira for Mac, Linux preview (not a release)
===========================================

The Mac app of Mira, built for Linux x64 so it can be tried on a Linux desktop:
the same pages, player, settings and party as on a Mac. It is not an official
Mira for Linux: no signature, no automatic updates, nothing of macOS (menu bar,
Dock, trackpad gestures, keychain, notifications, Now Playing).

Needs
- A desktop session (X11, or Wayland with XWayland).
- mpv's library, libmpv.so.2:
    Debian, Ubuntu, Mint:  sudo apt install libmpv2
    Fedora (RPM Fusion):   sudo dnf install mpv-libs
    Arch, Manjaro:         sudo pacman -S mpv
    openSUSE:              sudo zypper install libmpv2
- ICU and the X11 libraries, already there on a desktop distribution. A
  minimal system (WSL, a server) needs them too, on Debian or Ubuntu:
    sudo apt install libicu74 libice6 libsm6 libx11-6 libx11-xcb1 libxcursor1 libxrandr2 libxi6 libxext6 libfontconfig1 libgl1 libegl1 libgl1-mesa-dri
  (libicu72 on Debian 12)

On Windows 11 (WSL)
    wsl --install -d Ubuntu-24.04      (PowerShell as administrator, once)
  then, in Ubuntu, the two apt lines above and ./mira: its window opens on the
  Windows desktop. ./mira draws it at scale 2, as on a Mac's Retina screen
  (AVALONIA_GLOBAL_SCALE_FACTOR=1.5 ./mira for another). Scrolling and video go
  through WSL's remote display and OpenGL emulation: their smoothness says
  nothing of a Mac's.

Start
    ./mira
or with a profile of its own, apart from anything else:
    ./mira --data ~/mira-preview
Too small or too big on your screen:
    AVALONIA_GLOBAL_SCALE_FACTOR=2 ./mira

Where it keeps its files
    ~/.local/share/Mira (or the folder given with --data). The Jellyfin
    session is kept in session.json there (mode 0600): there is no keychain.

Remove
    Delete this folder and ~/.local/share/Mira.
