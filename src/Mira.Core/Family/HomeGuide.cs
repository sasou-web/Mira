namespace Mira.Core.Family;

/// <summary>
/// « Away from home », set up once by the administrator as a guide (maquette A « Le guide », ogsas 2026-11-20): one step
/// per screen, the one thing to do there, a button that opens the right page of Tailscale's console and a drawing of
/// what to click; then Mira checks the key and puts the network's rules right on a click. Tailscale's words (OAuth,
/// scopes, policy) are said only where they are to be read on its console. The same words and drawings in every app.
/// </summary>
public static class HomeGuide
{
    /// <summary>The steps: the tag, the key (OAuth client), its secret, the check and the rules.</summary>
    public const int Steps = 4;

    /// <summary>The visual editor's tags, where step 1 is done.</summary>
    public const string TagsPage = "https://login.tailscale.com/admin/acls/visual";
    /// <summary>The trust credentials, where step 2 is done.</summary>
    public const string CredentialsPage = "https://login.tailscale.com/admin/settings/trust-credentials";
    /// <summary>The policy file, for what Mira can't fix.</summary>
    public const string PolicyPage = "https://login.tailscale.com/admin/acls/file";
    /// <summary>The tag's name as typed in the console (without « tag: »).</summary>
    public const string TagName = "mira-home";

    // —— Réglages → Home, the row ——

    public static string EntryTitle => L("Laisser la famille regarder loin de la maison", "Let the family watch away from home");
    public static string NotSetUp => L("Pas réglé", "Not set up");
    public static string EntryText => L("Aujourd’hui le foyer ne marche que sur ton Wi-Fi. Mira te guide une fois dans Tailscale, environ 3 minutes. Ensuite, un lien du foyer suffit à la famille : ni adresse, ni compte Tailscale.",
        "Today the home works on your Wi-Fi only. Mira walks you through Tailscale once, about 3 minutes. After that, a home link is all the family needs: no address, no Tailscale account.");
    public static string SetUp => L("Régler…", "Set up…");
    public static string Continue => L("Continuer le réglage…", "Continue setup…");
    public static string ReadyTitle => L("La famille peut regarder loin de la maison", "The family can watch away from home");
    public static string Ready => L("Prêt", "Ready");
    public static string ReadyText => L("Les appareils du foyer n’atteignent que Jellyfin sur ton réseau Tailscale. Vérifié à l’instant.", "Home devices reach only Jellyfin on your Tailscale network. Checked just now.");
    public static string FirstLinkTitle => L("Faire le premier lien du foyer", "Make the first home link");
    public static string FirstLinkText => L("Envoie-le à quelqu’un de la famille : il ouvre Mira sur ton foyer, où qu’il soit. Il sert une fois et finit au bout de 48 heures.",
        "Send it to someone in the family: it opens Mira on your home, wherever they are. Works once, ends after 48 hours.");
    public static string MakeLink => L("Faire un lien du foyer", "Make a home link");
    public static string Disconnect => L("Déconnecter…", "Disconnect…");
    public static string Undo => L("Annuler", "Undo");
    public static string Undone => L("Les règles d’avant sont revenues.", "Your previous rules are back.");
    public static string Changed(DateTimeOffset until) => L($"Mira a changé tes règles. Tu peux annuler jusqu’à {Lang.Time(until.ToLocalTime().DateTime)}.", $"Mira changed your rules. You can undo it until {Lang.Time(until.ToLocalTime().DateTime)}.");

    // —— The sheet ——

    public static string Header(int step) => L($"Loin de la maison · Étape {step} sur {Steps}", $"Away from home · Step {step} of {Steps}");
    public static string Close => L("Fermer", "Close");
    public static string Back => L("Retour", "Back");
    public static string NotNow => L("Plus tard", "Not now");

    /// <summary>A step of the guide: its title, why, what to do (one line each), its button to Tailscale, its foot.</summary>
    public sealed record Step(int Number, string Title, string Why, IReadOnlyList<string> Do, string? Open, string? OpenLabel, string? FootNote, string Next);

