using Mira.Core.Tailscale;

namespace Mira.Core.Family;

/// <summary>What Mira says of « Le foyer », the same words in every app.</summary>
public static class HomeText
{
    public static string NoExtension => L("Ce Jellyfin n’a pas de foyer : son extension Mira doit être à jour.", "This Jellyfin has no home: its Mira plugin needs updating.");
    public static string WhoIsWatching => L("Qui regarde ?", "Who’s watching?");
    public static string HomeOf(string name) => L($"Foyer {name}", $"{name}’s home");
    public static string Join(string name) => L($"Rejoindre le foyer {name}", $"Join {name}’s home");
    public static string Password => L("Mot de passe du foyer", "Home password");
    public static string FromLink(string name) => L($"Ouvert depuis le lien de {name}", $"Opened from {name}’s link");
    public static string FromLinkNote => L("Mira sait où est ce foyer, chez lui comme ailleurs. Rien d’autre à installer ni à taper.", "Mira knows where this home is, there and away from it. Nothing else to install or type.");
    public static string FoundHere => L("Trouvé sur ce réseau.", "Found on this network.");
    public static string Next => L("Ensuite : choisis ton profil. Cet appareil reste dans le foyer tant qu’on ne l’en retire pas.", "Next: pick your profile. This device stays in the home until it’s removed.");
    public static string OwnAccount => L("J’ai mon propre compte", "I have my own account");
    public static string Leave => L("Quitter ce foyer", "Leave this home");
    public static string PinOf(string name) => L($"Code de {name}", $"{name}’s PIN");
    public static string PinNote => L("Vérifié par l’extension Mira de Jellyfin, pas par cet appareil. 5 erreurs bloquent le profil 5 minutes.", "Checked by Jellyfin’s Mira plugin, not by this device. 5 wrong tries lock the profile for 5 minutes.");
    public static string PinLeft(int left) => left <= 0 ? L("Ce n’est pas le bon code.", "That’s not the right PIN.")
        : L($"Ce n’est pas le bon code. Encore {Lang.Count(left, "essai", "essais", "try", "tries")}.", $"That’s not the right PIN. {Lang.Count(left, "essai", "essais", "try", "tries")} left.");
    public static string PinWait(int seconds) => L($"Trop d’essais : réessaie dans {Math.Max(1, (seconds + 59) / 60)} min.", $"Too many tries: try again in {Math.Max(1, (seconds + 59) / 60)} min.");
    public static string NoProfiles => L("Aucun profil dans ce foyer pour l’instant : demande à son administrateur d’en cocher un dans Réglages → Foyer.", "No profiles in this home yet: ask its administrator to turn one on in Settings → Home.");
    public static string Unreachable(string name) => L($"Le foyer {name} ne répond pas pour l’instant. Réessaie dans un moment.", $"{name}’s home isn’t answering right now. Try again in a moment.");
    public static string NotThisServer(string name) => L($"Ce n’est pas le Jellyfin de {name} : le lien ne correspond plus. Demande-lui un nouveau lien.", $"This isn’t {name}’s Jellyfin: the link no longer matches. Ask for a new link.");
    public static string LinkExpired(string name) => L($"Ce lien a expiré (48 h). Demande un nouveau lien du foyer à {name}.", $"This link has expired (48 h). Ask {name} for a new home link.");
    public static string LinkUsed(string name) => L($"Ce lien a déjà servi ou a été retiré. Demande un nouveau lien du foyer à {name}.", $"This link was already used or taken back. Ask {name} for a new home link.");
    public static string LinkBroken => L("Ce lien du foyer est incomplet : recopie-le en entier, ou demande-en un nouveau.", "This home link is incomplete: copy all of it, or ask for a new one.");
    public static string Reaching(string name) => L($"Connexion au réseau du foyer {name}…", $"Connecting to {name}’s home network…");
    public static string NoHelper => L("Mira ne peut pas joindre ce foyer depuis ce PC : son Tailscale intégré manque ou est bloqué.", "Mira can’t reach this home from this PC: its built-in Tailscale is missing or blocked.");
    public static string Removed(string name) => L($"Cet appareil n’est plus dans le foyer {name}. Pour revenir, demande un nouveau lien du foyer.", $"This device is no longer in {name}’s home. To come back, ask for a new home link.");
    public static string Left(string name) => L($"Cet appareil a quitté le foyer {name}.", $"This device left {name}’s home.");
    public static string LeftHere(string name) => L($"Cet appareil a quitté le foyer {name} ici ; le serveur ne l’a pas su : son administrateur peut le retirer dans Réglages → Foyer.", $"This device left {name}’s home here; the server didn’t hear it: its administrator can remove it in Settings → Home.");
    public static string Owner => L("Propriétaire", "Owner");
    public static string ThisDevice => L("Sur cet appareil", "On this device");

