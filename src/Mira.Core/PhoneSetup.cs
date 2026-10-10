namespace Mira.Core;

/// <summary>
/// « Mira sur ton téléphone », in the guide of Mira for Windows: what stands between a Jellyfin and a phone that opens
/// Mira web, at home and away, as steps in the order they matter. Each is done, done by « Tout préparer » (one click,
/// one consent of Windows), or left to the person (an app to install, another account); and the address the QR code
/// gives. The guide reads the facts; this says what they mean, so that the tests can check every case.
/// </summary>
public static class PhoneSetup
{
    /// <summary>Done; to do by « Tout préparer »; for the person to do (with a link, or elsewhere); a note.</summary>
    public enum Mark { Done, ToDo, Yours, Note }
    public sealed record Step(string Key, Mark Mark, string Title, string Text, string? Action = null);

    /// <summary>
    /// What the guide found. <paramref name="Server"/> is Jellyfin's address in Mira (127.0.0.1 for one on this PC,
    /// then <paramref name="Local"/>); <paramref name="Home"/> the one phones use on the home network;
    /// <paramref name="Tailnet"/> this PC's on Tailscale, when Tailscale is connected here. Null: not known, or not
    /// readable with this account.
    /// </summary>
    public sealed record Facts(string Server, bool Local, bool? Web, string? Home, bool? Admin = null, string? Tailnet = null, bool? TailnetAllowed = null,
        JellyfinFirewall.Verdict? Firewall = null, int FirewallProfile = JellyfinFirewall.Private, bool MiraRule = false, JellyfinStartup.State? Boot = null);

    public static IReadOnlyList<Step> Steps(Facts facts)
    {
        var steps = new List<Step>
        {
            facts.Web switch
            {
                true => new("web", Mark.Done, "Mira web est installé sur Jellyfin", "Jellyfin le met à jour à chaque version de Mira."),
                false when facts.Admin == false => new("web", Mark.Yours, "Installer Mira web sur Jellyfin", "Il faut le compte administrateur de Jellyfin : connecte Mira avec celui-ci."),
                false => new("web", Mark.ToDo, "Installer Mira web sur Jellyfin", "C’est Jellyfin qui sert Mira à ton téléphone : rien à prendre sur l’App Store ou le Play Store."),
                null => new("web", Mark.Note, "Mira web sur Jellyfin", "Vérification…"),
            },
        };
        if (!facts.Local)
        {
            steps.Add(new("elsewhere", Mark.Note, "Jellyfin tourne sur un autre ordinateur",
                "Le pare-feu et Tailscale se règlent sur celui-là : ouvre ce guide dans Mira sur cet ordinateur."));
            return steps;
        }
        if (facts.Home is null)
            steps.Add(new("network", Mark.Yours, "Connecter ce PC à ta box", "En Wi-Fi ou par câble : c’est par elle que ton téléphone le trouve."));
        var network = facts.FirewallProfile switch { JellyfinFirewall.Public => "Réseau public", JellyfinFirewall.Domain => "Réseau de domaine", _ => "Réseau privé" };
        steps.Add((facts.Firewall ?? (facts.MiraRule ? JellyfinFirewall.Verdict.Open : JellyfinFirewall.Verdict.Blocked)) switch
        {
            JellyfinFirewall.Verdict.Open => new("firewall", Mark.Done, "Le pare-feu laisse passer ton téléphone", "Sur le port de Jellyfin seulement, depuis ton Wi-Fi et Tailscale."),
            JellyfinFirewall.Verdict.Closed => new("firewall", Mark.Yours, "Le pare-feu bloque toutes les connexions",
                $"Dans Sécurité Windows → Pare-feu et protection du réseau → {network}, décoche « Bloque toutes les connexions entrantes »."),
            _ => new("firewall", Mark.ToDo, "Laisser passer ton téléphone", "Le pare-feu de Windows bloque Jellyfin : sur le téléphone, la page reste blanche."),
        });
        steps.Add((facts.Tailnet, facts.TailnetAllowed) switch
        {
            (null, _) => new("tailscale", Mark.Yours, "Hors de chez toi (facultatif)",
                "Installe Tailscale, gratuit, sur ce PC et sur ton téléphone, avec le même compte : Mira marche partout, sans ouvrir ta box à Internet.", "Installer Tailscale"),
            (_, true) => new("tailscale", Mark.Done, "Prêt hors de chez toi", "Garde Tailscale activé sur ton téléphone, avec le même compte que ce PC."),
            (_, false) when facts.Admin != false => new("tailscale", Mark.ToDo, "Autoriser Tailscale dans Jellyfin", "Tes appareils Tailscale seulement : le reste d’Internet reste refusé."),
            _ => new("tailscale", Mark.Yours, "Autoriser Tailscale dans Jellyfin", "Il faut le compte administrateur de Jellyfin : connecte Mira avec celui-ci."),
        });
        steps.Add(facts.Boot switch
        {
            { StartsAtBoot: true } =>
                new("boot", Mark.Done, "Disponible dès l’allumage du PC", "Jellyfin démarre avec Windows, avant ta session, et repart après une erreur."),
            { HasService: true } => new("boot", Mark.ToDo, "Disponible dès l’allumage du PC", "Jellyfin démarrera avec Windows, sans attendre ta session, et repartira après une erreur."),
            _ => new("boot", Mark.Note, "Jellyfin démarre avec ta session", "Ton téléphone le trouve une fois ta session Windows ouverte sur ce PC."),
        });
        return steps;
    }

    /// <summary>Whether « Tout préparer » has something to do, and whether it asks for Windows' consent.</summary>
    public static bool CanPrepare(IReadOnlyList<Step> steps) => steps.Any(x => x.Mark == Mark.ToDo);
    public static bool NeedsWindows(IReadOnlyList<Step> steps) => steps.Any(x => x.Mark == Mark.ToDo && x.Key is "firewall" or "boot");
    public static bool Is(IReadOnlyList<Step> steps, string key, Mark mark) => steps.Any(x => x.Key == key && x.Mark == mark);

    /// <summary>
    /// This PC on Tailscale, once phones get through there: the address that works everywhere (at home too, with
    /// Tailscale on in the phone). Null until then.
    /// </summary>
    public static string? Everywhere(Facts facts) =>
        facts.Local && facts.Tailnet is not null && facts.TailnetAllowed == true && facts.Firewall == JellyfinFirewall.Verdict.Open ? facts.Tailnet : null;

    /// <summary>The address to type in the phone's browser: the one that works everywhere, or the home network's.</summary>
    public static string Address(Facts facts) => (Everywhere(facts) ?? facts.Home ?? facts.Server).TrimEnd('/') + "/Mira";

    /// <summary>
    /// The page with the QR code, opened on this PC at an address it always reaches (the home network's): the code is
    /// for the address that works everywhere when there is one, with the home one to switch to.
    /// </summary>
    public static string SharePage(Facts facts) => MiraWeb.SharePage(facts.Home ?? facts.Server, Everywhere(facts));
}
