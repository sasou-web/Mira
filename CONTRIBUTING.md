# Contributing to Mira

Mira is still a development version. Precise feedback on playback, sync and the interface is welcome. The interface and discussions can be in French; English issues and pull requests are welcome too.

## Set up the project

You need Windows 10 2004+ or Windows 11 x64, Git and the .NET 10 SDK. Node.js is only needed for the stand-in Jellyfin server. An x64 libmpv library is needed for playback tests, and a TorLink installation for TorLink tests, not for the basic tests. `tools/import-xterm.ps1` re-imports xterm.js from npm and checks its integrity.

```powershell
git clone https://github.com/sasou-web/Mira.git
cd Mira
dotnet restore Mira.sln --configfile NuGet.Config
dotnet build Mira.sln -c Release --no-restore
dotnet run --project tests/Mira.Tests/Mira.Tests.csproj -c Release --no-build
dotnet run --project src/Mira.Desktop/Mira.Desktop.csproj -- --demo --data .artifacts/dev-profile
```

The test projects are assertion executables: use `dotnet run`, not `dotnet test`. A failure doesn’t stop the following tests; the final summary counts passes and failures, and the exit code is 1 if a test failed. `-- --filter <text>` only runs the tests whose name contains that text, `-- --list` names them. `tests/Mira.Core.Tests` holds the tests without WPF and also runs on Linux and macOS; `tests/Mira.Tests` runs them on Windows along with the app’s own.

## Propose a change

1. Open an issue for a major feature or an architecture change.
2. Create a branch and keep the pull request to one identifiable problem.
3. Explain the behavior before/after and the checks you ran. Attach a screenshot for a visible change.
4. Build and run the tests above. For the player and TorLink, follow the isolated tests in [VALIDATION.md](docs/VALIDATION.md); `tools/torlink-check.ps1` never uses TorLink’s real state.

Use a separate `--data` profile and test media. Don’t include a `data` folder, SQLite cache, Jellyfin token, password, personal media or log containing a private URL. The repository’s screenshots use the built-in demo.

## Distribution and screenshots

```powershell
./tools/package.ps1
./tools/capture-gallery.ps1
```

Before a release, add the version’s entry to `src/Mira.Core/WhatsNew.json`: a one-sentence summary and two to five short points, with one of Mira’s icons. The tests check it. This text shows in Mira after the update; `package.ps1` (and `release.ps1`) also draw `.artifacts/release-notes-<version>.md` from it, the text of the release page on GitHub, which is in English (write the English of each point first). The details stay in `CHANGELOG.md`.

