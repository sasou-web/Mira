# Mira

**Your Jellyfin library, with an integrated mpv player and a native Windows interface.**

[Download Windows preview](https://github.com/sasou-web/Mira/releases/tag/v0.5.1) · [Screenshot gallery](SCREENSHOTS.md) · [Français](../README.md)

![Mira home screen](screenshots/01-home.png)

Mira keeps Jellyfin as your library server. It provides a cinematic desktop UI, search and filters, favourites, movie and series details, and playback in the same application through libmpv.

- Fullscreen and movable mini-player, audio/subtitle tracks, speed, chapters and next episode.
- Most recently watched titles first, persistent playback reports and synchronization retries.
- Windows Start Menu identity, notification-area controls, taskbar thumbnail controls and system media session.
- Local artwork/cache, bundled typography, responsive hover transitions and reduced-motion preference.
- Optional TorLink page: if TorLink is installed separately, its terminal interface opens inside Mira and each finished download is placed in the Jellyfin library folders with Jellyfin-style names. Mira provides no sources or content.

## Install

Requires Windows 10 2004+ or Windows 11 **x64**, a running Jellyfin server, and a separately installed **x64 libmpv DLL**. The self-contained ZIP includes .NET, but not libmpv. The UI is currently in French.

1. From the release, run `Mira-0.5.1-win-x64-setup.exe` (per-user install, no administrator rights), or use `Mira-0.5.1-win-x64-portable.exe` (one file that creates its `data` folder beside it) or the `Mira-0.5.1-win-x64.zip` folder. The files are not signed yet, so SmartScreen may ask for confirmation.
2. Keep a portable copy in a writable folder of its own.
3. Connect to Jellyfin. A server on the same PC commonly uses `http://127.0.0.1:8096`.
4. In **Réglages → Lecture**, check player-engine detection or select `libmpv-2.dll` / `mpv-2.dll`. `mpv.exe` alone is insufficient. See the [mpv installation page](https://mpv.io/installation/) for Windows distributions.

The preview is unsigned and updates are manual. Preserve the `data` folder when updating; it contains settings, protected session credentials and pending playback reports. Do not publish it.

## Build

On Windows, install the .NET 8 SDK, then run from the repository root:

```powershell
dotnet restore Mira.sln --configfile NuGet.Config
dotnet build Mira.sln -c Release --no-restore
dotnet run --project tests/Mira.Tests/Mira.Tests.csproj -c Release --no-build
dotnet run --project src/Mira.Desktop/Mira.Desktop.csproj -- --demo --data .artifacts/dev-profile
./tools/package.ps1
```

Tests use an executable harness, not `dotnet test`. Baseline tests require neither a live server nor libmpv. Native playback and Windows integration checks are separate local fixtures. [Contribution guide](../CONTRIBUTING.md).

The repository gallery shows real app views using the built-in fictional demo, not bundled movies. Mira is an independent development preview: transcoding, multiple versions of a title, editable collections, signing, and wider HDR/audio/multi-monitor validation remain future work.

Original code: [MIT](../LICENSE). Dependencies retain their [own licences](../THIRD_PARTY_NOTICES.md).
