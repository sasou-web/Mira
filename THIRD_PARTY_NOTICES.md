# Third-party notices

Mira's original source code is licensed under MIT. This does not replace the licences of the following components. The release ZIP includes the `licenses` directory and the asset-specific notices.

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

The current logo, icon geometry and fictional demo illustrations are original Mira assets. The screenshots show that offline demo; no movies, series, personal media or Jellyfin cache are included in the repository or release.

## External player

**libmpv is not included.** Mira loads a separately installed x64 library. mpv and its dependencies have their own licences, depending on the build. Obtain the library and its notices from its distributor; Mira's MIT licence does not relicense it. See the [mpv project](https://github.com/mpv-player/mpv) and its [installation page](https://mpv.io/installation/).

Mira is an independent client. It is not an official Jellyfin or mpv release.