Release files are produced in `dist/packages`, from fresh folders, without touching a local installation’s profile: archive, portable executable and, if [Inno Setup 6](https://jrsoftware.org/isinfo.php) is installed (or given with `-Iscc`), the installer described by `installer/Mira.iss`. None of them contains libmpv. The screenshot script generates a temporary profile and exports the public views to `docs/screenshots`.

To publish, on the PC that has the signing key: `git checkout main; git pull --ff-only; .\tools\release.ps1`. The script checks gh, the key, a clean tree with no file outside git, that `main` is GitHub’s and that the version isn’t published yet, then waits for `ci.yml` to have passed on that commit (if it fails, it reruns what failed once). It creates the `vX.Y.Z` release as a **draft** on that commit, with its notes and no files: a draft is only visible to maintainers and has no tag yet. It makes the `release/vX.Y.Z` branch at that commit and runs `windows.yml`, `mac.yml`, `web.yml` and `ios.yml` there for this draft (`main` can move meanwhile): they build from the same commit and attach to it the Windows files (built by GitHub, never on the PC, and signed by SignPath Foundation: two signing requests to approve on https://app.signpath.io while the script waits, the app then the installer), the Mac disk image, the Jellyfin extension (`mira-jellyfin-X.Y.Z.zip`, `jellyfin-manifest.json`) and the unsigned iPhone app (`Mira-X.Y.Z-ios.ipa`), and the script waits for them to be attached. Whatever fails (a Mac machine that never came, a cut download, a flaky test) is rerun by itself, twice at most; tests that pull requests have already passed (iPhone simulator, Tailscale on Mac, icons) only warn during a release. Stopped midway, the same `.\tools\release.ps1` picks up where it was (`.artifacts/release-X.Y.Z.json`) without rerunning what succeeded; each call to GitHub is tried four times. `tools/release-check.ps1` (in `ci.yml`) runs the script against a fake GitHub. It then downloads these files, checks them (Authenticode signatures included), signs `mira-update.json` for all platforms, checks that signature and that the draft has exactly its 14 files, (a missing or different file is sent back to its workflow), then publishes the release in one go, with its notes, and starts the Homebrew cask update. `-DryRun` does everything but publish: the draft stays invisible, and the next run publishes it without rebuilding anything; `-Unsigned` publishes unsigned Windows files (as long as SignPath hasn’t accepted the project). The four workflows run as they are at the published commit: a change to these workflows must be on `main` before the release. The code signing policy is in the README; SignPath’s artifact configurations are copied in `installer/signpath/`.

The script also reports if immutable releases aren’t turned on for the repository. Once turned on (**Settings → General → Releases → Enable release immutability**), they prevent changing a published release, its files as well as its tag; the script then checks the published release with `gh release verify` (gh 2.81 or later).

### Update signing

Installed copies only update from a release that publishes `mira-update.json` and `mira-update.json.sig`, signed by a key listed in `UpdateKeys.Trusted` (`src/Mira.Core/Updates/UpdateManifest.cs`). The manifest describes the Windows files (`files`, the only part copies 0.5.2 to 0.7.4 read) and, from 0.7.5, the Mac disk image and the Jellyfin extension (`others`): Mira for Windows only installs the extension after checking it. `release.ps1` signs last, once all the release’s files are gathered; `package.ps1` no longer signs, except with `-SigningKey` (the test key of `update-check.ps1`). A signature by a key missing from `UpdateKeys.Trusted` is refused.

```powershell
dotnet run --project tools/Mira.Release -c Release -- verify --version X.Y.Z --packages dist\packages --others dist\release   # signature and files, without the key
dotnet run --project tools/Mira.Release -c Release -- export-backup --out E:\mira-signing-backup.pem   # backup copy of the key, protected by a passphrase
dotnet run --project tools/Mira.Release -c Release -- import-backup --in E:\mira-signing-backup.pem    # on a new PC or Windows account, or if the key is gone
dotnet run --project tools/Mira.Release -c Release -- keygen-backup --out E:\mira-backup-key.pem       # once only: the backup key
```

The key stays encrypted by Windows for the current account in `%APPDATA%\Mira Release\update-signing.key`, outside the repository; never commit or share it. Any program run under this Windows account can use it: an account reserved for releases limits that risk. Without it, new releases aren’t offered to installed copies.

**If the key can’t be found, don’t run `keygen`**: a new key would sign releases that every installed copy refuses. Restore it with `import-backup`, from its backup copy (`export-backup`) or from the backup key. That one is created once only, with `keygen-backup`: it’s only written to the file given, encrypted by its passphrase, never to the Windows profile. Keep the file off the PC (USB stick, safe) and the passphrase somewhere else. The command shows its public key: add it to `UpdateKeys.Trusted` and publish it in a release signed by the main key; updated copies will then accept the backup key if the main one is lost. To change keys, likewise add the new public key to `UpdateKeys.Trusted` in a release still signed by the old one.

Installed copies query `api.github.com/repos/sasou-web/Mira/releases`. If the repository is renamed or transferred, GitHub redirects this address and the downloads, which Mira follows; don’t then recreate a repository under the old name, which would take the redirect’s place. `./tools/update-check.ps1` checks the archive, the portable executable and the installer end to end, with a local feed and a test key; it then gives the Start menu shortcut back to the copy it pointed to.

The [architecture](docs/ARCHITECTURE.md) describes native mpv rendering, the Windows layers and progress reporting. The original code is under the [MIT license](LICENSE); third-party components keep their licenses.
