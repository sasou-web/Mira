using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Mira.Core.TorLink;

namespace Mira.Jellyfin;

/// <summary>
/// What <see cref="TorLinkRelay"/> and the guests' links (<see cref="GuestController"/>) say, in the language of the request: French when its Accept-Language puts French
/// before English (Mira web sends the language it shows, Mira for Windows its own), English otherwise. The relay writes
/// French; what Mira for Windows reports (TorLink's own errors) is passed on as it came, in that app's language.
/// </summary>
internal static class RelayText
{
    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["Écris ce que tu cherches."] = "Type what you’re looking for.",
        ["Choisis Films, Séries ou Animes."] = "Choose Movies, Shows or Anime.",
        ["Mira n’est pas ouvert sur le PC serveur : c’est lui qui cherche, avec TorLink."] = "Mira isn’t open on the server PC: that’s where the search runs, with TorLink.",
        ["Ce résultat n’est plus là : cherche de nouveau."] = "This result is gone: search again.",
        ["Ce n’est pas un lien magnet (« magnet:?xt=urn:btih:… »)."] = "That’s not a magnet link (“magnet:?xt=urn:btih:…”).",
        ["Ce n’est pas un fichier .torrent."] = "That’s not a .torrent file.",
        ["Le PC serveur n’a pas encore qBittorrent : installe-le dans la page Téléchargements de Mira, sur ce PC."] = "The server PC doesn’t have qBittorrent yet: install it from Mira’s Downloads page, on that PC.",
        ["Trop de demandes attendent déjà le PC serveur."] = "Too many requests are already waiting for the server PC.",
        ["Mira sur le PC serveur ne sait pas encore remplacer un titre : mets-le à jour."] = "Mira on the server PC can’t replace a title yet: update it.",
        ["Ce fichier est déjà demandé au PC serveur."] = "This file has already been asked of the server PC.",
        ["Mira sur le PC serveur ne sait pas encore ranger un épisode manquant : mets-le à jour."] = "Mira on the server PC can’t place a missing episode yet: update it.",
        ["Une meilleure version de ce titre est déjà demandée."] = "A better version of this title is already requested.",
        ["Demande inconnue."] = "Unknown request.",
        ["La recherche n’a pas abouti."] = "The search didn’t go through.",
        ["TorLink a refusé ce téléchargement."] = "TorLink refused this download.",
        // « Coller un lien magnet ».
        ["Mira sur le PC serveur ne sait pas encore choisir les fichiers d’un lien : mets-le à jour."] = "Mira on the server PC can’t choose a link’s files yet: update it.",
        ["Le PC serveur n’a pas pu lire ce lien."] = "The server PC couldn’t read this link.",
        ["PC serveur"] = "Server PC",
        // « Suivre une série ».
        ["Série inconnue."] = "Unknown series.",
        ["Trop de séries suivies : arrête d’en suivre une d’abord."] = "Too many series followed: stop following one first.",
        // « Inviter par lien » (GuestController, InvitesController).
        ["Trop d’essais : attends une minute."] = "Too many tries: wait a minute.",
        ["Ce lien ne marche plus : demande-en un nouveau à la personne qui t’a invité."] = "This link no longer works: ask the person who invited you for a new one.",
        ["Ce lien ne marche plus ou la party est pleine : demande à la personne qui t’a invité."] = "This link no longer works or the party is full: ask the person who invited you.",
        ["Ce lien sert déjà sur un autre appareil : demande-en un nouveau à la personne qui t’a invité."] = "This link is already in use on another device: ask the person who invited you for a new one.",
        ["Jellyfin n’a pas ouvert la séance. Réessaie dans un instant."] = "Jellyfin didn’t open the session. Try again in a moment.",
        ["Trop de liens en cours : retire ceux qui ne servent plus."] = "Too many links at once: remove those no longer in use.",
        ["Jellyfin n’a pas répondu. Réessaie dans un instant."] = "Jellyfin didn’t answer. Try again in a moment.",
        ["Son prénom ne peut contenir que des lettres, des chiffres, des espaces et ces signes : - _ ' . @ +"] = "Their name can only contain letters, digits, spaces and these signs: - _ ' . @ +",
        ["Jellyfin n’a pas créé le compte de l’invité."] = "Jellyfin didn’t create the guest’s account.",
        ["L’adresse doit commencer par https:// et finir par le nom de l’ordinateur."] = "The address must start with https:// and end with the computer’s name.",
        // « Ta party » (PartyController, GuestController).
        ["Cette party est pleine : demande à la personne qui t’a invité de faire de la place."] = "This party is full: ask the person who invited you to make room.",
        ["Un administrateur de ce Jellyfin doit d’abord ouvrir Regarder ensemble dans Mira une fois."] = "An administrator of this Jellyfin must first open Watch together in Mira once.",
        ["Un invité ne peut pas inviter."] = "A guest can’t invite others.",
        ["Ce lien ouvre ta party depuis Internet, pour tout le monde."] = "This link opens your party from the Internet, for everyone.",
        ["Ce lien ne marche que pour ceux qui ont Tailscale sur ton réseau : ouvre Tailscale Funnel sur le PC de Jellyfin (Mira pour Windows le fait) pour qu’il marche partout."] = "This link only works for people on your Tailscale network: open Tailscale Funnel on the Jellyfin PC (Mira for Windows does it) so it works everywhere.",
        ["Ce lien ne marche qu’à la maison : ouvre Tailscale Funnel sur le PC de Jellyfin (Mira pour Windows le fait) pour qu’il marche partout."] = "This link only works at home: open Tailscale Funnel on the Jellyfin PC (Mira for Windows does it) so it works everywhere.",
        // « Le foyer » (HomeController).
        ["Ce Jellyfin n’a pas de foyer."] = "This Jellyfin has no home.",
        ["Trop d’essais : attends quelques minutes."] = "Too many tries: wait a few minutes.",
        ["Ce n’est pas le mot de passe du foyer."] = "That’s not the home password.",
        ["Le foyer a déjà trop d’appareils : demande d’en retirer un."] = "The home already has too many devices: ask for one to be removed.",
        ["Un administrateur de ce Jellyfin doit d’abord ouvrir les réglages du foyer dans Mira."] = "An administrator of this Jellyfin must first open the home settings in Mira.",
        ["Ce profil n’est pas dans le foyer."] = "This profile isn’t in the home.",
        ["Ce profil a un code."] = "This profile has a PIN.",
        ["Ce n’est pas le bon code."] = "That’s not the right PIN.",
        ["Le code fait quatre chiffres."] = "The PIN is four digits.",
        ["Le mot de passe du foyer fait au moins 8 caractères."] = "The home password is at least 8 characters.",
        ["Ce compte n’existe pas sur ce Jellyfin."] = "This account doesn’t exist on this Jellyfin.",
        ["Un profil administrateur a besoin d’un code à quatre chiffres."] = "An administrator’s profile needs a four-digit PIN.",
        ["Un profil administrateur garde toujours son code."] = "An administrator’s profile always keeps its PIN.",
        ["Le foyer a déjà trop de profils."] = "The home already has too many profiles.",
        ["Cet appareil n’est plus dans le foyer : entre de nouveau son mot de passe."] = "This device is no longer in the home: enter its password again.",
        ["Colle l’identifiant et le secret du client OAuth que Tailscale vient de te donner."] = "Paste the ID and secret of the OAuth client Tailscale just gave you.",
        ["Tailscale refuse ce client OAuth : vérifie l’identifiant et le secret."] = "Tailscale refuses this OAuth client: check the ID and secret.",
        ["Tailscale n’a pas répondu. Réessaie dans un instant."] = "Tailscale didn’t answer. Try again in a moment.",
        ["Choisis d’abord le mot de passe du foyer."] = "Choose the home password first.",
        ["Relie d’abord le foyer à ton réseau Tailscale."] = "Connect the home to your Tailscale network first.",
        ["Tailscale n’est pas allumé sur le PC de Jellyfin."] = "Tailscale isn’t on on the Jellyfin PC.",
        ["Ta politique Tailscale laisse les appareils du foyer aller ailleurs que sur Jellyfin : corrige-la d’abord."] = "Your Tailscale policy lets the home’s devices reach more than Jellyfin: fix it first.",
        ["Tailscale refuse le client OAuth du foyer : il a été supprimé ou révoqué. Relie de nouveau le foyer."] = "Tailscale refuses the home’s OAuth client: it was deleted or revoked. Connect the home again.",
        ["Le client OAuth du foyer n’a pas les droits qu’il faut (auth_keys, devices:core, policy_file et l’étiquette tag:mira-home)."] = "The home’s OAuth client lacks what it needs (auth_keys, devices:core, policy_file and the tag:mira-home tag).",
        ["Ce client OAuth a plus de droits qu’il n’en faut : supprime-le, puis crée-en un avec seulement Policy File, Devices Core et Auth Keys (étiquette tag:mira-home)."] = "This OAuth client has more rights than it needs: delete it, then create one with only Policy File, Devices Core and Auth Keys (tag tag:mira-home).",
        ["Il manque un droit à ce client OAuth : crée-en un autre avec Policy File, Devices Core et Auth Keys (étiquette tag:mira-home)."] = "This OAuth client is missing a right: create another one with Policy File, Devices Core and Auth Keys (tag tag:mira-home).",
        ["Il est trop tard pour annuler : change ta politique dans la console de Tailscale."] = "It’s too late to undo: change your policy in Tailscale’s console.",
        ["Ta politique a changé depuis : Mira ne l’annule pas. Change-la dans la console de Tailscale."] = "Your policy changed since: Mira won’t undo it. Change it in Tailscale’s console.",
        ["Mira ne sait pas corriger ces règles seule : restreins-les dans la politique Tailscale."] = "Mira can’t fix these rules by itself: narrow them in the Tailscale policy.",
        ["Le client OAuth du foyer ne peut pas écrire la politique : crée-en un avec le droit policy_file, puis relie de nouveau le foyer."] = "The home’s OAuth client can’t write the policy: create one with the policy_file scope, then connect the home again.",
        ["Ta politique Tailscale vient de changer : vérifie à nouveau, puis corrige."] = "Your Tailscale policy just changed: check again, then fix it.",
        ["Tailscale a refusé la politique corrigée : rien n’a changé. Corrige-la dans la console de Tailscale."] = "Tailscale refused the fixed policy: nothing changed. Fix it in Tailscale’s console.",
    };

    /// <summary>Whether the request asks for French: the first of French and English among its languages, by preference.</summary>
    public static bool French(HttpRequest request)
    {
        var languages = request.GetTypedHeaders().AcceptLanguage;
        foreach (var language in languages.OrderByDescending(x => x.Quality ?? 1))
        {
            var code = language.Value.Value?.Split('-', '_')[0];
            if (string.Equals(code, "fr", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(code, "en", StringComparison.OrdinalIgnoreCase)) return false;
        }

        return false;
    }

    /// <summary>One of the relay's texts in the language asked; any other text as it is.</summary>
    public static string? For(string? text, bool french) =>
        french || text is null ? text : English.GetValueOrDefault(text, text);

    public static RelayStatus For(RelayStatus status, bool french) => french ? status : status with
    {
        Agent = status.Agent is { } agent ? agent with { Name = For(agent.Name, false)! } : null,
        Requests = status.Requests.Select(x => x with { Error = For(x.Error, false) }).ToList(),
        Follows = status.Follows is { } follows ? For(follows, false) : null,
        Missing = status.Missing?.Select(x => x with { Error = For(x.Error, false) }).ToList(),
    };

    public static List<RelayFollow> For(List<RelayFollow> follows, bool french) => french ? follows
        : follows.Select(x => x with { Picks = x.Picks.Select(p => p with { Error = For(p.Error, false) }).ToList() }).ToList();

    public static RelaySearch For(RelaySearch search, bool french) => french ? search : search with { Error = For(search.Error, false) };
}
