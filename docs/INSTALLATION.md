# Install Mira on Windows

## Requirements

- Windows 10 version 2004 or later, or Windows 11, **x64**. Native rounded corners need Windows 11.
- A Jellyfin server you can reach, on the same PC or on the network, and an account on that server. With no server, Mira can [install Jellyfin on this PC](#install-jellyfin-from-mira).
- The **libmpv x64** video engine. Mira installs it when needed (31 MB) and keeps it up to date; an engine already on the PC is reused as is. `mpv.exe` alone isn’t enough.

The installer, the portable executable and the archive include the .NET runtime: you don’t need to install the SDK or .NET. The interface is in English and French. Mira is a development pre-release.

Optional: [TorLink](#torlink), which Mira installs when you turn it on, and the Microsoft Edge WebView2 runtime (already on Windows 11 and most Windows 10 PCs) to show its terminal inside Mira.

## Installation

The [latest release](https://github.com/sasou-web/Mira/releases) offers three files, where `<version>` is its number; you only need one.

- **`Mira-<version>-win-x64-setup.exe`, recommended.** The installer puts Mira in `%LOCALAPPDATA%\Programs\Mira`, for your account only and without administrator rights. It adds the Start menu shortcut, a desktop shortcut if you tick it, and an uninstall entry in **Settings → Apps**.
- **`Mira-<version>-win-x64-portable.exe`.** Mira in a single file, with nothing to install. Put it in a folder of its own that your account can write to, for example `Documents\Mira`: it creates its `data` folder there. On first launch, .NET extracts a few native libraries to `%TEMP%\.net`.
- **`Mira-<version>-win-x64.zip`.** The app’s full folder. Extract the **whole** `Mira` folder to a place you can write to; don’t run the executable from inside the archive, and don’t copy `Mira.exe` alone.

As long as the files aren’t signed (code signing policy in the repository’s README), Windows SmartScreen may show “Windows protected your PC”. Check the file’s hash (further down), then choose **More info → Run anyway**.

1. Open Mira. A Mira shortcut is added to the Start menu the first time the main profile starts, or right at installation.
2. Enter your username and password. Mira looks for Jellyfin and fills in its address: on this PC, on your home network, or on your Tailscale devices (a line under the address says where it found it, and other servers it found are offered next to it). If it finds nothing, type the address: the PC’s IP address or name is enough (`192.168.1.20`, `nas:8096`), as is the Tailscale address (`100.x.y.z`) or the address copied from Jellyfin’s web page. A Jellyfin on a Windows PC is only found on the home network once **Set everything up** has been run again on that PC with a version of Mira later than 0.8.0 (before that, only through Tailscale). Jellyfin 10.9 or later is required.
3. The video engine: with the installer, leave **Download the mpv video engine** ticked. Otherwise, **Settings → Playback → Install the mpv engine**, also offered the first time you play something.

Without a Jellyfin server, **No server yet: Mira installs Jellyfin on this PC** installs it and sets it up (next section).

## Install Jellyfin from Mira

Under the sign-in form, choose **No server yet: Mira installs Jellyfin on this PC**, then:

1. **Videos folder**: `Videos\Jellyfin` by default, or another one with **Browse** (a storage drive, for example). Mira creates `Movies`, `Shows` and `Anime` in it.
2. **Username** and **Password**: they become Jellyfin’s administrator account.
3. **Install**. Mira downloads the official Jellyfin 12.1 installer from `repo.jellyfin.org` and checks its SHA-256 hash, the one in Jellyfin’s winget manifest, then checks it again while keeping the file open until it starts: a different file is never run. Windows then asks for permission: Jellyfin installs for the whole PC, as a service that starts with Windows.

Mira then fills in Jellyfin’s first-run wizard:

- interface and metadata in Mira’s language;
- your administrator account;
- three libraries: **Movies** (movies type), **Shows** and **Anime** (shows type);
- access from the Internet turned off; devices on the local network can connect.

Then it signs in. All that’s left is to put your videos in these folders, under the names Jellyfin expects (`Title (Year)\Title (Year).mkv`, `Show\Season 01\Show - S01E02.mkv`), or to let TorLink do it: it finds the three libraries with nothing to set.

Jellyfin’s service runs under the Windows **Network Service** account, which can’t read personal folders. Mira gives it the right to read, but not change, `Movies`, `Shows` and `Anime` and everything added to them, not the rest of the folder you chose. It may also create its own files there (each title’s NFO and images, so that a reinstalled Jellyfin finds everything again) and change those only, never a video. A video copied or moved with File Explorer gets this right. A file sorted by TorLink, by hard link or by a move, gets it too. The chosen folder can’t be a whole drive, your personal folder (or another account’s), Documents, the Desktop or OneDrive itself. To take this right away (Jellyfin then no longer sees these videos), in a terminal: `icacls "<folder>\Movies" /remove:g *S-1-5-20 /t`, and the same for `Shows` and `Anime`. Up to 0.7.4, Mira gave it to the whole chosen folder: `icacls "<folder>" /remove:g *S-1-5-20 /t` removes it, then `icacls "<folder>\Movies" /grant *S-1-5-20:(OI)(CI)RX` (the same for `Shows` and `Anime`) gives it back to the three folders only.

If Jellyfin is already installed and set up on this PC, Mira says so and offers to sign in with its account. An interrupted installation starts again with the same account: steps already done are kept. Jellyfin is uninstalled from **Settings → Apps → Jellyfin Server**; its data is in `%ProgramData%\Jellyfin\Server`. Its other settings, including access from the Internet, are on its web page, `http://127.0.0.1:8096`.

## Back up and update Jellyfin

On Jellyfin’s PC, signed in with an administrator account, **Settings → Jellyfin server → Jellyfin on this PC** shows its version and its latest backup (Jellyfin 10.11 or later).

- **Back up now** asks Jellyfin for its built-in backup: its accounts, everyone’s progress, the libraries, their images, extracted subtitles and its settings, not your videos or its plugins. It goes in `%ProgramData%\Jellyfin\Server\data\backups` (Jellyfin needs 5 GB free on that drive). **Also copy to…** picks a folder on a USB stick or another drive: each backup is copied there, and Mira keeps the three most recent. It contains your devices’ sign-in tokens: don’t copy it to a shared folder.
- After 30 days without a backup, Mira reminds you with a notice (**Back up**), then every week.
- When a newer version of Jellyfin is out (read once a day from Microsoft’s winget catalog, a few days after its release), Mira says so once with a notice. **Update Jellyfin** first backs Jellyfin up, downloads its official installer, checks its hash, then Windows asks for permission; Jellyfin stops for about a minute. Refused while someone is watching. A new major version (13 after 12) is announced as such, with its **What’s new**: read it first, a major version may no longer load Mira’s extension. If the installer shows “Could not start the Jellyfin Server service”, choose **Ignore**: Jellyfin starts right after.

Mira only updates a Jellyfin installed as a service by its own installer (the one Mira installs). A new version changes Jellyfin’s database with no way back: going back to the old version needs the backup.

**Restore a backup** (new PC, replaced drive, back to the old version):

1. Install Jellyfin, the same version as the backup or newer (from Mira or `jellyfin.org`), and finish its wizard with any account.
2. Copy the `jellyfin-backup-<date>.zip` archive to `C:\ProgramData\Jellyfin\Server\data\backups`.
3. Open `http://127.0.0.1:8096`, **Dashboard → Backups**, and restore it: Jellyfin restarts with its old accounts, your progress and your libraries.
4. Put your videos back at the same paths (or fix the libraries’ folders in the dashboard), then, in Mira, **Help → Mira on your iPhone → Set everything up**.

## Get the video engine

**Automatically.** The installer’s **Download the mpv video engine** box (ticked by default), the **Install the mpv engine** button in **Settings → Playback**, or the **Install** button offered the first time you play something all do the same thing: Mira downloads a specific build from the [Windows builds of mpv](https://mpv.io/installation/) (shinchiro, published on SourceForge, 31 MB), checks its SHA-256 hash, extracts `libmpv-2.dll` and checks that one’s hash too, then puts it in `data\mpv`. This engine follows the build Mira pins: a newer build, pinned by a new version of Mira, is downloaded in the background, checked the same way and used at the next start; an engine chosen in the settings or found on the PC is never touched. A file other than the expected one is never installed. With the installer, a failed download (no connection) doesn’t stop the installation: Mira offers it again the first time you play something.

**With an engine already on the PC.** If mpv.net or Jellyfin MPV Shim is installed in a place Mira knows, its library is reused and nothing is downloaded. Another x64 `libmpv-2.dll` or `mpv-2.dll` can be chosen in **Settings → Playback → Engine location**; it comes before Mira’s.

The engine isn’t included in Mira’s files: it’s downloaded from its distributor, and its licenses (GPL for mpv and FFmpeg) remain its own. `data\mpv\SOURCE.txt` gives its origin and its hashes.

## Usage

The Jellyfin server keeps running: it handles the library, posters, metadata and progress. Mira provides the interface and playback. A local file is played directly if the server is local and the file can be reached; otherwise Mira uses the server’s original stream. When your connection needs it, Jellyfin converts the video (**Settings → Playback → Quality**).

| Action | Shortcut |
| --- | --- |
| Search | Ctrl + K / Ctrl + F |
| Home, Movies, Shows & anime, Whole library, Downloads | Ctrl + 1 to Ctrl + 5 |
| Move from a poster to the next one | Arrow keys, on a row or a grid of posters |
| Play / pause | Space / K |
| Back / forward 10 seconds | ← / →, J / L |
| Back / forward one minute | Shift + ← / Shift + → |
| Go to 0, 10 … 90% of the title | 0 to 9 (top row or numeric keypad) |
| Subtitles: off / back on | C |
| Volume (up to 200%) | ↑ / ↓, mouse wheel on the picture or on the speaker |
| Mute | M |
| Full screen | F / F11 / double-click on the video |
| Mini player / enlarge | I |
| Available skip: intro, credits, next | S |
| Next episode | N |
| Back / close a panel / leave full screen | Esc |

The notification area lets you hide Mira and bring it back without stopping playback. The window’s × button quits the app. Taskbar controls and Windows’ media overlay appear during playback; how they look depends on Windows.

## Away from home, without the Tailscale app

On a PC where the Tailscale app isn’t installed (a laptop you take with you, a relative’s PC), **Built-in Tailscale** brings Mira into your Tailscale network, as one more device, to reach your Jellyfin away from home. Jellyfin’s PC keeps the real Tailscale app: on it, as on any PC where the Tailscale app is connected, Mira doesn’t use it.

1. On the sign-in screen, type the Tailscale address of Jellyfin’s PC (`100.x.y.z`, or its name in Tailscale). Mira offers **Built-in Tailscale**; it also offers it when it finds no server. As long as it isn’t connected, **Sign in** to Jellyfin is refused, with the reason.
2. **Sign in to Tailscale**: Tailscale’s sign-in page opens in your browser. Sign in with the Tailscale account of Jellyfin’s PC. If your Tailscale network makes you approve each new device, approve `mira-<PC name>` in its console (**Open Tailscale’s admin console**).
3. If Windows asks you to allow “Mira (Tailscale)” on its networks, accept: a direct connection is faster. Refusing works too, through a Tailscale relay.
4. Sign in to Jellyfin as usual.

After that, Mira joins your Tailscale network each time it opens, before asking Jellyfin anything, and leaves it when it closes (even when ended from Task Manager). **Settings → Away from home** shows its state, which way Mira goes (**At home (direct)**, **Through the Tailscale app** or **Through Built-in Tailscale**) and what Tailscale sees of it. At home, Mira reaches Jellyfin directly.

- **Sign out of Tailscale** (same place) tells Tailscale, then erases Built-in Tailscale’s key and state from this PC. The device stays in the [Tailscale console](https://login.tailscale.com/admin/machines), “Logged out”, until you delete it there.
- Its connection expires like any Tailscale device’s (180 days by default): Mira warns you two weeks before; in the console, **Disable key expiry** (the device’s `⋯` menu) prevents it.
- Optional: during an outage of Tailscale’s coordination server (like on the evening of 30 October 2026), Built-in Tailscale can start again from the device list kept on the device. While Tailscale is trialling this feature, it only grants it if the network’s policy says so (“During the trial phase, client-side netmap caching is opt-in”); Mira already asks Tailscale to turn it on by default at the end of the trial (protocol version 149), but that isn’t enough yet. Under **Built-in Tailscale** (Settings, on Windows, Mac and iPhone), Mira offers it with **Copy the line**, **Open the network’s policy** and **Hide**; the notice goes away by itself once the setting is saved. In your [network’s policy](https://login.tailscale.com/admin/acls/file) (JSON editor), paste the line right under `"nodeAttrs": [` (there’s often one already, Funnel’s: a second `"nodeAttrs"` is refused, “duplicate name”); if there isn’t one, write it right after the first `{`:

  ```json
  "nodeAttrs": [
    { "target": ["autogroup:member", "autogroup:tagged"], "attr": ["cache-network-maps"] },
  ],
  ```

  A device that has already connected once then starts again from the list kept on it and reaches your Jellyfin despite the outage; a very first connection waits for Tailscale to come back. The list (device names, addresses and public keys) stays in Built-in Tailscale’s folder, on the device. Details: [Client-side netmap caching](https://tailscale.com/docs/reference/client-side-netmap-caching).
- Privacy: nothing goes to Tailscale before **Sign in to Tailscale**. After that, as with the Tailscale app, its servers see the chosen account, the device’s name, its system and its IP addresses (public and local, for the direct connection); your videos stay encrypted end to end, directly or through a Tailscale relay. Mira sends no log to Tailscale; its own, `data\tailscale\mira-tailscale.log`, keeps no sign-in page. Details in [SECURITY.md](../SECURITY.md#built-in-tailscale-windows).

## TorLink

TorLink is a terminal download client ([independent project](https://github.com/baairon/torlink), MIT license), published on npm (`npx torlnk`). On Jellyfin’s PC, the rail’s **Downloads** page uses it with the Download to the server PC screen: search for a title, Mira puts forward the best version of each movie or episode, **Download** hands it to TorLink, and the right-hand column shows progress, speed and time left (**Pause**, **Resume**, **Cancel**), then what is heading to Jellyfin (**Folder**, **Retry**, **File as**). The **Terminal** button shows TorLink’s own interface (and **Mira’s screen** goes back); Mira remembers your choice.

1. Open **Downloads** (arrow on the rail), then turn on **TorLink** in the list of downloaders.
2. If TorLink is already on this PC (global npm install, shortcut), Mira shows you its folder and version, and only runs it once you’ve confirmed it’s yours; it remembers that afterwards. Otherwise, it installs it for this Windows account, without administrator rights and without changing the PATH:
   - **Node.js 24.21.0**: the official zip from nodejs.org (38 MB), checked by its size and SHA-256, and put in `data\node`;
   - **TorLink 1.9.0**, installed by that npm in `data\torlink`, with each of its dependencies at the version pinned by Mira and checked by its hash.
   Allow about a minute.
3. In **Settings → Downloads**, check the Movies, Shows and Anime folders. Left empty, they’re read from Jellyfin: that takes an administrator account and a server on this PC.

Turning TorLink off closes it; it stays installed. The TorLink installed by Mira follows Mira: when a new version of Mira pins a newer Node.js or TorLink, it’s prepared alongside while TorLink runs, then used the next time it opens. It runs without its optional WebRTC module, which needs build tools: TCP, uTP and DHT peers are enough.

Each finished download is **moved** into the library, under the names Jellyfin expects, for example `Title (Year)\Title (Year).mkv` or `Show\Season 01\Show - S01E02.mkv`. It never exists twice:
- on the same drive, the move is instant: the file only changes folder;
- on another drive, each file is copied then deleted right away from TorLink’s folder. To avoid this, Mira has TorLink download onto the library’s drive, in “TorLink downloads”, when TorLink doesn’t have a folder yet. Otherwise, choose that folder in TorLink’s terminal with the **o** key.

TorLink stops sharing a download once it’s moved. To keep it sharing, turn on **Settings → Downloads → Keep sharing in TorLink**: on the same drive, a hard link also puts the file in the library, without taking more space. It then shows in both folders. Jellyfin is told, then the title shows up in Mira.

Quitting Mira closes TorLink too; its downloads resume the next time it opens. TorLink can’t run in Mira and in another window at the same time. With Jellyfin in Docker or on another PC, choose the local folders the server shares: new titles then show up at its next library scan.

Mira doesn’t check what is downloaded. The sources built into TorLink mostly index copies whose distribution isn’t authorized: only download content you have the right to get.

## Updates and data

The `data` folder, next to `Mira.exe`, holds the protected session, preferences, pending progress, the cache and the log of TorLink sorting. Never publish it or attach it to an issue. The protected session can’t be carried over to another Windows account.

**Automatic updates, from 0.5.2 on.** A few seconds after it opens, then every 6 hours, Mira looks for a new version on GitHub. It downloads in the background the file for its install type (installer, portable executable or archive), checks that it’s signed by Mira, with the announced size and SHA-256 hash, and installs it when you close Mira. **Restart**, in the message that appears or in **Settings → Updates**, installs it right away and reopens Mira. The `data` folder stays where it is; pre-releases are offered, like every version published so far. The switch in **Settings → Updates** stops the automatic check; **Check for updates** stays available. The details of each check are written to `data\updates\update.log`.

A copy at 0.5.1 or older doesn’t update by itself: installing 0.5.2 or a later version by hand once is enough. To update by hand:

- **Installer**: run the new version’s installer; it closes Mira if it’s open, replaces the app and keeps `data`. Uninstalling asks before deleting this data (no by default), giving its size, then says what stays on the PC and how to remove it: the “Mira - Jellyfin” firewall rule (also removed by **Help → Mira on your iPhone → Close Jellyfin to the network**), Jellyfin and its service setting, Jellyfin’s read access to your folders, TorLink’s files, and the libraries extracted by the portable version in `%TEMP%\.net`. The portable Mira itself erases those of its earlier versions at the next start, when no other Mira is open.
- **Portable executable**: close Mira and replace the `.exe` file, in the same folder as its `data`.
- **Archive**: close Mira, back up `data`, then replace the app’s files with those from the new archive, **keeping `data`**.

Each file in the release comes with its SHA-256 hash (a `.sha256` file). To check a downloaded file:

```powershell
Get-FileHash .\Mira-<version>-win-x64-setup.exe -Algorithm SHA256
```

To move from a portable copy to the installer and keep its session: install, close every Mira window, then copy the old copy’s `data` folder into `%LOCALAPPDATA%\Programs\Mira`, replacing the one there.

## If something doesn’t work

| Symptom | What to check |
| --- | --- |
| Can’t reach Jellyfin away from home | The address in the guide (192.168…) only works on your home network. Install Tailscale on this PC and on the device, with the same account, then **Help** (**?** on the left) **→ Mira on your iPhone → Set everything up**, and type the Tailscale address it gives into Mira (or into the Jellyfin app). On iPhone, Mira’s Built-in Tailscale saves you the Tailscale app. Opening your router (port forwarding) would expose Jellyfin to the whole Internet: not recommended. |
| Can’t reach Jellyfin from a PC without the Tailscale app | **Built-in Tailscale** (section [Away from home, without the Tailscale app](#away-from-home-without-the-tailscale-app)): on the sign-in screen, with the Tailscale address of Jellyfin’s PC, or **Settings → Away from home → Sign in to Tailscale**. |
| “… isn’t connected to Tailscale: it’s off, asleep or without Internet” | Jellyfin’s PC has left Tailscale: turn it on or wake it (next rows: sleep, session, expired connection). Mira reloads Home as soon as it’s back. |
| “Waiting for approval in Tailscale’s admin console” | Your Tailscale network makes you approve each new device: **Open Tailscale’s admin console**, then approve `mira-<PC name>`. |
| “Windows Security blocks mira-tailscale.exe” or “Built-in Tailscale stops as soon as it starts” | An antivirus is blocking Mira’s Built-in Tailscale (**Windows Security → Protection history**). The Tailscale app does the same thing: install it on this PC ([tailscale.com/download](https://tailscale.com/download)); Mira then goes through it. |
| Can’t reach it away from home after the server PC restarts, until you sign in to Windows | Tailscale is waiting for your session: **Set everything up** on the “Mira on your iPhone” card in Help fixes it, or right-click Tailscale’s icon → **Preferences** → **Run unattended**. |
| Can’t reach it after the server PC has been idle for a while | It went to sleep: Tailscale can’t wake it. **Set everything up** on the “Mira on your iPhone” card in Help keeps it awake when plugged in, or Settings → System → Power, sleep when plugged in: **Never** (for a laptop, closing the lid: **Do nothing** when plugged in). While something plays on another device, Mira keeps the PC awake as long as Mira is open. |
| Can’t reach it away from home, Tailscale asks you to sign in again on the server PC | Its Tailscale connection expired (every 180 days by default): sign in again from Tailscale’s menu, then on https://login.tailscale.com/admin/machines, this PC’s `⋯` menu → **Disable key expiry**. Mira warns you two weeks before. |
| “Jellyfin is stopped on this PC” | **Start Jellyfin**, in the notice or on the “Mira on your iPhone” card in Help. For its service, Windows asks for permission. If it stops again after two starts, Mira shows the cause written in its log (`%ProgramData%\Jellyfin\Server\log`) and **Open its log**: fix the cause (port 8096 taken, full drive, unreadable data folder), then start it. |
| Server can’t be reached | Jellyfin must be running; check the address, the port and network access. For a remote server, use HTTPS. |
| “This address answers, but it isn’t a Jellyfin server” | The address answers, but it isn’t Jellyfin (router, NAS, Emby…): check the port, 8096 by default. |
| “Mira needs Jellyfin 10.9 or later” | Update Jellyfin; Mira uses routes introduced in that version. |
| “Could not start the Jellyfin Server service” window from Jellyfin’s installer | Choose **Ignore** (not Retry, which opens it again). This message can appear while the service is starting fine: it was seen on a real PC, where Jellyfin was running right after. Mira puts this window in front of itself and doesn’t wait for your answer: as soon as Jellyfin answers, it carries on with the setup. If it doesn’t answer, Mira shows the cause written in Jellyfin’s log (`%ProgramData%\Jellyfin\Server\log`), for example port 8096 already taken. |
| “Jellyfin is installed but doesn’t start” or “… already installed on this PC but isn’t answering” | Read the cause given, fix it, then start **Jellyfin Server** (Start menu, or Windows **Services**) or restart the PC. **Install** then picks up where it stopped, without reinstalling. |
| “Another program is already answering on port 8096” | It isn’t Jellyfin: close that program, or install Jellyfin on another PC. |
| “This Jellyfin already has an administrator account” | Its setup was started with another account: finish it at `http://127.0.0.1:8096`, then sign in. |
| Video sorted but missing from Jellyfin | Check that it’s in `Movies`, `Shows` or `Anime`. A file moved by another program can keep the permissions of its old folder: copy it with File Explorer instead. |
| “This server’s HTTPS certificate isn’t recognized” | The server’s certificate isn’t trusted by Windows, and Mira doesn’t try `http://` instead: for a server at your home, type its full address with `http://` (the one Mira suggests, for example `http://nas:8096`); otherwise, install a valid certificate. |
| “This address goes over the Internet with http://” | The password would cross the Internet in clear text: use the server’s `https://` address, or its Tailscale address. **Sign in anyway** sends it regardless. |
| Library visible but nothing plays | **Settings → Playback**: install the mpv engine, or choose a **64-bit** libmpv DLL with its dependencies. |
| “The video engine couldn’t be downloaded” | Check the Internet connection, then try again from **Settings → Playback**. The download goes through `downloads.sourceforge.net` and its mirrors. |
| Progress slow to reach the server | Look in **Settings → Account**; uploads wait when the server is unavailable. |
| Notification icon missing | Look in the hidden icons near the clock. |
| Mira is already open | Opening the shortcut again should bring back the existing window. |
| TorLink not found | On the **Downloads** page, choose **Reinstall**. |
| “TorLink couldn’t install” | Check the Internet connection (nodejs.org and registry.npmjs.org), then turn TorLink back on. |
| “TorLink is already open” | Quit the other TorLink window (q or Ctrl + C), then choose **Retry**. |
| Built-in terminal unavailable | Install the Microsoft Edge WebView2 runtime. Meanwhile, **Open in a window** runs TorLink separately; its finished downloads are still sorted, at the latest the next time Mira opens. |
| Download not sorted | Read its state under **To Jellyfin**, on the Downloads page. Choose the missing folders in the settings, then **Retry**. |
| Update that won’t install | **Settings → Updates** gives the last error, `data\updates\update.log` the details. Mira’s folder must stay writable; a copy run from a development folder (`bin\Release`) never updates. |

For a problem you can reproduce, [open an issue](https://github.com/sasou-web/Mira/issues/new/choose) with the versions of Mira, Windows, Jellyfin and the engine. Don’t attach any credentials or personal media.