    /// <summary>The name of this device in the home's list of devices.</summary>
    public static string DeviceName(string app, string machine) => L($"{app} sur {machine}", $"{app} on {machine}");

    /// <summary>What the policy's verdict means for the administrator (GET Mira/home/tailnet), in a line.</summary>
    public static string Policy(string? policy) => policy switch
    {
        "ok" => L("Prêt : les appareils du foyer n’atteignent que Jellyfin sur ton réseau Tailscale.", "Ready: the home’s devices reach only Jellyfin on your Tailscale network."),
        "no-tag" => L("L’étiquette tag:mira-home n’est pas encore dans ta politique Tailscale (tagOwners).", "The tag:mira-home tag isn’t in your Tailscale policy yet (tagOwners)."),
        "too-open" => L("Ta politique laisse les appareils du foyer aller ailleurs que sur Jellyfin.", "Your policy lets the home’s devices go beyond Jellyfin."),
        "no-access" => L("Aucune règle ne laisse encore les appareils du foyer joindre Jellyfin.", "No rule lets the home’s devices reach Jellyfin yet."),
        "no-write" => L("Le client OAuth du foyer ne peut pas écrire la politique : crée-en un avec le droit policy_file, puis relie de nouveau le foyer.", "The home’s OAuth client can’t write the policy: create one with the policy_file scope, then connect the home again."),
        "changed" => L("Ta politique Tailscale vient de changer : vérifie à nouveau, puis corrige.", "Your Tailscale policy just changed: check again, then fix it."),
        "invalid" => L("Tailscale a refusé la politique corrigée : rien n’a changé. Corrige-la dans la console de Tailscale.", "Tailscale refused the fixed policy: nothing changed. Fix it in Tailscale’s console."),
        "refused" => L("Tailscale refuse le client OAuth du foyer : il a été supprimé ou révoqué. Relie de nouveau le foyer.", "Tailscale refuses the home’s OAuth client: it was deleted or revoked. Connect the home again."),
        "scope" => L("Le client OAuth n’a pas les droits qu’il faut (auth_keys, devices:core, policy_file, et l’étiquette tag:mira-home).", "The OAuth client lacks the rights it needs (auth_keys, devices:core, policy_file, and the tag:mira-home tag)."),
        "no-tailscale" => L("Tailscale n’est pas allumé sur le PC de Jellyfin.", "Tailscale isn’t on on Jellyfin’s PC."),
        "unreachable" => L("Tailscale n’a pas répondu. Réessaie dans un instant.", "Tailscale didn’t answer. Try again in a moment."),
        "disconnected" => L("Relie d’abord le foyer à ton réseau Tailscale.", "First connect the home to your Tailscale network."),
        _ => L("Le foyer n’est pas relié à ton réseau Tailscale : il ne marche qu’à la maison.", "The home isn’t connected to your Tailscale network: it works only at home."),
    };