    public static Step Tag => new(1,
        L("Crée une étiquette pour les appareils de la famille", "Make a label for the family’s devices"),
        L("Tailscale l’appelle « tag ». C’est ainsi que ton réseau reconnaît les appareils du foyer : ils atteignent Jellyfin, et rien d’autre.",
            "Tailscale calls it a tag. It’s how your network tells the home’s devices apart, so they can reach Jellyfin and nothing else."),
        [L("Clique sur Tags, puis Create tag.", "Click Tags, then Create tag."),
         L($"Nomme-la {TagName} (bouton Copier ci-dessous).", $"Name it {TagName} (Copy button below)."),
         L("Clique sur Save tag, puis reviens ici.", "Click Save tag, then come back here.")],
        TagsPage, L("Ouvrir les étiquettes de Tailscale", "Open Tailscale’s tags"),
        L("Mira ne peut pas faire cette étape à ta place : Tailscale la demande d’abord.", "Mira can’t do this step for you: Tailscale asks for it first."),
        L("J’ai enregistré l’étiquette", "I’ve saved the tag"));

    public static Step Key => new(2,
        L("Donne à Mira une clé de ton réseau", "Give Mira a key to your network"),
        L("Une clé qui ne sait faire que trois choses : les liens du foyer, retirer un appareil, et garder tes règles justes. Coche exactement ce que montre le dessin.",
            "A key that can do three things only: make the home links, remove a device, and keep your rules right. Tick exactly what the picture shows."),
        [L("Clique sur Credential, choisis OAuth, puis Custom scopes. Pas « All ».", "Click Credential, choose OAuth, then Custom scopes. Not “All”."),
         L($"Coche Read et Write sur Policy File, Devices › Core et Keys › Auth Keys. Sur les deux derniers, choisis l’étiquette {TagName}.", $"Tick Read and Write on Policy File, Devices › Core and Keys › Auth Keys. On the last two, pick the tag {TagName}."),
         L("Clique sur Generate credential.", "Click Generate credential.")],
        CredentialsPage, L("Ouvrir les identifiants de Tailscale", "Open Tailscale’s credentials"),
        L("L’étiquette n’est pas dans la liste ? L’étape 1 n’a pas été enregistrée.", "The tag isn’t in the list? Step 1 wasn’t saved."),
        L("Je vois le secret", "I see the secret"));

    public static Step Secret => new(3,
        L("Copie le secret, Mira fait le reste", "Copy the secret, Mira does the rest"),
        L("Tailscale ne le montre qu’une fois. Mira l’envoie à ton Jellyfin, qui le garde ; cet appareil ne le garde jamais.",
            "Tailscale shows it once. Mira sends it to your Jellyfin, which keeps it; this device never stores it."),
        [], null, null,
        L("Une seule case, pas deux : l’identifiant du client est dans le secret.", "One box, not two: the client ID is inside the secret."),
        L("Vérifier", "Check it"));

    public static Step Rules(int changes) => new(4,
        changes > 0 ? L("Une dernière chose : les règles de ton réseau", "One thing left: your network’s rules") : L("Tout est prêt", "All set"),
        changes > 0
            ? L($"Mira a vérifié ta clé avec Tailscale. Tes règles laisseraient un appareil du foyer voir tout ton réseau : Mira va y faire {Lang.Count(changes, "changement", "changements", "change", "changes")}. Tes commentaires restent.",
                $"Mira checked your key with Tailscale. Your rules would let a home device see your whole network, so Mira will make {Lang.Count(changes, "changement", "changements", "change", "changes")}. Your comments stay.")
            : L("Mira a vérifié ta clé et tes règles avec Tailscale : les appareils du foyer n’atteignent que Jellyfin.", "Mira checked your key and your rules with Tailscale: home devices reach only Jellyfin."),
        [], null, null,
        changes > 0 ? L("Rien n’est écrit avant ton clic. Tu peux annuler pendant 10 minutes.", "Nothing is written until you click. You can undo it for 10 minutes.") : null,
        changes > 0 ? MakeChanges(changes) : L("Terminé", "Done"));

    /// <summary>« Make these 2 changes », the button that writes the rules.</summary>
    public static string MakeChanges(int changes) => changes == 1 ? L("Faire ce changement", "Make this change") : L($"Faire ces {changes} changements", $"Make these {changes} changes");

