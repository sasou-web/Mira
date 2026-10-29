namespace Mira.Core.Tailscale;

/// <summary>
/// The words of « Tailscale intégré », the same in Mira for Windows and Mac as in the iPhone app, and what Mira says of
/// the node's states and of a server it does not reach through it.
/// </summary>
public static class TailnetText
{
    public static string Name => L("Tailscale intégré", "Built-in Tailscale");
    public static string SignIn => L("Se connecter à Tailscale", "Sign in to Tailscale");
    public static string SignOut => L("Se déconnecter de Tailscale", "Sign out of Tailscale");
    public static string WaitingApproval => L("En attente d’approbation dans la console de Tailscale", "Waiting for approval in Tailscale’s admin console");
    /// <summary>Said before the first sign-in and beside the setting: what Tailscale learns, and what it never gets.</summary>
    public static string Privacy => L("Les serveurs de Tailscale voient cet appareil rejoindre ton réseau Tailscale : son nom, son système et ses adresses IP (publique et locales, pour la connexion directe). Tes vidéos restent chiffrées de bout en bout, en direct ou par un relais de Tailscale. Mira n’envoie aucun journal à Tailscale.",
        "Tailscale’s servers see this device join your Tailscale network: its name, its system and its IP addresses (public and local, for direct connections). Your videos stay end-to-end encrypted, directly or through a Tailscale relay. Mira sends no logs to Tailscale.");
    /// <summary>What it is for, said where it is offered.</summary>
    public static string Purpose => L("Pour joindre ton Jellyfin loin de chez toi sans installer l’app Tailscale : Mira rejoint ton réseau Tailscale lui-même, comme un appareil de plus, seulement quand il est ouvert.",
        "To reach your Jellyfin away from home without installing the Tailscale app: Mira joins your Tailscale network itself, as one more device, only while it’s open.");
    /// <summary>The login screen, for a server on Tailscale that this computer does not reach without it.</summary>
    public static string ServerOnTailnet => L("Ce serveur est sur Tailscale. Mira peut le joindre lui-même, sans l’app Tailscale.",
        "This server is on Tailscale. Mira can reach it itself, without the Tailscale app.");
    /// <summary>The login screen when nothing was found: Jellyfin may be on the person's Tailscale.</summary>
    public static string AwayFromHome => L("Pas chez toi ? Mira peut rejoindre ton réseau Tailscale lui-même et y trouver ton Jellyfin, sans l’app Tailscale.",
        "Not at home? Mira can join your Tailscale network itself and find your Jellyfin there, without the Tailscale app.");
    public static string Console => L("Ouvrir la console de Tailscale", "Open Tailscale’s admin console");
    public static string ReopenPage => L("Rouvrir la page de connexion", "Open the sign-in page again");
    public static string Retry => L("Réessayer", "Try again");

    /// <summary>What a sign-in or sign-out says beside its button (<see cref="BuiltInTailnet"/>).</summary>
    public static string PageOpened => L("La page de connexion de Tailscale s’est ouverte dans ton navigateur : connecte-toi avec ton compte Tailscale, celui du PC de Jellyfin.",
        "Tailscale’s sign-in page opened in your browser: sign in with your Tailscale account, the one of Jellyfin’s PC.");
    public static string Connected => L("Connecté à ton réseau Tailscale.", "Connected to your Tailscale network.");
    public static string NotStarted => L($"{Name} n’a pas pu démarrer : réessaie dans un instant.", $"{Name} couldn’t start: try again in a moment.");
    public static string NoAnswer => L("Tailscale ne répond pas : vérifie la connexion à Internet, puis réessaie.", "Tailscale isn’t answering: check the Internet connection, then try again.");
    public static string SigningOut => L("Déconnexion de Tailscale…", "Signing out of Tailscale…");
    public static string SignedOut => L("Déconnecté, et ses clés effacées de cet appareil. Il reste dans la console de Tailscale, « Logged out », jusqu’à ce que tu l’y supprimes.",
        "Signed out, and its keys erased from this device. It stays in Tailscale’s admin console, “Logged out”, until you remove it there.");
    public static string SignedOutHere => L("Déconnecté de cet appareil et ses clés effacées, mais Tailscale ne l’a pas su (pas de réseau) : supprime cet appareil dans la console de Tailscale.",
        "Signed out on this device and its keys erased, but Tailscale didn’t hear of it (no network): remove this device in Tailscale’s admin console.");
    public static string NotErased(string why, string folder) => L($"{Name} ne démarrera plus avec Mira, mais ses fichiers n’ont pas pu être effacés ({why}) : supprime le dossier {folder}.",
        $"{Name} won’t start with Mira any more, but its files couldn’t be erased ({why}): delete the folder {folder}.");
    public static string NoFolder(string why) => L($"Mira ne peut pas créer le dossier de {Name} ({why}).", $"Mira can’t create {Name}’s folder ({why}).");

