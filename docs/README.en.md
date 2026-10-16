# Mira

**Your Jellyfin library, with an integrated mpv player and a native Windows interface.**

[Download Windows preview](https://github.com/sasou-web/Mira/releases/tag/v0.7.8) · [Screenshot gallery](SCREENSHOTS.md) · [Français](../README.md)

![Mira home screen](screenshots/01-home.png)

Mira keeps Jellyfin as your library server. It provides a cinematic desktop UI, search and filters, favourites, movie and series details with cast and similar titles, and playback in the same application through libmpv.

- A clean playback bar split by chapters, fullscreen and movable mini-player, audio/subtitle tracks, speed and next episode.
- Automatic updates from 0.5.2: each new release is downloaded in the background, checked (Mira's signature, size and SHA-256) and installed when Mira closes, leaving the `data` folder untouched.
- Most recently watched titles first, persistent playback reports and synchronization retries.
- Windows Start Menu identity, notification-area controls, taskbar thumbnail controls and system media session.
- Local artwork/cache, bundled typography, responsive hover transitions and reduced-motion preference.
- Optional downloads page: turning TorLink on installs it when needed (Node.js and the torlnk npm package, checked, in Mira's data folder) and opens its terminal interface inside Mira; each finished download is moved (never duplicated) into the Jellyfin library folders with Jellyfin-style names. TorLink is an independent project; Mira provides no sources or content.

## Install

Requires Windows 10 2004+ or Windows 11 **x64** and a running Jellyfin server. The packages include .NET; the **libmpv** player engine is downloaded on request (31 MB, from mpv's Windows builds, SHA-256 checked), or an existing one is reused. The UI is currently in French.

1. From the release, run `Mira-0.7.8-win-x64-setup.exe` (per-user install, no administrator rights), or use `Mira-0.7.8-win-x64-portable.exe` (one file that creates its `data` folder beside it) or the `Mira-0.7.8-win-x64.zip` folder. Until the files are signed (see [Code signing policy](#code-signing-policy)), SmartScreen may ask for confirmation.
2. Keep a portable copy in a writable folder of its own.
3. Connect to Jellyfin 10.9 or later. A server on the same PC commonly uses `http://127.0.0.1:8096`; on the network, its IP address or name is enough (`192.168.1.20`, `nas:8096`), as is the address copied from Jellyfin's web page. With no server yet, "Installer Jellyfin sur ce PC" below the sign-in form handles the rest. Mira downloads Jellyfin's official installer (SHA-256 checked) and runs it after Windows asks for consent. It creates Films, Séries and Animes folders and completes Jellyfin's first-run wizard in French with your account. Then it signs in.
4. Keep the installer's "Télécharger le moteur vidéo mpv" box ticked, or use **Installer le moteur mpv** in **Réglages → Lecture** (Mira also offers it before the first playback). A `libmpv-2.dll` / `mpv-2.dll` already on the PC can be selected there instead; `mpv.exe` alone is insufficient.

From 0.5.2 on, Mira updates itself (**Réglages → Mises à jour**); older copies need 0.5.2 installed once by hand. When updating by hand, preserve the `data` folder: it contains settings, protected session credentials and pending playback reports. Do not publish it.

## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- **What is signed.** The Windows files of each release: the installer `Mira-X.Y.Z-win-x64-setup.exe`, the portable `Mira-X.Y.Z-win-x64-portable.exe` and, inside `Mira-X.Y.Z-win-x64.zip`, `Mira.exe`, `Mira.dll` and `Mira.Core.dll`. GitHub Actions ([`windows.yml`](../.github/workflows/windows.yml)) builds them from the `main` commit the release publishes, never on a PC; SignPath Foundation signs them once the maintainer approves each signing request; they are then published at once, with their SHA-256, in an immutable release. Before signing, SignPath checks that each file carries the product name "Mira" and the release's version (artifact configurations in [`installer/signpath/`](../installer/signpath)). The uninstaller Inno Setup generates, the .NET runtime and the third-party libraries in the folder keep their own signature or have none. Mira for Mac is signed ad hoc, without an Apple developer account; Mira web (the Jellyfin plugin) is not a Windows executable.
- **Team roles.** sasou-web (ogsas) is the maintainer: his GitHub account is the only one that writes to `main`, he alone starts a release (`tools/release.ps1`) and approves each signing request in SignPath, with two-factor authentication on GitHub and on SignPath. Changes are written and tested by the Claude AI assistant (Anthropic) at his request, under his account and responsibility; nothing enters a release before GitHub Actions' checks pass (build, tests, Mira web against a real Jellyfin).
- **Privacy policy.** This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it, with one exception: the update check against GitHub (this repository's releases API), at startup then every 6 hours on Windows, at each launch on Mac. It sends nothing but the request itself (IP address, Mira's version) and reports nothing; on Windows, **Réglages → Mises à jour** turns it off. Nothing is collected about you or your library. Details: [SECURITY.md](../SECURITY.md).
- **Status.** Application to SignPath Foundation in progress: up to 0.7.8, the Windows files are unsigned and SmartScreen asks for confirmation.

## Build

On Windows, install the .NET 10 SDK, then run from the repository root:

```powershell
dotnet restore Mira.sln --configfile NuGet.Config
dotnet build Mira.sln -c Release --no-restore
dotnet run --project tests/Mira.Tests/Mira.Tests.csproj -c Release --no-build
dotnet run --project src/Mira.Desktop/Mira.Desktop.csproj -- --demo --data .artifacts/dev-profile
./tools/package.ps1
./tools/release.ps1   # the GitHub release vX.Y.Z: Windows (signed by SignPath Foundation), Mac and Mira web from GitHub Actions, signed manifest, notes (gh)
```

Tests use an executable harness, not `dotnet test`; a failure does not stop the following tests, `--filter <text>` picks tests by name, and `tests/Mira.Core.Tests` (the tests free of WPF) also runs on Linux and macOS. Baseline tests require neither a live server nor libmpv. Native playback and Windows integration checks are separate local fixtures. [Contribution guide](../CONTRIBUTING.md).

The repository gallery shows real app views using the built-in fictional demo, not bundled movies. Mira is an independent development preview: transcoding, multiple versions of a title, editable collections, release signing (SignPath Foundation application in progress), and wider HDR/audio/multi-monitor validation remain future work.

Original code: [MIT](../LICENSE). Dependencies retain their [own licences](../THIRD_PARTY_NOTICES.md).