    public static string Copy => L("Copier", "Copy");
    public static string Copied => L("Copié.", "Copied.");
    public static string FoundSecret => L("Un secret Tailscale que tu viens de copier", "Found a Tailscale secret you just copied");
    public static string UseIt => L("L’utiliser", "Use it");
    public static string PasteYourself => L("Ou colle-le toi-même :", "Or paste it yourself:");
    public static string SecretBox => L("Secret du client", "Client secret");
    public static string IdBox => L("Identifiant du client (Client ID)", "Client ID");
    public static string IdNeeded => L("Tailscale n’a pas reconnu l’identifiant tiré du secret : colle aussi le Client ID affiché juste au-dessus du secret.", "Tailscale didn’t take the ID read from the secret: paste the Client ID shown just above the secret too.");
    public static string Checking => L("Vérification avec Tailscale…", "Checking with Tailscale…");
    public static string SeeLines => L("Voir les lignes exactes", "See the exact lines");
    public static string HideLines => L("Cacher les lignes", "Hide the lines");
    public static string Fixing => L("Mira écrit tes règles…", "Mira is writing your rules…");

    /// <summary>Whether the clipboard holds what step 3 wants: a client's secret.</summary>
    public static bool LooksLikeSecret(string? text) => text is { Length: > 20 and < 256 } && text.Trim().StartsWith("tskey-client-", StringComparison.Ordinal) && !text.Trim().Contains(' ', StringComparison.Ordinal);

    /// <summary>A secret shown without its secret part: « tskey-client-kAbC12…•••••••• ».</summary>
    public static string Masked(string secret)
    {
        var text = secret.Trim();
        return text.Length <= 19 ? "tskey-client-…" : text[..19] + "…••••••••";
    }

    // —— Step 4: what Mira checked ——

    /// <summary>A line of the check: done, to do (a warning, fixed by the button) or wrong.</summary>
    public enum Mark { Ok, Warn, Bad }

    /// <summary>What Mira checked with Tailscale, line by line, from the extension's answer.</summary>
    public static IReadOnlyList<(Mark Mark, string Text)> Checks(HomeTailnetState state)
    {
        var lines = new List<(Mark, string)>();
        var refused = state.Policy is "refused";
        lines.Add((refused ? Mark.Bad : Mark.Ok, refused ? L("Tailscale ne reconnaît plus cette clé", "Tailscale no longer knows this key") : L("Tailscale accepte la clé", "Tailscale accepts the key")));
        if (refused) return lines;
        lines.Add(state.Broad ? (Mark.Bad, L("Cette clé peut trop de choses", "This key can do too much"))
            : state.Missing.Count > 0 || state.Policy == "scope" ? (Mark.Bad, Missing(state.Missing))
            : (Mark.Ok, L("Elle fait ce qu’il faut au foyer, et rien de plus", "It can do what the home needs, and no more")));
        lines.Add(state.Tag ? (Mark.Ok, L($"L’étiquette {TagName} est là", $"The {TagName} tag is there")) : (Mark.Warn, L($"L’étiquette {TagName} manque : Mira l’ajoute", $"The {TagName} tag is missing: Mira adds it")));
        var changes = state.Fix?.Count ?? 0;
        lines.Add(state.Policy == "ok" ? (Mark.Ok, L("Tes règles sont justes", "Your rules are right"))
            : changes > 0 ? (Mark.Warn, L($"Règles : {Lang.Count(changes, "changement", "changements", "change", "changes")} à faire", $"Rules: {Lang.Count(changes, "changement", "changements", "change", "changes")} to make"))
            : (Mark.Bad, L("Des règles que Mira ne sait pas corriger seule", "Rules Mira can’t fix by itself")));
        return lines;
    }

    /// <summary>One change Mira would make to the rules, in plain words.</summary>
    public static string Change(HomePolicyChange change) => change.Kind switch
    {
        "tag" => L($"Ajouter l’étiquette {TagName}.", $"Add the {TagName} tag."),
        "grant" => L("Ajouter une règle : les appareils du foyer joignent Jellyfin, rien d’autre.", "Add a rule: the home’s devices reach Jellyfin, nothing else."),
        "narrow" => L($"La règle {change.Number} laissait tout le monde aller partout : elle couvre maintenant tes propres appareils et les amis avec qui tu partages, pas ceux du foyer.",
            $"Rule {change.Number} let everyone go everywhere: it now covers your own devices and the friends you share with, not the home’s."),
        _ => change.Kind,
    };

    // —— When something is off: one sentence, one button that fixes it ——

    /// <summary>What fixes a trouble: redo step 2, open the credentials, fix the rules, start again, check again, open the policy.</summary>
    public enum Remedy { RedoKey, OpenCredentials, Fix, SeeLines, SetUpAgain, CheckAgain, OpenPolicy }

    /// <summary>Something off with the home's Tailscale, said plainly, and what fixes it.</summary>
    public sealed record Problem(string Title, string Text, IReadOnlyList<Remedy> Remedies);

