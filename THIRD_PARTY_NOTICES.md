# Third-party notices

Mira's original source code is licensed under MIT. This does not replace the licences of the following components. The release ZIP and the installer include the `licenses` directory, this file and the asset-specific notices. The portable executable contains the same components; its notices are this file and the `licenses` directory, published beside it in the repository, the ZIP and the release. The Mac disk image carries them in `Mira.app/Contents/Resources`, with the notice of the libraries it bundles (see [Mac](#mac)).

| Component | Use | Licence / notice |
| --- | --- | --- |
| .NET 10, WPF and Windows Forms | Self-contained Windows runtime; Forms is used for NotifyIcon only | MIT, .NET Foundation and contributors; `licenses/DOTNET-LICENSE.txt`, `licenses/WINDOWS-DESKTOP-LICENSE.txt`, `licenses/DOTNET-THIRD-PARTY-NOTICES.txt` |
| Microsoft.Data.Sqlite 10.0.12 | SQLite data access | MIT, .NET Foundation and contributors; .NET notice above |
| SQLitePCL.raw 2.1.12 | SQLite interop and native package | Copyright 2014–2024 SourceGear, LLC; Apache-2.0, `licenses/SQLITEPCLRAW-APACHE-2.0.txt` |
| SQLite 3.53.3 | Native database engine in the SQLitePCL.raw package | Public domain; [SQLite copyright statement](https://www.sqlite.org/copyright.html) |
| C#/WinRT runtime | Windows media-session interop | MIT, Microsoft Corporation; `licenses/CSWINRT-MIT.txt` |
| Windows SDK .NET targeting pack 10.0.19041.57 | Windows API projections | Microsoft Corporation; [Windows SDK licence](https://aka.ms/WinSDKLicenseURL) |
| Nunito Sans | Bundled typefaces (Mira web: Latin subsets in WOFF2) | SIL Open Font License 1.1; `licenses/NUNITO-SANS-OFL.txt`, upstream revision in `src/Mira.Desktop/Assets/Fonts/SOURCE.txt` |
| Phosphor Icons | Archived SVG assets from an earlier interface; icon paths of Mira web | MIT; `licenses/PHOSPHOR-MIT.txt`, revision in `src/Mira.Desktop/Assets/Phosphor/SOURCE.txt` |
| Microsoft.Web.WebView2 1.0.4258.31 | WebView2 SDK for the embedded TorLink terminal (managed assemblies and `WebView2Loader.dll`) | Microsoft Corporation, BSD-style licence; `licenses/WEBVIEW2-BSD.txt` |
| xterm.js 6.0.0 and @xterm/addon-fit 0.11.0 | Terminal renderer of the TorLink page, unmodified npm builds embedded as resources | MIT, the xterm.js authors; `licenses/XTERM-MIT.txt`, versions and integrity in `src/Mira.Desktop/Assets/TorLink/xterm/SOURCE.txt` |
| SharpCompress 1.0.0 | Reads the `.7z` archive of the libmpv build Mira installs on request | MIT, Adam Hathcock; `licenses/SHARPCOMPRESS-MIT.txt` |
| hls.js 1.7.3 | Mira web: plays HLS in browsers that do not have it built in (not Safari); unmodified npm build (`dist/hls.light.min.mjs`, without its source map comment) inside the Jellyfin plugin | Apache-2.0, Dailymotion and contributors; `licenses/HLSJS-APACHE-2.0.txt`, version in `src/Mira.Web/vendor/SOURCE.txt` |
| qrcode-generator 2.0.4 | Mira web: the QR code of its address, unmodified npm build inside the Jellyfin plugin | MIT, Kazuhiko Arase; `licenses/QRCODE-GENERATOR-MIT.txt` |
| mira-tailscale (Built-in Tailscale) | The Tailscale node of Mira for Windows and Mac: `mira-tailscale.exe` beside `Mira.exe` (the portable executable carries it inside and writes it into its profile), `Mira.app/Contents/MacOS/mira-tailscale` on Mac (its licences in `Mira.app/Contents/Resources/licenses/mira-tailscale`): Mira's own code (`src/Mira.Tailscale`, MIT) built with Go and unmodified Go modules | Tailscale (`tailscale.com`, BSD-3-Clause, Tailscale Inc & AUTHORS), wireguard-go (MIT), gVisor (Apache-2.0), the Go standard library and the `golang.org/x` modules (BSD-3-Clause), and the other modules listed with their exact versions in `licenses/mira-tailscale/MODULES.txt`, each licence file beside that list |
| Inno Setup 6.7.3 | Builds the Windows installer; its setup and uninstall program is part of `Mira-*-setup.exe` | Copyright Jordan Russell and Martijn Laan, [Inno Setup License](https://jrsoftware.org/files/is/license.txt); not part of the zip or the portable executable |

The current logo, icon geometry and fictional demo illustrations are original Mira assets. The gallery (`docs/SCREENSHOTS.md`) shows that offline demo. The three screenshots at the top of the README show Mira connected to a personal Jellyfin library: the posters and artwork visible in them belong to their respective rights holders and are shown only to illustrate the software. No media file, personal library data or Jellyfin cache is included in the repository or release.

## Mira web

The Jellyfin plugin `mira-jellyfin-*.zip` contains Mira's own code, the web app and, inside it, hls.js, qrcode-generator, Nunito Sans and Phosphor paths with their licence files (`vendor/`, `fonts/OFL.txt`). It is built against Jellyfin's public libraries (`Jellyfin.Controller`, GPL-2.0) but does not include them: the Jellyfin server that loads the plugin provides them.

## External player

**On Windows, libmpv is not included.** Mira loads a separately installed x64 library. On request (the installer's "Télécharger le moteur vidéo mpv" task or **Réglages → Lecture**), Mira downloads one fixed archive of the Windows builds listed on the [mpv installation page](https://mpv.io/installation/) (shinchiro's [mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake), from SourceForge), checks its SHA-256, and extracts `libmpv-2.dll` into the profile (`data\mpv`, with a `SOURCE.txt` naming its origin). The file comes from its distributor, not from Mira's releases. mpv and the libraries it includes, such as FFmpeg, keep their own licences (GPL for these builds); Mira's MIT licence does not relicense them. Sources: the [mpv project](https://github.com/mpv-player/mpv) and the build scripts linked above.

## Mac

**The Mac app includes libmpv and the libraries it needs, FFmpeg among them.** `Mira-*-mac-arm64.dmg` carries the Homebrew builds of the `mpv` formula and of its dependencies, copied into `Mira.app/Contents/Frameworks` by `tools/mac/package.sh`: unmodified, except for their load paths (rewritten so that they load from the app) and their code signatures (ad hoc). Homebrew builds FFmpeg with its GPL parts enabled, so FFmpeg, and libmpv with it, are distributed under the GPL; other libraries are under the LGPL or permissive licences. `Mira.app/Contents/Resources/licenses/HOMEBREW-LIBRARIES.txt`, written at each build, lists every bundled library with its formula, exact version, licence, project and source archive; the formulas' own licence files are in `licenses/homebrew/`, and the full GPL and LGPL texts in `licenses/GPL-2.0.txt`, `GPL-3.0.txt`, `LGPL-2.1.txt` and `LGPL-3.0.txt`. Source: the archive named for each library, built by the [Homebrew formula](https://github.com/Homebrew/homebrew-core) of that version; for three years from each release, sasou-web also provides the complete corresponding source of these libraries on request, through the [issues](https://github.com/sasou-web/Mira/issues). Mira's MIT licence does not relicense them. The app also contains the .NET runtime for macOS (MIT, notices above), Avalonia with SkiaSharp and HarfBuzzSharp (MIT; Skia under BSD-3-Clause, HarfBuzz under the Old MIT licence) and mira-tailscale, the node of Built-in Tailscale (table above).

## Optional external components

**TorLink and Node.js are not included.** The Downloads page runs TorLink unmodified: a copy already on the PC, or, when it is turned on there, one Mira installs for the current Windows account. That install is the official Node.js 24.21.0 Windows zip from nodejs.org (SHA-256 checked; `node.exe`, npm and `LICENSE` kept in `data\node`, MIT and the licences listed in that file), then the npm package `torlnk` 1.9.0 ([TorLink](https://github.com/baairon/torlink), MIT, by bairon) with its npm dependencies in `data\torlink`. Both come from their publishers, not from Mira's releases, and keep their own licences. **The Microsoft Edge WebView2 Runtime is not included** either: the terminal uses the runtime provided with Windows or installed from Microsoft.

**Jellyfin Server is not included.** When asked to ("Installer Jellyfin sur ce PC"), Mira downloads the official Windows installer from `repo.jellyfin.org` (the release and SHA-256 listed in winget's `Jellyfin.Server` manifest) and runs it; Jellyfin is then installed and updated as its own program, under its own licence (GPL-2.0). Source: the [Jellyfin project](https://github.com/jellyfin/jellyfin).

Mira is an independent client. It is not an official Jellyfin, mpv or TorLink release.
