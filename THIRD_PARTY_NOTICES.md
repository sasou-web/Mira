# Third-party notices

Mira's original source code is licensed under MIT. This does not replace the licences of the following components. The release ZIP and the installer include the `licenses` directory, this file and the asset-specific notices. The portable executable contains the same components; its notices are this file and the `licenses` directory, published beside it in the repository, the ZIP and the release.

| Component | Use | Licence / notice |
| --- | --- | --- |
| .NET 8, WPF and Windows Forms | Self-contained Windows runtime; Forms is used for NotifyIcon only | MIT, .NET Foundation and contributors; `licenses/DOTNET-LICENSE.txt`, `licenses/WINDOWS-DESKTOP-LICENSE.txt`, `licenses/DOTNET-THIRD-PARTY-NOTICES.txt` |
| Microsoft.Data.Sqlite 8.0.20 | SQLite data access | MIT, .NET Foundation and contributors; .NET notice above |
| System.Security.Cryptography.ProtectedData 8.0.0 and System.Memory 4.5.3 | Windows DPAPI and transitive managed dependency | MIT, .NET Foundation and contributors; .NET notice above |
| SQLitePCL.raw 2.1.6 | SQLite interop and native package | Copyright 2014–2023 SourceGear, LLC; Apache-2.0, `licenses/SQLITEPCLRAW-APACHE-2.0.txt` |
| SQLite | Native database engine in the SQLitePCL.raw package | Public domain; [SQLite copyright statement](https://www.sqlite.org/copyright.html) |
| C#/WinRT runtime | Windows media-session interop | MIT, Microsoft Corporation; `licenses/CSWINRT-MIT.txt` |
| Windows SDK .NET targeting pack 10.0.19041.56 | Windows API projections | Microsoft Corporation; [Windows SDK licence](https://aka.ms/WinSDKLicenseURL) |
| Nunito Sans | Bundled typefaces | SIL Open Font License 1.1; `licenses/NUNITO-SANS-OFL.txt`, upstream revision in `src/Mira.Desktop/Assets/Fonts/SOURCE.txt` |
| Phosphor Icons | Archived SVG assets from an earlier interface | MIT; `licenses/PHOSPHOR-MIT.txt`, revision in `src/Mira.Desktop/Assets/Phosphor/SOURCE.txt` |
| Microsoft.Web.WebView2 1.0.4258.31 | WebView2 SDK for the embedded TorLink terminal (managed assemblies and `WebView2Loader.dll`) | Microsoft Corporation, BSD-style licence; `licenses/WEBVIEW2-BSD.txt` |
| xterm.js 6.0.0 and @xterm/addon-fit 0.11.0 | Terminal renderer of the TorLink page, unmodified npm builds embedded as resources | MIT, the xterm.js authors; `licenses/XTERM-MIT.txt`, versions and integrity in `src/Mira.Desktop/Assets/TorLink/xterm/SOURCE.txt` |
| Inno Setup 6.7.3 | Builds the Windows installer; its setup and uninstall program is part of `Mira-*-setup.exe` | Copyright Jordan Russell and Martijn Laan, [Inno Setup License](https://jrsoftware.org/files/is/license.txt); not part of the zip or the portable executable |

The current logo, icon geometry and fictional demo illustrations are original Mira assets. The gallery (`docs/SCREENSHOTS.md`) shows that offline demo. The three screenshots at the top of the README show Mira connected to a personal Jellyfin library: the posters and artwork visible in them belong to their respective rights holders and are shown only to illustrate the software. No media file, personal library data or Jellyfin cache is included in the repository or release.

## External player

**libmpv is not included.** Mira loads a separately installed x64 library. mpv and its dependencies have their own licences, depending on the build. Obtain the library and its notices from its distributor; Mira's MIT licence does not relicense it. See the [mpv project](https://github.com/mpv-player/mpv) and its [installation page](https://mpv.io/installation/).

## Optional external components

**TorLink is not included.** The TorLink page runs a copy installed separately on the PC, unmodified, with its own Node.js runtime and licences. **The Microsoft Edge WebView2 Runtime is not included** either: the terminal uses the runtime provided with Windows or installed from Microsoft.

Mira is an independent client. It is not an official Jellyfin, mpv or TorLink release.