    /// <summary>
    /// The trouble in the extension's verdict (<see cref="HomeTailnetState.Policy"/> or a refusal's code), with the scopes
    /// missing and the name of Jellyfin's PC; null when nothing is wrong.
    /// </summary>
    public static Problem? Trouble(string? code, IReadOnlyList<string>? missing = null, bool broad = false, int changes = 0, string? server = null)
    {
        if (broad || code == "too-broad")
            return new(L("Cette clé peut trop de choses", "This key can do too much"),
                L("Elle a été faite avec « All - Read & Write » : elle pourrait tout changer sur ton réseau, et elle serait gardée sur ton serveur. Mira ne s’en sert pas. Supprime-la dans Tailscale et fais-en une avec Custom scopes.",
                    "It was made with “All - Read & Write”: it could change anything on your network, and it would sit on your server. Mira won’t use it. Delete it in Tailscale and make one with Custom scopes."),
                [Remedy.RedoKey, Remedy.OpenCredentials]);
        if (code is "missing" or "scope" || missing is { Count: > 0 })
            return new(Missing(missing),
                L("Sans elle, Mira ne peut pas faire son travail. Tailscale ne modifie pas une clé déjà faite : fais-en une nouvelle, avec les trois lignes cochées.",
                    "Without it Mira can’t do its job. Tailscale can’t change a key once made: make a new one, with the three lines ticked."),
                [Remedy.RedoKey]);
        return code switch
        {
            "too-open" or "no-access" or "no-tag" when changes > 0 => new(L("Tes règles laissent les appareils du foyer voir tout ton réseau", "Your rules let home devices see your whole network"),
                L($"Mira peut corriger cela en {Lang.Count(changes, "changement", "changements", "change", "changes")}, sans toucher à tes commentaires.", $"Mira can fix that in {Lang.Count(changes, "changement", "changements", "change", "changes")}, keeping your comments."),
                [Remedy.Fix, Remedy.SeeLines]),
            "too-open" or "no-access" or "no-tag" or "invalid" => new(L("Des règles que Mira ne sait pas corriger seule", "Rules Mira can’t fix by itself"),
                L("Une règle laisse les appareils du foyer aller plus loin que Jellyfin d’une façon que Mira ne change pas sans toi. Ouvre tes règles dans Tailscale, ou demande de l’aide sur le Discord de Mira.",
                    "A rule lets home devices go further than Jellyfin in a way Mira won’t change without you. Open your rules in Tailscale, or ask for help on Mira’s Discord."),
                [Remedy.OpenPolicy, Remedy.CheckAgain]),
            "no-write" => new(L("Il manque une coche à cette clé : Policy File", "This key is missing one tick: Policy File"),
                L("Sans elle, Mira ne peut pas corriger tes règles. Tailscale ne modifie pas une clé déjà faite : fais-en une nouvelle, avec les trois lignes cochées.",
                    "Without it Mira can’t fix your rules. Tailscale can’t change a key once made: make a new one, with the three lines ticked."),
                [Remedy.RedoKey]),
            "changed" => new(L("Tes règles viennent de changer", "Your rules just changed"),
                L("Quelqu’un les a modifiées pendant que Mira les lisait. Vérifie de nouveau, puis corrige.", "Someone changed them while Mira was reading them. Check again, then fix them."),
                [Remedy.CheckAgain]),
            "refused" => new(L("Tailscale ne reconnaît plus cette clé", "Tailscale no longer knows this key"),
                L("Elle a été supprimée ou révoquée dans Tailscale. Plus de nouveaux liens du foyer tant que tu n’en donnes pas une autre à Mira. Les appareils déjà dans le foyer continuent de marcher.",
                    "It was deleted or revoked in Tailscale. Home links can’t be made until you give Mira a new one. Devices already in the home keep working."),
                [Remedy.SetUpAgain]),
            "no-tailscale" => new(L("Tailscale est éteint sur le PC de Jellyfin", "Tailscale is off on Jellyfin’s PC"),
                server is { Length: > 0 } ? L($"Allume-le sur {server}, puis vérifie de nouveau.", $"Turn it on on {server}, then check again.") : L("Allume-le sur le PC de Jellyfin, puis vérifie de nouveau.", "Turn it on on Jellyfin’s PC, then check again."),
                [Remedy.CheckAgain]),
            "unreachable" => new(L("Tailscale ne répond pas", "Tailscale isn’t answering"),
                L("Le PC de Jellyfin n’a pas pu joindre Tailscale. Réessaie dans un instant.", "Jellyfin’s PC couldn’t reach Tailscale. Try again in a moment."),
                [Remedy.CheckAgain]),
            _ => null,
        };
    }