    /// <summary>The client connected has more rights than the home needs (<see cref="HomeTailnetState.Broad"/>).</summary>
    public static string Broad => L("Le client OAuth du foyer a tous les droits sur ton réseau Tailscale, et son secret est gardé sur le serveur : remplace-le par un client limité à Policy File, Devices → Core et Keys → Auth Keys (Déconnecter, puis les étapes).",
        "The home’s OAuth client has every right on your Tailscale network, and its secret is kept on the server: replace it with one limited to Policy File, Devices → Core and Keys → Auth Keys (Disconnect, then the steps).");

    /// <summary>Above the changes Mira can make to the policy itself.</summary>
    public static string FixAbout => L("Mira peut corriger ta politique elle-même, sans toucher à tes commentaires :", "Mira can fix your policy itself, keeping your comments:");
    public static string FixButton => L("Corriger ma politique", "Fix my policy");
    public static string Fixing => L("Correction de la politique…", "Fixing the policy…");

    /// <summary>One change Mira would make to the policy (<see cref="HomePolicyChange"/>), in a line.</summary>
    public static string Change(HomePolicyChange change) => change.Kind switch
    {
        "tag" => L("Ajouter l’étiquette tag:mira-home à « tagOwners ».", "Add the tag:mira-home tag to “tagOwners”."),
        "grant" => L("Ajouter une règle : les appareils du foyer joignent Jellyfin, rien d’autre.", "Add a rule: the home’s devices reach Jellyfin, nothing else."),
        "narrow" => L($"Règle {change.Number} de « {change.Section} » : ses sources deviennent {string.Join(", ", change.Sources ?? [])} (tes appareils, tes amis partagés, tes autres étiquettes).",
            $"Rule {change.Number} of “{change.Section}”: its sources become {string.Join(", ", change.Sources ?? [])} (your devices, your shared friends, your other tags)."),
        _ => change.Kind,
    };

    /// <summary>Step 1 of the setup: the tag, made in Tailscale's visual editor.</summary>
    public static string StepTag => L("Dans Tailscale, Access controls → Tags → Create tag : nomme-la mira-home, puis Save tag.", "In Tailscale, Access controls → Tags → Create tag: name it mira-home, then Save tag.");
    /// <summary>Step 2 of the setup: the OAuth client.</summary>
    public static string StepClient(string scopes, string tag) => L(
        $"Crée un client OAuth en « Custom scopes », pas « All » : Policy File, Devices → Core et Keys → Auth Keys, en lecture et écriture, avec l’étiquette {tag} ({scopes}). Mira refuse un client qui a plus de droits.",
        $"Create an OAuth client with “Custom scopes”, not “All”: Policy File, Devices → Core and Keys → Auth Keys, read and write, with the tag {tag} ({scopes}). Mira refuses a client with more rights.");
    /// <summary>Step 3 of the setup: the client pasted; Mira puts the policy right after.</summary>
    public static string StepPaste => L("Colle son identifiant et son secret. Mira garde le secret sur le serveur, jamais sur cet appareil, puis te propose de corriger la politique.", "Paste its ID and secret. Mira keeps the secret on the server, never on this device, then offers to fix the policy.");
    public static string OpenTags => L("Ouvrir les étiquettes de Tailscale", "Open Tailscale’s tags");
    /// <summary>The visual editor's tags, where step 1 is done.</summary>
    public const string TagsPage = "https://login.tailscale.com/admin/acls/visual";

    /// <summary>Why the home's node cannot take this device in, from its state (<see cref="TailnetCodes"/>).</summary>
    public static string Node(string code, string name) => code switch
    {
        TailnetCodes.NeedsLogin or TailnetCodes.Expired => LinkUsed(name),
        TailnetCodes.Helper or TailnetCodes.Version or TailnetCodes.Key or TailnetCodes.Protocol => NoHelper,
        TailnetCodes.Offline => L("Pas de connexion à Internet pour l’instant.", "No Internet connection right now."),
        _ => Unreachable(name),
    };
}
