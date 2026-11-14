using static Mira.Core.Lang;

namespace Mira.Core;

/// <summary>The app whose Réglages → Confidentialité is drawn (the iPhone keeps its own copy, Settings/Privacy.swift).</summary>
public enum PrivacyApp { Windows, Mac }

/// <summary>
/// What a connection of Réglages → Confidentialité can be stopped by: a switch on the page (<see cref="Updates"/>,
/// <see cref="JellyfinReleases"/>), the section of Réglages where it is turned on and off, or nothing in Mira.
/// </summary>
public enum PrivacyControl
{
    /// <summary>Nothing to switch: what Mira is for, or only at the person's own click.</summary>
    None,
    /// <summary>PlayerSettings.AutoUpdate: a switch on the page.</summary>
    Updates,
    /// <summary>PlayerSettings.JellyfinReleaseCheck: a switch on the page (Windows, on the PC of Jellyfin).</summary>
    JellyfinReleases,
    /// <summary>« Tailscale intégré », in Compte & serveur: signing out is its own step.</summary>
    Tailscale,
    /// <summary>TorLink and qBittorrent, in Téléchargements.</summary>
    Downloads,
    /// <summary>Guest links and Tailscale Funnel, in Amis & invités.</summary>
    GuestLinks,
    /// <summary>Automatic subtitles and missing episodes, in Soin de la bibliothèque (Library care).</summary>
    Maintenance,
}

/// <summary>One connection Mira, or the Jellyfin it drives, opens: to whom, when, what goes, and how it stops.</summary>
/// <param name="Hosts">The names SECURITY.md gives this connection (a test keeps the page and the document together).</param>
public sealed record PrivacyConnection(string Key, string Title, string To, string When, string Sends, string Stop, PrivacyControl Control, bool FromServer, string[] Hosts);

/// <summary>
/// Réglages → Confidentialité (Windows and Mac): every connection that leaves this device, and those the Jellyfin
/// server opens for Mira's features, each with what it sends and its switch where there is one. No telemetry, no
/// analytics, no crash report: nothing else goes anywhere. The details are SECURITY.md's « What goes out on the network » (docs/SECURITY.fr.md in French).
/// </summary>
public static class Privacy
{
    public static string Document => L("https://github.com/sasou-web/Mira/blob/main/docs/SECURITY.fr.md#ce-qui-part-sur-le-réseau",
        "https://github.com/sasou-web/Mira/blob/main/SECURITY.md#what-goes-out-on-the-network");

    public static string Summary => L("Mira n’a ni compte, ni publicité, ni mesure d’audience, ni rapport de plantage envoyé tout seul. Voici tout ce qui part sur le réseau, et comment l’arrêter.",
        "Mira has no account, no ads, no analytics and no crash report sent on its own. Here is everything that goes out on the network, and how to stop it.");
    public static string FromDevice(PrivacyApp app) => app == PrivacyApp.Mac ? L("Depuis ce Mac", "From this Mac") : L("Depuis ce PC", "From this PC");
    public static string FromServer => L("Depuis ton serveur Jellyfin", "From your Jellyfin server");
    public static string ServerNote => L("Ces connexions partent du PC de Jellyfin, jamais de cet appareil.", "These connections leave from Jellyfin’s PC, never from this device.");
    public static string DocumentLabel => L("Tous les détails (SECURITY.md)", "Every detail (SECURITY.md)");