    /// <summary>How Mira reaches its Jellyfin.</summary>
    public static string Direct => L("À la maison (direct)", "At home (direct)");
    public static string ThroughApp => L("Par l’app Tailscale", "Through the Tailscale app");
    public static string ThroughNode => L("Par Tailscale intégré", "Through Built-in Tailscale");
    public static string Path(string way) => L($"Chemin vers Jellyfin : {way}", $"Path to Jellyfin: {way}");

    /// <summary>
    /// Where the node is, in a line: <paramref name="helper"/> and <paramref name="status"/> from TailnetProcess;
    /// <paramref name="problem"/>, why it cannot run here, wins.
    /// </summary>
    public static string State(TailnetHelper helper, TailnetStatus? status, string? problem = null)
    {
        if (problem is not null) return problem;
        return helper switch
        {
            TailnetHelper.Off or TailnetHelper.Stopped => L("Pas connecté.", "Not signed in."),
            TailnetHelper.Starting => L("Démarrage…", "Starting…"),
            TailnetHelper.Restarting => L("Arrêté par une erreur, il redémarre tout seul…", "Stopped by an error, it’s starting again on its own…"),
            TailnetHelper.Failed => L("Il ne peut pas démarrer sur cet appareil.", "It can’t start on this device."),
            _ => status?.State switch
            {
                TailnetStates.NeedsLogin => L("Termine la connexion à Tailscale dans ton navigateur.", "Finish signing in to Tailscale in your browser."),
                TailnetStates.Expired => L("Sa connexion à Tailscale a expiré : reconnecte-le.", "Its sign-in to Tailscale expired: sign in again."),
                TailnetStates.MachineAuth => WaitingApproval + ".",
                TailnetStates.Running when status.Offline => L("Connecté, mais sans réseau pour joindre Tailscale.", "Signed in, but without a network to reach Tailscale."),
                TailnetStates.Running => status.User is { Length: > 0 } user ? L($"Connecté : {user}.", $"Signed in: {user}.") : L("Connecté.", "Signed in."),
                TailnetStates.Stopped => L("Arrêté.", "Stopped."),
                _ => L("Connexion à Tailscale…", "Connecting to Tailscale…"),
            },
        };
    }

