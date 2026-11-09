---
title: Verify your download
short: Verify
description: Why Windows and macOS warn about Mira, and three ways to check that the file you got is the one Mira published.
platforms: Windows, Mac, iPhone
---

Mira's files are not signed by a paid certificate yet. Windows SmartScreen and macOS Gatekeeper warn about every app they do not know, signed or not: an unsigned file starts with no reputation at each new version. The warning says nothing about the file itself. Here is how to check it, in a minute.

## 1. Compare the SHA-256

Every file of a release has a `.sha256` next to it, published at the same moment, in a release that can no longer be changed (GitHub's immutable releases: once out, nobody can replace a file, not even the maintainer).

Windows, in PowerShell:

```powershell
Get-FileHash .\Mira-__VERSION__-win-x64-setup.exe -Algorithm SHA256
```

Mac, in Terminal:

```sh
shasum -a 256 ~/Downloads/Mira-__VERSION__-mac-arm64.dmg
```

The value must match the one in the `.sha256` file. The same goes for the iPhone `.ipa`.

## 2. Check where it was built

No file of a release is built on a PC: GitHub Actions builds each one from the release's commit, in the open ([the workflows](https://github.com/sasou-web/Mira/tree/main/.github/workflows)), and signs a build attestation for it. With [GitHub CLI](https://cli.github.com/):

```sh
gh attestation verify Mira-__VERSION__-win-x64-setup.exe -R sasou-web/Mira
gh release verify v__VERSION__ -R sasou-web/Mira
```

The first line names the workflow and the commit that produced your file; the second checks the release as a whole. Attestations begin with the version after 0.9.5.

## 3. Look it up on VirusTotal

The release workflow sends the Windows installer, the portable and the Mac disk image to [VirusTotal](https://www.virustotal.com/), where some seventy antivirus engines scan them. Open `https://www.virustotal.com/gui/file/` followed by the SHA-256 from step 1. If the file is unknown there, upload it yourself: it is public already.

> [!NOTE]
> A brand-new unsigned program sometimes gets one "machine learning" verdict (a name ending in `!ml`) from a single engine. That is a known false positive on new, unsigned files; look at the whole score, not one line.

## 4. Or let a package manager check for you

- **Mac**: `brew install --cask sasou-web/mira/mira` from [Mira's Homebrew tap](https://github.com/sasou-web/homebrew-mira): Homebrew checks the image's SHA-256 itself. See [Install on Mac](mac.md#with-homebrew).
- **Windows**: winget is being added; until then, the installer from the release.

## What Mira checks itself

Once installed, Mira updates itself: it only installs a version whose manifest is signed with Mira's own key and whose files have the announced size and SHA-256. See [Install on Windows](windows.md#updates) and [Install on Mac](mac.md#updates).

## The code

Mira is open source under the MIT licence: [github.com/sasou-web/Mira](https://github.com/sasou-web/Mira). What it sends and to whom: [Privacy](privacy.md). Found a problem? [Report it](privacy.md#report-a-problem).
