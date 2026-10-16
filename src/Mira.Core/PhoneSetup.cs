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
    /// What the guide found. <paramref name="Server"/> is Jellyfin's address in Mira (<paramref name="Local"/>: on this
    /// PC); <paramref name="Home"/> the one phones use on the home network; <paramref name="Tailnet"/> this PC's on
    /// Tailscale, when Tailscale is connected here. <paramref name="Firewall"/>: what phones meet
    /// (<see cref="JellyfinFirewall.Reach"/>), null when it cannot be read, Mira's rule (<paramref name="MiraRule"/>,
    /// judged by its content) then deciding; <paramref name="Program"/>: whether Mira found Jellyfin's program on this
    /// PC, which the firewall is opened for. <paramref name="Answering"/>: Jellyfin answers (it may be restarting, or
    /// stopped). <paramref name="NetworkKept"/>: Windows did not let « Passer en privé » make the public network
    /// (<see cref="JellyfinFirewall.Reach.Public"/>) private. <paramref name="Down"/>: what a Jellyfin on this PC that
    /// does not answer is (<see cref="LocalJellyfin.Judge"/>). <paramref name="Node"/>: Tailscale on this PC, read by its
    /// command line (<see cref="TailscaleNode"/>). <paramref name="Power"/>: Windows' power plan on mains power
    /// (<see cref="ServerSleep"/>). Null: not known, or not readable with this account.
    /// </summary>
    public sealed record Facts(string Server, bool Local, bool? Web, string? Home, bool? Admin = null, string? Tailnet = null, bool? TailnetAllowed = null,
        JellyfinFirewall.Reach? Firewall = null, JellyfinFirewall.RuleState MiraRule = JellyfinFirewall.RuleState.Missing, JellyfinStartup.State? Boot = null,
        bool Program = true, bool Answering = true, bool NetworkKept = false, LocalJellyfin.Verdict Down = LocalJellyfin.Verdict.Unknown,
        TailscaleNode.State? Node = null, ServerSleep.Plan? Power = null);

    public static IReadOnlyList<Step> Steps(Facts facts)
    {
        var steps = new List<Step>
        {
            facts.Web switch
            {
                true => new("web", Mark.Done, L("Mira web est installé sur Jellyfin", "Mira web is installed on Jellyfin"), L("Il suit la version de Mira : quand Mira se met à jour et que tu l’ouvres avec le compte administrateur de Jellyfin, Jellyfin installe la nouvelle version tout de suite, et Mira propose de le redémarrer dès que personne ne regarde.", "It follows Mira’s version: when Mira updates and you open it with Jellyfin’s administrator account, Jellyfin installs the new version right away, and Mira offers to restart it as soon as no one is watching.")),
                false when facts.Admin == false => new("web", Mark.Yours, L("Installer Mira web sur Jellyfin", "Install Mira web on Jellyfin"), L("Il faut le compte administrateur de Jellyfin : connecte Mira avec celui-ci.", "This needs Jellyfin’s administrator account: sign Mira in with it.")),
                false => new("web", Mark.ToDo, L("Installer Mira web sur Jellyfin", "Install Mira web on Jellyfin"), L("C’est Jellyfin qui sert Mira à ton téléphone : rien à prendre sur l’App Store ou le Play Store.", "Jellyfin serves Mira to your phone: nothing to get from the App Store or the Play Store.")),
                // Its button starts it: Windows' consent for its service, none for its program.
                null when !facts.Answering && facts.Local && facts.Down == LocalJellyfin.Verdict.Stopped => new("web", Mark.Yours, L("Jellyfin est arrêté sur ce PC", "Jellyfin is stopped on this PC"),
                    L("Ton téléphone ne le trouve plus", "Your phone can’t find it anymore") + (facts.Boot?.HasService == true ? L(" : le démarrer demande une autorisation de Windows.", ": starting it asks for Windows’ permission.") : "."), LocalJellyfin.Start),
                null when !facts.Answering && facts.Local && facts.Down == LocalJellyfin.Verdict.Starting => new("web", Mark.Note, L("Jellyfin démarre sur ce PC", "Jellyfin is starting on this PC"), L("Cette carte se met à jour dès qu’il répond.", "This card updates as soon as it answers.")),
                null when !facts.Answering && facts.Local && facts.Down == LocalJellyfin.Verdict.Failing => new("web", Mark.Yours, L("Jellyfin s’arrête juste après avoir démarré", "Jellyfin stops right after starting"),
                    L("Son journal dit pourquoi. Une fois la cause corrigée, cette carte propose de nouveau de le démarrer.", "Its log says why. Once the cause is fixed, this card offers to start it again."), LocalJellyfin.OpenLog),
                null when !facts.Answering => new("web", Mark.Note, L("Jellyfin ne répond pas pour l’instant", "Jellyfin isn’t answering right now"), L("Il redémarre, ou il est arrêté : cette carte se met à jour dès qu’il répond.", "It’s restarting, or it’s stopped: this card updates as soon as it answers.")),
                null => new("web", Mark.Note, L("Mira web sur Jellyfin", "Mira web on Jellyfin"), L("Vérification…", "Checking…")),
            },
        };
        if (!facts.Local)
        {
            steps.Add(new("elsewhere", Mark.Note, L("Jellyfin tourne sur un autre ordinateur", "Jellyfin runs on another computer"),
                L("Le pare-feu et Tailscale se règlent sur celui-là : ouvre ce guide dans Mira sur cet ordinateur.", "The firewall and Tailscale are set up on that one: open this guide in Mira on that computer.")));
            return steps;
        }
        if (facts.Home is null)
            steps.Add(new("network", Mark.Yours, L("Connecter ce PC à ta box", "Connect this PC to your router"), L("En Wi-Fi ou par câble : c’est par elle que ton téléphone le trouve.", "By Wi-Fi or cable: that’s how your phone finds it.")));
        var network = facts.Firewall?.Profile switch { JellyfinFirewall.Public => L("Réseau public", "Public network"), JellyfinFirewall.Domain => L("Réseau de domaine", "Domain network"), _ => L("Réseau privé", "Private network") };
        // What Mira cannot change is the person's, said where: « Tout préparer » never asks again for what it cannot do.
        // Phones getting through by other rules leave Mira's own, when it is not as Mira writes it, to replace. A network
        // counted as public, where Mira's rule never applies, is made private only when the person says it is home's
        // (« Passer en privé », its own consent): the same adapter joins a café's, and the card may be read there. Mira's
        // rule is still written or replaced by « Tout préparer », for Tailscale and private networks. A network Windows
        // keeps public, or whose profile it does not say, is the person's to change in its settings.
        var counted = facts.Firewall?.Public;
        var shown = counted is null ? "" : counted.Name is { Length: > 0 } name && !name.Equals(counted.Adapter, StringComparison.OrdinalIgnoreCase)
            ? L($"« {name} » ({counted.Adapter})", $"“{name}” ({counted.Adapter})") : L($"« {counted.Adapter} »", $"“{counted.Adapter}”");
        var where = counted is null ? "" : L($"dans Paramètres → Réseau et Internet → {counted.Adapter}, choisis « Réseau privé » (pour le Wi-Fi, dans les propriétés de ton réseau)",
            $"in Settings → Network & internet → {counted.Adapter}, choose “Private network” (for Wi-Fi, in your network’s properties)");
        var publicMark = facts.MiraRule == JellyfinFirewall.RuleState.Ready ? Mark.Yours : Mark.ToDo;
        steps.Add(Firewall(facts) switch
        {
            JellyfinFirewall.Verdict.Open when facts.MiraRule != JellyfinFirewall.RuleState.Stale =>
                new("firewall", Mark.Done, L("Le pare-feu laisse passer ton téléphone", "The firewall lets your phone through"), L("Sur le port de Jellyfin seulement, depuis ton Wi-Fi et Tailscale.", "On Jellyfin’s port only, from your Wi-Fi and Tailscale.")),
            // Named only when the rest is open: Mira's rule is not what is missing.
            _ when counted is { Known: false } => new("firewall", Mark.Yours, L("Vérifier le type de ton réseau", "Check your network’s type"),
                L($"Windows ne dit pas à Mira si {shown} est un réseau privé ou public ; s’il est public, ton téléphone n’y entre pas. Si c’est le réseau de ta maison, {where}.", $"Windows doesn’t tell Mira whether {shown} is a private or public network; if it’s public, your phone can’t get in. If it’s your home network, {where}.")),
            _ when counted is not null && facts.NetworkKept => new("firewall", publicMark, L("Passer ton réseau en privé", "Make your network private"),
                L($"Windows compte {shown} comme un réseau public et n’a pas laissé Mira le changer : {where}.", $"Windows counts {shown} as a public network and didn’t let Mira change it: {where}.")),
            _ when counted is not null => new("firewall", publicMark, L("Ton réseau est compté comme public", "Your network counts as public"),
                L($"Windows compte {shown} comme un réseau public, comme celui d’un café, où Mira ne laisse pas entrer ton téléphone. Si c’est le réseau de ta maison, "
                + "passe-le en privé, avec une autorisation de Windows.",
                $"Windows counts {shown} as a public network, like a coffee shop’s, where Mira doesn’t let your phone in. If it’s your home network, "
                + "make it private, with Windows’ permission."), L("Passer en privé", "Make it private")),
            JellyfinFirewall.Verdict.Closed => new("firewall", Mark.Yours, L("Le pare-feu bloque toutes les connexions", "The firewall blocks all connections"),
                L($"Dans Sécurité Windows → Pare-feu et protection du réseau → {network}, décoche « Bloque toutes les connexions entrantes ».", $"In Windows Security → Firewall & network protection → {network}, untick “Blocks all incoming connections”.")),
            _ when facts.Firewall?.Kept is { } rule => new("firewall", Mark.Yours, L("Une règle du pare-feu bloque Jellyfin", "A firewall rule blocks Jellyfin"),
                L($"Dans « Pare-feu Windows Defender avec fonctions avancées » → Règles de trafic entrant, désactive « {rule} ».", $"In “Windows Defender Firewall with Advanced Security” → Inbound Rules, disable “{rule}”.")),
            _ when !facts.Program => new("firewall", Mark.Yours, L("Autoriser Jellyfin dans le pare-feu", "Allow Jellyfin in the firewall"),
                L("Mira ne trouve pas le programme de Jellyfin sur ce PC : autorise-le dans le pare-feu de Windows pour les réseaux privés, "
                + "et vérifie que Windows compte ton Wi-Fi comme réseau privé.",
                "Mira can’t find Jellyfin’s program on this PC: allow it in Windows’ firewall for private networks, "
                + "and check that Windows counts your Wi-Fi as a private network.")),
            JellyfinFirewall.Verdict.Open => new("firewall", Mark.ToDo, L("Mettre à jour la règle du pare-feu", "Update the firewall rule"),
                L("La règle de Mira, d’une version précédente, laisse aussi entrer les réseaux publics, ou ne correspond plus au port ou au programme de Jellyfin : "
                + "Mira la remplace par une règle pour tes réseaux privés seulement.",
                "Mira’s rule, from an earlier version, also lets public networks in, or no longer matches Jellyfin’s port or program: "
                + "Mira replaces it with a rule for your private networks only.")),
            _ => new("firewall", Mark.ToDo, L("Laisser passer ton téléphone", "Let your phone through"), L("Le pare-feu de Windows bloque Jellyfin : sur le téléphone, la page reste blanche.", "Windows’ firewall blocks Jellyfin: on the phone, the page stays blank.")),
        });
        steps.Add((facts.Tailnet, facts.TailnetAllowed) switch
        {
            (null, _) => new("tailscale", Mark.Yours, L("Hors de chez toi (facultatif)", "Away from home (optional)"),
                L("Installe Tailscale, gratuit, sur ce PC et sur ton téléphone, avec le même compte : Mira marche partout, sans ouvrir ta box à Internet.",
                    "Install Tailscale, free, on this PC and on your phone, with the same account: Mira works everywhere, without opening your router to the Internet."), L("Installer Tailscale", "Install Tailscale")),
            (_, true) => new("tailscale", Mark.Done, L("Prêt hors de chez toi", "Ready away from home"), L("Garde Tailscale activé sur ton téléphone, avec le même compte que ce PC.", "Keep Tailscale on in your phone, with the same account as this PC.")),
            (_, false) when facts.Admin != false => new("tailscale", Mark.ToDo, L("Autoriser Tailscale dans Jellyfin", "Allow Tailscale in Jellyfin"), L("Tes appareils Tailscale seulement : le reste d’Internet reste refusé.", "Your Tailscale devices only: the rest of the Internet stays refused.")),
            (_, null) when facts.Admin != false => new("tailscale", Mark.Note, L("Tailscale dans Jellyfin", "Tailscale in Jellyfin"), facts.Answering ? L("Vérification…", "Checking…") : L("Vérifié dès que Jellyfin répond.", "Checked as soon as Jellyfin answers.")),
            _ => new("tailscale", Mark.Yours, L("Autoriser Tailscale dans Jellyfin", "Allow Tailscale in Jellyfin"), L("Il faut le compte administrateur de Jellyfin : connecte Mira avec celui-ci.", "This needs Jellyfin’s administrator account: sign Mira in with it.")),
        });
        // Tailscale on this PC, once it has its address: connected without a Windows session, and this PC's connection
        // not about to expire. Both are the person's: « Tout préparer » never changes Tailscale; « Run unattended » has
        // its own button, which runs Tailscale's command line, in the person's rights.
        if (facts.Tailnet is not null && facts.Node is { } node)
        {
            steps.Add(node.Unattended switch
            {
                true => new("unattended", Mark.Done, L("Tailscale reste connecté sans ta session", "Tailscale stays connected without your session"), L("Après un redémarrage, il se reconnecte sans attendre que tu ouvres ta session.", "After a restart, it reconnects without waiting for you to sign in to Windows.")),
                false => new("unattended", Mark.Yours, L("Tailscale se coupe sans ta session", "Tailscale disconnects without your session"),
                    L($"Après un redémarrage (une mise à jour de Windows la nuit, par exemple), ton téléphone ne trouve plus ce PC tant que tu n’as pas ouvert ta session. Mira peut le régler ; sinon, {TailscaleNode.WhereUnattended}.", $"After a restart (a Windows update at night, for example), your phone can’t find this PC until you sign in to Windows. Mira can set this; otherwise, {TailscaleNode.WhereUnattended}."),
                    TailscaleNode.KeepConnected),
                null => new("unattended", Mark.Note, L("Tailscale sans ta session", "Tailscale without your session"),
                    L($"Mira ne peut pas lire les réglages de Tailscale. Pour que ton téléphone trouve ce PC après un redémarrage, avant que tu ouvres ta session, {TailscaleNode.WhereUnattended}.", $"Mira can’t read Tailscale’s settings. For your phone to find this PC after a restart, before you sign in to Windows, {TailscaleNode.WhereUnattended}.")),
            });
            if (TailscaleNode.ExpiresSoon(node))
                steps.Add(node.Expired
                    ? new("keyexpiry", Mark.Yours, L("La connexion de ce PC à Tailscale a expiré", "This PC’s Tailscale connection has expired"),
                        L($"Hors de chez toi, ton téléphone ne le trouve plus. Reconnecte-toi dans le menu de Tailscale ; puis, pour qu’elle n’expire plus, {TailscaleNode.Disable(node)}.", $"Away from home, your phone can’t find it anymore. Sign in again from Tailscale’s menu; then, to keep it from expiring, {TailscaleNode.Disable(node)}."), TailscaleNode.OpenMachines)
                    : new("keyexpiry", Mark.Yours, L($"La connexion de ce PC à Tailscale expire le {TailscaleNode.Day(node.KeyExpiry!.Value)}", $"This PC’s Tailscale connection expires on {TailscaleNode.Day(node.KeyExpiry!.Value)}"),
                        L($"Ensuite, hors de chez toi, ton téléphone ne le trouvera plus jusqu’à ce que tu te reconnectes sur ce PC. Pour qu’elle n’expire plus, {TailscaleNode.Disable(node)}.", $"After that, away from home, your phone won’t find it until you sign in again on this PC. To keep it from expiring, {TailscaleNode.Disable(node)}."), TailscaleNode.OpenMachines));
        }
        steps.Add(facts.Boot switch
        {
            // Away from home, Tailscale too waits for the session: said by its own step, above.
            { StartsAtBoot: true } when facts.Tailnet is not null && facts.Node?.Unattended == false =>
                new("boot", Mark.Done, L("Jellyfin démarre dès l’allumage du PC", "Jellyfin starts as soon as the PC turns on"), L("Avant ta session, et il repart après une erreur ; hors de chez toi, Tailscale attend encore ta session (plus haut).", "Before your session, and it restarts after an error; away from home, Tailscale still waits for your session (above).")),
            { StartsAtBoot: true } =>
                new("boot", Mark.Done, L("Disponible dès l’allumage du PC", "Available as soon as the PC turns on"), L("Jellyfin démarre avec Windows, avant ta session, et repart après une erreur.", "Jellyfin starts with Windows, before your session, and restarts after an error.")),
            { HasService: true } => new("boot", Mark.ToDo, L("Disponible dès l’allumage du PC", "Available as soon as the PC turns on"), L("Jellyfin démarrera avec Windows, sans attendre ta session, et repartira après une erreur.", "Jellyfin will start with Windows, without waiting for your session, and restart after an error.")),
            _ => new("boot", Mark.Note, L("Jellyfin démarre avec ta session", "Jellyfin starts with your session"), L("Ton téléphone le trouve une fois ta session Windows ouverte sur ce PC.", "Your phone finds it once you’ve signed in to Windows on this PC.")),
        });
        // Asleep, the PC answers no one: changed only by the person's own button, never by « Tout préparer ».
        if (facts.Power is { } power)
        {
            var (title, text) = ServerSleep.Says(power);
            steps.Add(ServerSleep.Sleeps(power) ? new("sleep", Mark.Yours, title, text, ServerSleep.NeverOnMains) : new("sleep", Mark.Done, title, text));
        }
        return steps;
    }

    /// <summary>Whether « Tout préparer » has something to do, and whether it asks for Windows' consent.</summary>
    public static bool CanPrepare(IReadOnlyList<Step> steps) => steps.Any(x => x.Mark == Mark.ToDo);
    public static bool NeedsWindows(IReadOnlyList<Step> steps) => steps.Any(x => x.Mark == Mark.ToDo && x.Key is "firewall" or "boot");
    public static bool Is(IReadOnlyList<Step> steps, string key, Mark mark) => steps.Any(x => x.Key == key && x.Mark == mark);

    /// <summary>The firewall for phones, the worse of the home network and Tailscale: read, or Mira's rule, ready, when it cannot be.</summary>
    public static JellyfinFirewall.Verdict Firewall(Facts facts) =>
        facts.Firewall?.Verdict ?? (facts.MiraRule == JellyfinFirewall.RuleState.Ready ? JellyfinFirewall.Verdict.Open : JellyfinFirewall.Verdict.Blocked);

    /// <summary>
    /// This PC on Tailscale, once phones get through there (whatever the home network's firewall): the address that
    /// works everywhere (at home too, with Tailscale on in the phone). Null until then.
    /// </summary>
    public static string? Everywhere(Facts facts) =>
        facts.Local && facts.Tailnet is not null && facts.TailnetAllowed == true && (facts.Firewall?.Tailnet ?? Firewall(facts)) == JellyfinFirewall.Verdict.Open ? facts.Tailnet : null;

    /// <summary>The address to type in the phone's browser: the one that works everywhere, or the home network's.</summary>
    public static string Address(Facts facts) => (Everywhere(facts) ?? facts.Home ?? facts.Server).TrimEnd('/') + "/Mira";

    /// <summary>
    /// The page with the QR code, opened on this PC at an address it always reaches (the home network's): the code is
    /// for the address that works everywhere when there is one, with the home one to switch to.
    /// </summary>
    public static string SharePage(Facts facts) => MiraWeb.SharePage(facts.Home ?? facts.Server, Everywhere(facts));
}
