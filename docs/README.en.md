<div align="center">
  <img src="../src/Mira.Desktop/Assets/mira.png" width="80" alt="Mira logo" />
  <h1>Mira</h1>
  <p><strong>Your Jellyfin library, like at the movies.</strong></p>
  <p>An app for Windows and Mac that plays everything itself, and Mira web for your iPhone or Android phone.<br />Free, with no account or subscription, and nothing leaves your server.</p>
  <p>
    <a href="https://github.com/sasou-web/Mira/releases/tag/v0.8.6"><strong>Download for Windows</strong></a> ·
    <a href="INSTALLATION-MAC.md">Mac</a> ·
    <a href="INSTALLATION-WEB.md">iPhone and Android</a> ·
    <a href="../README.md">Français</a>
  </p>
</div>

![Mira for Windows: the featured film, « Continuer à regarder » (continue watching) and recently added titles](screenshots/readme/windows-home.jpg)

## Why Mira?

[Jellyfin](https://jellyfin.org) keeps your films and series on your PC and serves them anywhere. Mira is the pleasant way to watch them. The interface is in French for now.

- **Beautiful from the start.** Posters, large artwork, summaries, cast, sagas: your library looks like a streaming service.
- **Pick up where you left off.** On the PC, the Mac or the phone, « Continuer à regarder » takes you back to the right minute, and the next episode follows on its own.
- **Playback inside Mira.** The mpv engine plays almost any file as it is: subtitles, audio tracks, chapters, intros skipped in one gesture.
- **Watch together.** A link is enough to start the same film at a friend's, at the same time, each at home.
- **No complicated setup.** No Jellyfin yet? Mira installs and prepares it for you. The phone connects by scanning a QR code.
- **Your privacy stays at home.** No Mira account, no ads, nothing collected; only the update check contacts GitHub, and Tailscale only if you sign Mira in to it.

## On your phone too

Mira web is served by your own Jellyfin server: nothing to get from the App Store, no Apple account. On iPhone, **Share → Add to Home Screen**, and it opens like an app, at home or anywhere with Tailscale.

<table>
  <tr>
    <td width="33%"><img src="screenshots/readme/web-home.jpg" alt="Mira web on iPhone: Home" /></td>
    <td width="33%"><img src="screenshots/readme/web-film.jpg" alt="Mira web on iPhone: a film's page" /></td>
    <td width="33%"><img src="screenshots/readme/web-series.jpg" alt="Mira web on iPhone: a series and its episodes" /></td>
  </tr>
  <tr><td align="center">Home</td><td align="center">A film</td><td align="center">A series</td></tr>
</table>

**Watch with someone who has no account.** A guest link, made in the settings, opens Mira for your friend, with no account and no Tailscale, straight into your “Watch together” group. It works on one device only, and when you take it back, your friend is out at once. It goes through Tailscale Funnel, which opens only Mira to the Internet, never the rest of Jellyfin (details, in French, in [SECURITY.md](../SECURITY.md#liens-dinvités-inviter-par-lien)).

## On your computer

<table>
  <tr>
    <td width="50%"><img src="screenshots/readme/windows-film.jpg" alt="A film's page in Mira for Windows, with « Reprendre » (resume)" /></td>
    <td width="50%"><img src="screenshots/readme/windows-series.jpg" alt="A series in Mira for Windows, with its episodes" /></td>
  </tr>
  <tr><td align="center">A film's page</td><td align="center">A series and its episodes</td></tr>
  <tr>
    <td width="50%"><img src="screenshots/readme/windows-films.jpg" alt="All films, with search, filters and sorting" /></td>
    <td width="50%"><img src="screenshots/readme/windows-subtitles.jpg" alt="Language and subtitle settings" /></td>
  </tr>
  <tr><td align="center">All your films</td><td align="center">Languages and subtitles</td></tr>
</table>

<sub>Captures of the real application, connected to a demo Jellyfin server (placeholder videos; posters and summaries from TMDb). <i>Arcane</i> © Riot Games, Fortiche; <i>Spider-Man: Across the Spider-Verse</i> © Sony Pictures Animation; the other titles belong to their rights holders.</sub>

## Install

| On | What you need | How |
| --- | --- | --- |
| **Windows** 10 or 11 (x64) | `Mira-0.8.6-win-x64-setup.exe` | Run the installer, connect your Jellyfin server, or let Mira install one. [Guide (French)](INSTALLATION.md) |
| **Mac** with Apple silicon, macOS 15 or later | `Mira-0.8.6-mac-arm64.dmg` | Drag Mira to Applications, allow it once (it is not signed by Apple). [Guide (French)](INSTALLATION-MAC.md) |
| **iPhone, iPad** (trial) | AltStore Classic, iOS 26 | The native app: library, search, its own player (Picture in Picture, AirPlay), sleep timer, Watch together and, away from home, Built-in Tailscale. Installed with your free Apple ID, refreshed each week by the server PC. [Guide (French)](INSTALLATION-IOS.md) |
| **iPhone, iPad, Android** | Nothing to download | In Mira for Windows: **Guide → Mira sur ton téléphone → Tout préparer**, then scan the QR code. [Guide (French)](INSTALLATION-WEB.md) |

Every file is in the [0.8.6 release](https://github.com/sasou-web/Mira/releases/tag/v0.8.6). You need a Jellyfin 10.9 or later server (Mira web is tried on 10.9, 10.10, 12.1 and 12.4 at every change; on 10.9 and 10.10, a film’s page has no row for its saga and an ASS subtitle keeps its style marks). Until the Windows files are signed, SmartScreen may show a warning: **More info → Run anyway**. Mira then updates itself.

<details>
<summary>The other Windows files</summary>

- **Mira-0.8.6-win-x64-portable.exe**: one file that runs as it is. Keep it in a writable folder of its own: it creates its `data` folder there.
- **Mira-0.8.6-win-x64.zip**: the application's whole folder, to extract anywhere.

The .NET runtime is included. The mpv player engine (31 MB) is downloaded on request, from mpv's Windows builds, SHA-256 checked; one already installed with mpv.net or Jellyfin MPV Shim is reused. The optional downloads page turns TorLink on in one click; TorLink is an independent project, and Mira provides no sources or content.
</details>

## Status

Mira **0.8.6** is a preview: it is used every day, but some things are still missing (choosing between versions of a title, signed Windows files) and HDR, multichannel audio and multiple monitors have not been tried everywhere.

- [Every feature and shortcut (French)](USER_GUIDE.md)
- [Release history](../CHANGELOG.md)
- [Report a bug](https://github.com/sasou-web/Mira/issues/new/choose) or [a vulnerability](../SECURITY.md)

## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- **What is signed.** The Windows files of each release: the installer `Mira-X.Y.Z-win-x64-setup.exe`, the portable `Mira-X.Y.Z-win-x64-portable.exe` and, inside `Mira-X.Y.Z-win-x64.zip`, `Mira.exe`, `Mira.dll` and `Mira.Core.dll`. GitHub Actions ([`windows.yml`](../.github/workflows/windows.yml)) builds them from the `main` commit the release publishes, never on a PC; SignPath Foundation signs them once the maintainer approves each signing request; they are then published at once, with their SHA-256, in an immutable release. Before signing, SignPath checks that each file carries the product name "Mira" and the release's version (artifact configurations in [`installer/signpath/`](../installer/signpath)). The uninstaller Inno Setup generates, the .NET runtime and the third-party libraries in the folder keep their own signature or have none. Mira for Mac is signed ad hoc, without an Apple developer account; Mira web (the Jellyfin plugin) is not a Windows executable.
- **Team roles.** [sasou-web](https://github.com/sasou-web) (ogsas) is the maintainer: his GitHub account is the only one that writes to `main`, he alone starts a release (`tools/release.ps1`) and approves each signing request in SignPath, with two-factor authentication on GitHub and on SignPath. Changes are written and tested by the Claude AI assistant (Anthropic) at his request, under his account and responsibility; nothing enters a release before GitHub Actions' checks pass (build, tests, Mira web against a real Jellyfin).
- **Privacy policy.** This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it, with one exception: the update check against GitHub (this repository's releases API), at startup then every 6 hours on Windows, at each launch on Mac. It sends nothing but the request itself (IP address, Mira's version) and reports nothing; on Windows, **Réglages → Mises à jour** turns it off. Nothing is collected about you or your library. The services it contacts have their own privacy policies: [GitHub](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement) (updates, Mira web), [npm](https://docs.npmjs.com/policies/privacy) (TorLink), the [OpenJS Foundation](https://privacy-policy.openjsf.org/) (Node.js, from nodejs.org), SourceForge (the mpv engine), Jellyfin (its installer) and [Tailscale](https://tailscale.com/privacy-policy) (Built-in Tailscale: once signed in, its servers see the device’s name, its system and its IP addresses; videos stay end-to-end encrypted); once started, TorLink is a BitTorrent client (peers, trackers, router), and its terminal relies on [WebView2](https://learn.microsoft.com/microsoft-edge/webview2/concepts/data-privacy), a Windows component that follows Windows’ diagnostic settings. Details: [SECURITY.md](../SECURITY.md).
- **Status.** Everything is in place, but the application to SignPath Foundation has not been filed yet: up to 0.8.6, the Windows files are unsigned and SmartScreen asks for confirmation.

## Build

<details>
<summary>Build, test and release Mira</summary>

On Windows, install the .NET 10 SDK, then run from the repository root:

```powershell
dotnet restore Mira.sln --configfile NuGet.Config
dotnet build Mira.sln -c Release --no-restore
dotnet run --project tests/Mira.Tests/Mira.Tests.csproj -c Release --no-build
dotnet run --project src/Mira.Desktop/Mira.Desktop.csproj -- --demo --data .artifacts/dev-profile
./tools/package.ps1
./tools/release.ps1   # the GitHub release vX.Y.Z: Windows (signed by SignPath Foundation), Mac and Mira web from GitHub Actions, signed manifest, notes (gh)
```

Tests use an executable harness, not `dotnet test`; a failure does not stop the following tests, `--filter <text>` picks tests by name, and `tests/Mira.Core.Tests` (the tests free of WPF) also runs on Linux and macOS. Baseline tests require neither a live server nor libmpv. The captures on this page are made by the [`gallery.yml`](../.github/workflows/gallery.yml) workflow (`tools/gallery`). [Contribution guide](../CONTRIBUTING.md).
</details>

## Licence

Original code: [MIT](../LICENSE). Dependencies retain their [own licences](../THIRD_PARTY_NOTICES.md). Mira is an independent project, not affiliated with Jellyfin; no film, episode or account comes with it.
