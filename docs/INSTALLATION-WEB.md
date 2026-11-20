# Mira’s extension on Jellyfin (and the end of Mira web)

**Mira web is over.** Up to 0.9.3, your Jellyfin server served Mira to phones, in Safari or Chrome, at `http://<server>:8096/Mira`. Mira now focuses on its apps: [Windows](INSTALLATION.md), [Mac](INSTALLATION-MAC.md) and [iPhone](INSTALLATION-IOS.md). On Android, there’s no Mira for now. A Mira web icon kept on a phone’s home screen now opens a page that says so and leads to <https://miracinema.app>: you can remove it.

The extension, on the other hand, stays on Jellyfin. It serves:

- **your party**: your link to watch together, its page (with the preview in Discord or Messages) and guests without an account, who go through its gateway;
- **Download to the server PC** from your iPhone, your Mac or another PC (its relay to Mira for Windows on Jellyfin’s PC);
- **automatic subtitles** and **Identify…** for titles that were recognized wrong;
- subtitles in time with a conversion, for the iPhone app.

## With Mira for Windows: everything in one click

On the PC where Jellyfin runs:

1. Open **Help** (the **?** button on the left, or F1).
2. The **Mira on your iPhone** card ticks what’s ready and numbers what’s left:
   - **Mira’s extension on Jellyfin**;
   - **Windows’ firewall**: it lets your iPhone in, on Jellyfin’s port only, from your Wi-Fi and Tailscale, never on a public network: if Windows counts your router’s Wi-Fi as public, **Make it private** changes that, with Windows’ permission (only at home: a café’s network must stay public);
   - **away from home (optional)**: Tailscale, free, on this PC; on the iPhone, the Tailscale built into Mira is enough;
   - **as soon as the PC turns on**: Jellyfin starts with Windows, before your session (a delayed start chosen in Windows is kept), and starts again after an error, three times at most before a day without errors (with its Windows service).
3. Click **Set everything up**. Windows asks for a single permission, in the name of **Windows PowerShell** (Microsoft), which sets up the firewall and the service; then Jellyfin restarts for a few seconds.
4. The card gives the address to type into Mira on the iPhone; **Install Mira on iPhone** opens [the installation guide](INSTALLATION-IOS.md).

**Close Jellyfin to the network**, under the steps, removes the “Mira - Jellyfin” rule from the firewall (one Windows permission); **Set everything up** puts it back.

You need to be signed in with Jellyfin’s administrator account, which is the case if Mira installed Jellyfin.

Before installing the extension, Mira checks on GitHub that it really is the one for its version, signed like its own updates, in a release that no one can change anymore; otherwise, it installs nothing and the guide says why. The repository it adds to Jellyfin only offers this checked version (and replaces a Mira repository added by hand). After that, the extension follows the version of Mira for Windows: when Mira updates, open it once with Jellyfin’s administrator account; it moves Jellyfin to the new version and restarts it as soon as no one is watching.

## Without Mira for Windows

From Jellyfin’s web page:

1. **☰ → Dashboard → Plugins → Repositories**, then **+**:
   - name: `Mira`;
   - URL: `https://github.com/sasou-web/Mira/releases/latest/download/jellyfin-manifest.json`.
2. **Catalog**: choose **Mira**, then **Install**.
3. Restart Jellyfin (**Dashboard → Restart**).

Jellyfin then updates the extension by itself with each new version of Mira. It only checks the hash given by the same GitHub release: it trusts GitHub and the account that publishes Mira. With a Windows PC, Mira for Windows (above) is safer.

**Away from home**: install Tailscale on the server. In Jellyfin, **Dashboard → Networking → LAN networks**, add `100.64.0.0/10` (along with, if the list was empty, `127.0.0.0/8`, `10.0.0.0/8`, `172.16.0.0/12` and `192.168.0.0/16`). On Windows, the firewall must let `jellyfin.exe` in on its port from `100.64.0.0/10`.

## If something goes wrong

- **Mira on the iPhone can’t find the server**: open **Help** in Mira for Windows: the **Mira on your iPhone** card says what’s missing (most often the firewall: **Set everything up**).
- **Your party or Download to the server PC says the extension is missing**: check in **Dashboard → Plugins → My Plugins** that **Mira** is there, “Active”, then restart Jellyfin.
- **The extension keeps the old version after an update**: first open the updated Mira for Windows, with Jellyfin’s administrator account. To get it right away: **Dashboard → Scheduled Tasks → Update Plugins** (▶), then **Dashboard → Restart**.