    /// <summary>The page's rows for <paramref name="app"/>, those of this device first, then those of the server.</summary>
    public static IReadOnlyList<PrivacyConnection> Connections(PrivacyApp app)
    {
        var windows = app == PrivacyApp.Windows;
        List<PrivacyConnection> list =
        [
            new("jellyfin", L("Ton serveur Jellyfin", "Your Jellyfin server"),
                L("L’adresse où tu t’es connecté, chez toi ou par Tailscale.", "The address you signed in to, at home or through Tailscale."),
                L("Tant que tu es connecté : bibliothèque, images, lecture.", "While you’re signed in: library, pictures, playback."),
                L("Ton jeton de session dans un en-tête (jamais dans une adresse), ce que tu regardes et où tu t’es arrêté. Les affiches viennent aussi de lui.",
                    "Your session token in a header (never in an address), what you watch and where you stopped. Posters come from it too."),
                L("Se déconnecter, dans Compte & serveur.", "Sign out, in Account & server."), PrivacyControl.None, false, ["Jellyfin"]),
            new("discovery", L("Trouver Jellyfin", "Finding Jellyfin"),
                L("Cet ordinateur, ton réseau local (UDP 7359) et tes appareils Tailscale en ligne (port 8096).", "This computer, your home network (UDP 7359) and your online Tailscale devices (port 8096)."),
                L("À l’écran de connexion, tant qu’aucune adresse n’est connue.", "On the sign-in screen, while no address is known."),
                L("La question de découverte de Jellyfin, rien qui t’identifie. Rien ne sort de chez toi ni de Tailscale.", "Jellyfin’s discovery question, nothing that identifies you. Nothing leaves your home or Tailscale."),
                L("Taper l’adresse de ton serveur.", "Type your server’s address."), PrivacyControl.None, false, ["7359"]),
            new("updates", L("Mises à jour de Mira", "Mira updates"),
                L("GitHub (api.github.com, github.com), où Mira est publiée.", "GitHub (api.github.com, github.com), where Mira is published."),
                windows ? L("20 s après l’ouverture, puis toutes les 6 h ; et pour vérifier l’extension Mira avant de l’installer.", "20 s after opening, then every 6 hours; and to check Mira’s extension before installing it.")
                    : L("À l’ouverture, puis toutes les 6 h ; et pour vérifier l’extension Mira avant de l’installer.", "At opening, then every 6 hours; and to check Mira’s extension before installing it."),
                L("La version de Mira et ton adresse IP, comme toute requête. Chaque version est vérifiée par sa signature avant d’être installée.",
                    "Mira’s version and your IP address, like any request. Each version is checked by its signature before it’s installed."),
                L("L’interrupteur ci-contre : Mira ne cherche plus, tu mets à jour à la main.", "The switch here: Mira stops looking, you update by hand."), PrivacyControl.Updates, false, ["api.github.com"]),
        ];
        if (windows)
            list.Add(new("jellyfin-releases", L("Nouvelles versions de Jellyfin", "New Jellyfin versions"),
                L("GitHub (catalogue winget de Microsoft).", "GitHub (Microsoft’s winget catalogue)."),
                L("Une fois par jour au plus, sur le PC de Jellyfin, avec un compte administrateur.", "Once a day at most, on Jellyfin’s PC, with an administrator account."),
                L("Rien d’autre que la requête : ni la version de ton Jellyfin, ni rien qui t’identifie.", "Nothing but the request: neither your Jellyfin’s version nor anything that identifies you."),
                L("L’interrupteur ci-contre : Mira ne te dira plus qu’une version est sortie.", "The switch here: Mira won’t tell you a version is out any more."), PrivacyControl.JellyfinReleases, false, ["winget-pkgs"]));
        list.Add(new("tailscale", L("Tailscale intégré", "Built-in Tailscale"),
            L("Le serveur de coordination de Tailscale et ses relais, ton routeur (UPnP, NAT-PMP, PCP : un port ouvert pour une connexion directe plutôt que par un relais), puis tes appareils Tailscale.",
                "Tailscale’s coordination server and its relays, your router (UPnP, NAT-PMP, PCP: a port opened for a direct connection rather than through a relay), then your Tailscale devices."),
            L("Seulement après « Se connecter à Tailscale », à chaque ouverture de Mira.", "Only after “Sign in to Tailscale”, each time Mira opens."),
            L("Comme l’app Tailscale : ton compte, le nom de l’appareil, son système, ses adresses IP et sa clé publique. Les vidéos restent chiffrées de bout en bout, aucun journal n’est envoyé.",
                "Like the Tailscale app: your account, the device’s name, its system, its IP addresses and its public key. Videos stay end-to-end encrypted, no log is sent."),
            L("Se déconnecter de Tailscale, dans Compte & serveur.", "Sign out of Tailscale, in Account & server."), PrivacyControl.Tailscale, false, ["DERP"]));
        list.Add(new("trailers", L("Bandes-annonces du web", "Trailers from the web"),
            L("Le site de la bande-annonce (souvent YouTube), dans ton navigateur, jamais dans Mira.", "The trailer’s site (often YouTube), in your browser, never in Mira."),
            L("Seulement quand tu cliques sur « Bande-annonce » d’un titre qui n’en a pas sur ton serveur.", "Only when you click “Trailer” on a title that has none on your server."),
            L("Ce que ton navigateur envoie à ce site (ton adresse IP, ses cookies). Mira ne lui envoie rien et n’en charge rien avant ton clic. Une bande-annonce gardée sur ton serveur ne passe que par lui.",
                "What your browser sends that site (your IP address, its cookies). Mira sends it nothing and loads nothing from it before your click. A trailer kept on your server goes through it alone."),
            L("Rien à couper : rien ne part sans ton clic.", "Nothing to turn off: nothing goes without your click."), PrivacyControl.None, false, ["YouTube"]));
        if (windows)
        {
            list.Add(new("installs", L("Ce que tu installes", "What you install"),
                L("SourceForge (moteur mpv, qBittorrent), nodejs.org et registry.npmjs.org (TorLink), repo.jellyfin.org (Jellyfin).",
                    "SourceForge (mpv engine, qBittorrent), nodejs.org and registry.npmjs.org (TorLink), repo.jellyfin.org (Jellyfin)."),
                L("Seulement quand tu installes ou mets à jour l’un d’eux.", "Only when you install or update one of them."),
                L("Ton adresse IP. Chaque fichier est vérifié par son empreinte.", "Your IP address. Each file is checked by its fingerprint."),
                L("Rien à couper : rien ne part sans ton clic.", "Nothing to turn off: nothing goes without your click."), PrivacyControl.None, false, ["downloads.sourceforge.net", "registry.npmjs.org", "repo.jellyfin.org"]));
            list.Add(new("torrents", L("TorLink et qBittorrent", "TorLink and qBittorrent"),
                L("Les pairs BitTorrent, les trackers, la DHT et ton routeur (UPnP).", "BitTorrent peers, trackers, the DHT and your router (UPnP)."),
                L("Tant qu’ils tournent, une fois activés.", "While they run, once turned on."),
                L("Ce qu’envoie tout client BitTorrent : ton adresse IP et ce que tu télécharges ou partages.", "What any BitTorrent client sends: your IP address and what you download or share."),
                L("Couper TorLink dans Téléchargements.", "Turn TorLink off in Downloads."), PrivacyControl.Downloads, false, ["BitTorrent"]));
            list.Add(new("funnel", L("Liens d’invités (Tailscale Funnel)", "Guest links (Tailscale Funnel)"),
                L("Les relais de Tailscale, puis Internet.", "Tailscale’s relays, then the internet."),
                L("Tant que Funnel est ouvert sur le PC de Jellyfin.", "While Funnel is open on Jellyfin’s PC."),
                L("Le trafic de tes invités vers /Mira, chiffré jusqu’à ton PC. Le nom de ce PC dans Tailscale devient public.", "Your guests’ traffic to /Mira, encrypted up to your PC. This PC’s name in Tailscale becomes public."),
                L("Retirer les liens, dans Amis & invités.", "Remove the links, in Friends & guests."), PrivacyControl.GuestLinks, false, ["Funnel"]));
        }

        // —— From the Jellyfin server: Mira's features that make it ask a service ——
        list.Add(new("metadata", L("Affiches et résumés", "Posters and descriptions"),
            L("TMDb et les autres sources de métadonnées de Jellyfin.", "TMDb and Jellyfin’s other metadata sources."),
            L("Quand Jellyfin analyse ta bibliothèque, et quand tu corriges un titre.", "When Jellyfin scans your library, and when you fix a title."),
            L("Les noms et les années de tes titres, depuis ton serveur.", "Your titles’ names and years, from your server."),
            L("Dans le tableau de bord de Jellyfin, Bibliothèques.", "In Jellyfin’s dashboard, Libraries."), PrivacyControl.None, true, ["TMDb"]));
        list.Add(new("subtitles", L("Sous-titres automatiques", "Automatic subtitles"),
            L("OpenSubtitles et SubDL, par l’extension Mira de Jellyfin.", "OpenSubtitles and SubDL, through Jellyfin’s Mira extension."),
            L("Seulement une fois activés, quand un titre arrive ou quand tu cherches.", "Only once turned on, when a title arrives or when you search."),
            L("L’empreinte et la taille du fichier (OpenSubtitles), ses identifiants IMDb et TMDb, la saison, l’épisode et la langue. Jamais le nom du fichier.",
                "The file’s fingerprint and size (OpenSubtitles), its IMDb and TMDb ids, season, episode and language. Never the file name."),
            L("Les couper, dans Soin de la bibliothèque.", "Turn them off, in Library care."), PrivacyControl.Maintenance, true, ["OpenSubtitles", "SubDL"]));
        list.Add(new("tvdb", L("Épisodes manquants (TheTVDB)", "Missing episodes (TheTVDB)"),
            L("TheTVDB, par son extension officielle de Jellyfin.", "TheTVDB, through its official Jellyfin extension."),
            L("Seulement si tu l’as installée, à chaque analyse de la bibliothèque.", "Only if you installed it, at each library scan."),
            L("L’identifiant TheTVDB de chaque série, pour sa liste d’épisodes. Ni nom, ni image de TheTVDB ne sont gardés.", "Each series’ TheTVDB id, for its list of episodes. No name or picture from TheTVDB is kept."),
            windows ? L("La désinstaller, dans Soin de la bibliothèque.", "Uninstall it, in Library care.") : L("La désinstaller depuis Mira pour Windows ou le tableau de bord de Jellyfin.", "Uninstall it from Mira for Windows or Jellyfin’s dashboard."),
            windows ? PrivacyControl.Maintenance : PrivacyControl.None, true, ["TheTVDB"]));
        list.Add(new("torrent-search", L("Recherche de téléchargements", "Download searches"),
            L("Les sites de TorLink (The Pirate Bay par apibay.org, YTS, Nyaa, SubsPlease, EZTV, 1337x…), plus Knaben, AnimeTosho, Torrents-CSV et AniList (les autres noms d’un anime) sauf si tu les coupes.",
                "TorLink’s sites (The Pirate Bay through apibay.org, YTS, Nyaa, SubsPlease, EZTV, 1337x…), plus Knaben, AnimeTosho, Torrents-CSV and AniList (an anime’s other names) unless you turn them off."),
            L("Quand un administrateur cherche dans Téléchargements, quand Mira regarde une série suivie et quand tu demandes des épisodes manquants.",
                "When an administrator searches in Downloads, when Mira checks a followed series and when you get missing episodes."),
            L("Ce que tu cherches, tel quel et sans accents ni apostrophe (à AniList, le nom de l’anime seul), et l’adresse IP du PC de Jellyfin. Ni ton compte, ni ta bibliothèque.",
                "What you search for, as typed and without accents or apostrophes (to AniList, the anime’s name alone), and the Jellyfin PC’s IP address. Neither your account nor your library."),
            windows ? L("Couper « Plus de sources de recherche » dans les réglages de TorLink, ou TorLink lui-même.", "Turn off “More search sources” in TorLink’s settings, or TorLink itself.")
                : L("Dans Mira pour Windows sur le PC de Jellyfin : réglages de TorLink.", "In Mira for Windows on the Jellyfin PC: TorLink’s settings."),
            windows ? PrivacyControl.Downloads : PrivacyControl.None, true, ["apibay.org", "api.knaben.org", "feed.animetosho.net", "torrents-csv.com", "graphql.anilist.co"]));
        list.Add(new("extension", L("Mises à jour de l’extension Mira", "Mira extension updates"),
            L("GitHub, où Mira est publiée.", "GitHub, where Mira is published."),
            L("Quand Jellyfin cherche les mises à jour de ses extensions.", "When Jellyfin looks for updates to its extensions."),
            L("Ce qu’envoie Jellyfin : son adresse IP et son nom de programme.", "What Jellyfin sends: its IP address and its program name."),
            L("Dans le tableau de bord de Jellyfin, Extensions, Dépôts.", "In Jellyfin’s dashboard, Plugins, Repositories."), PrivacyControl.None, true, ["jellyfin-manifest.json"]));
        return list;
    }
}