    /// <summary>
    /// Why a server was not reached through the node, for one of the codes of <see cref="TailnetCodes"/>: in words, with
    /// the device it is on when Tailscale knows it (<paramref name="peer"/>). Null for a code that says nothing more than
    /// « no answer » (the relay's, the protocol's).
    /// </summary>
    public static string? Problem(string code, TailnetDevice? peer = null) => code switch
    {
        TailnetCodes.NeedsLogin => L($"{Name} n’est pas connecté : « {SignIn} » dans les Réglages.", $"{Name} isn’t signed in: “{SignIn}” in Settings."),
        TailnetCodes.Expired => L($"La connexion de {Name} a expiré : « {SignIn} » dans les Réglages.", $"{Name}’s sign-in expired: “{SignIn}” in Settings."),
        TailnetCodes.MachineAuth => WaitingApproval + ".",
        TailnetCodes.Starting => L($"{Name} n’a pas encore rejoint ton réseau Tailscale.", $"{Name} hasn’t joined your Tailscale network yet."),
        TailnetCodes.Offline => L($"Pas de réseau : {Name} ne joint pas Tailscale.", $"No network: {Name} can’t reach Tailscale."),
        TailnetCodes.Refused => L("Cette adresse n’est sur aucun appareil de ton réseau Tailscale.", "This address isn’t on any device of your Tailscale network."),
        TailnetCodes.PeerOffline => Asleep(peer),
        TailnetCodes.Unreachable => L($"{Device(peer)} est connecté à Tailscale, mais ne répond pas.", $"{Device(peer)} is connected to Tailscale, but isn’t answering."),
        TailnetCodes.Closed => L($"{Device(peer)} répond par Tailscale, mais Jellyfin n’y tourne pas.", $"{Device(peer)} answers through Tailscale, but Jellyfin isn’t running on it."),
        TailnetCodes.Locked => L($"Un autre Mira utilise déjà {Name} avec ce profil.", $"Another Mira is already using {Name} with this profile."),
        TailnetCodes.Stopped or TailnetCodes.Helper => L($"{Name} est arrêté.", $"{Name} is stopped."),
        TailnetCodes.Key => L($"Mira ne peut pas lire la clé de {Name} sur cet appareil.", $"Mira can’t read {Name}’s key on this device."),
        TailnetCodes.Version => L($"{Name} n’est pas celui de cette version de Mira : réinstalle Mira.", $"{Name} isn’t the one from this version of Mira: reinstall Mira."),
        _ => null,
    };

    /// <summary>Days before its key expires from which Mira says it, as for the Tailscale app on Jellyfin's PC.</summary>
    public const int WarnDays = TailscaleNode.WarnDays;
    /// <summary>
    /// Said once at Mira's start while the node's key expires within <see cref="WarnDays"/> days (after 180 days by
    /// default); null otherwise, or when it never expires. Once expired, <see cref="State"/> says it.
    /// </summary>
    public static string? Expiry(TailnetStatus? status, DateTimeOffset now) =>
        status is { State: TailnetStates.Running, Self: { KeyExpiry: { } at } self } && at > now && at - now <= TimeSpan.FromDays(WarnDays)
            ? L($"La connexion de {Name} à Tailscale expire le {Day(at.ToLocalTime())} : reconnecte-le alors, ou coupe cette expiration dans la console de Tailscale (menu ⋯ de « {self.HostName} », « Disable key expiry »).",
                $"{Name}’s connection to Tailscale expires on {Day(at.ToLocalTime())}: sign in again then, or turn off this expiry in Tailscale’s admin console (the ⋯ menu of “{self.HostName}”, “Disable key expiry”).")
            : null;

    /// <summary>The server's device is not on Tailscale: off, asleep or without Internet, and since when Tailscale last saw it.</summary>
    public static string Asleep(TailnetDevice? peer)
    {
        var name = Device(peer);
        return peer?.LastSeen is { } seen
            ? L($"{name} n’est pas connecté à Tailscale (vu pour la dernière fois {When(seen)}) : il est éteint, en veille ou sans Internet.",
                $"{name} isn’t connected to Tailscale (last seen {When(seen)}): it’s off, asleep or without Internet.")
            : L($"{name} n’est pas connecté à Tailscale : il est éteint, en veille ou sans Internet.", $"{name} isn’t connected to Tailscale: it’s off, asleep or without Internet.");
    }
    /// <summary>The device by its name on Tailscale, else as « le PC de Jellyfin ».</summary>
    private static string Device(TailnetDevice? peer) => peer is { HostName.Length: > 0 } ? peer.HostName : L("Le PC de Jellyfin", "Jellyfin’s PC");
    private static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime().DateTime;
        return local.Date == DateTime.Today ? L($"à {Time(local)}", $"at {Time(local)}") : L($"le {Day(local)} à {Time(local)}", $"on {Day(local)} at {Time(local)}");
    }
}
