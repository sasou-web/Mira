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
- ICU, already there on every desktop distribution (libicu).

Start
    ./mira
or with a profile of its own, apart from anything else:
    ./mira --data ~/mira-preview

Where it keeps its files
    ~/.local/share/Mira (or the folder given with --data). The Jellyfin
    session is kept in session.json there (mode 0600): there is no keychain.

Remove
    Delete this folder and ~/.local/share/Mira.