    /// <summary>The label of a remedy's button.</summary>
    public static string Label(Remedy remedy) => remedy switch
    {
        Remedy.RedoKey => L("Refaire l’étape 2", "Redo step 2"),
        Remedy.OpenCredentials => L("Ouvrir les identifiants de Tailscale", "Open Tailscale’s credentials"),
        Remedy.Fix => L("Faire les changements", "Make the changes"),
        Remedy.SeeLines => SeeLines,
        Remedy.SetUpAgain => L("Régler de nouveau", "Set up again"),
        Remedy.CheckAgain => L("Vérifier de nouveau", "Check again"),
        Remedy.OpenPolicy => L("Ouvrir mes règles dans Tailscale", "Open my rules in Tailscale"),
        _ => remedy.ToString(),
    };

    /// <summary>« This key is missing one tick: Keys › Auth Keys », in the console's words.</summary>
    private static string Missing(IReadOnlyList<string>? missing)
    {
        var names = (missing ?? []).Select(Console).ToList();
        return names.Count switch
        {
            0 => L("Il manque des coches à cette clé", "This key is missing some ticks"),
            1 => L($"Il manque une coche à cette clé : {names[0]}", $"This key is missing one tick: {names[0]}"),
            _ => L($"Il manque des coches à cette clé : {string.Join(", ", names)}", $"This key is missing some ticks: {string.Join(", ", names)}"),
        };
    }

    /// <summary>A scope as the console names it.</summary>
    public static string Console(string scope) => scope switch
    {
        "auth_keys" => "Keys › Auth Keys",
        "devices:core" => "Devices › Core",
        "policy_file" => "Policy File",
        _ => scope,
    };

    // —— The drawings of the console ——

    /// <summary>A line of a drawing of the console: a field, a box with Read and Write, a choice, a button; numbered as the steps.</summary>
    public sealed record Sketch(SketchKind Kind, string Label, string? Value = null, bool On = false, bool Dim = false, int Marker = 0, string? Tag = null, bool Wrong = false);
    public enum SketchKind { Heading, Field, Choice, Scope, Button }

    /// <summary>The address bar and the lines of the console at a step (1 and 2; 3 shows where the secret is).</summary>
    public static (string Address, IReadOnlyList<Sketch> Lines) Drawing(int step) => step switch
    {
        1 => ("login.tailscale.com/admin/acls/visual",
            [new(SketchKind.Heading, "Access controls · Tags"), new(SketchKind.Button, "Create tag", Marker: 1),
             new(SketchKind.Field, "Tag name", "tag:" + TagName, Marker: 2), new(SketchKind.Field, "Tag owners", L("Laisse tel quel", "Leave as it is"), Dim: true),
             new(SketchKind.Button, "Save tag", Marker: 3)]),
        2 => ("login.tailscale.com/admin/settings/trust-credentials",
            [new(SketchKind.Heading, "New credential · OAuth"), new(SketchKind.Choice, "All - Read & Write", L("pas celle-ci", "not this one"), Wrong: true),
             new(SketchKind.Choice, "Custom scopes", On: true, Marker: 1),
             new(SketchKind.Scope, "Policy File", On: true), new(SketchKind.Scope, "Devices › Core", On: true, Tag: "tag:" + TagName),
             new(SketchKind.Scope, "Devices › Routes", Dim: true), new(SketchKind.Scope, "Keys › Auth Keys", On: true, Tag: "tag:" + TagName, Marker: 2),
             new(SketchKind.Scope, "Keys › OAuth Keys", Dim: true), new(SketchKind.Button, "Generate credential", Marker: 3)]),
        _ => ("login.tailscale.com/admin/settings/trust-credentials",
            [new(SketchKind.Heading, "Credential created"), new(SketchKind.Field, "Client ID", "kAbC12DeFG11CNTRL", Dim: true),
             new(SketchKind.Field, L("Client secret · montré une fois", "Client secret · shown once"), "tskey-client-kAbC12…-9fQ2…", Marker: 1),
             new(SketchKind.Heading, L("Seul le secret compte : l’identifiant est dedans.", "Only the secret is needed: the client ID is already inside it."))]),
    };
}
