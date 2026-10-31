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
        ["Trop de demandes attendent déjà le PC serveur."] = "Too many requests are already waiting for the server PC.",
        ["Mira sur le PC serveur ne sait pas encore remplacer un titre : mets-le à jour."] = "Mira on the server PC can’t replace a title yet: update it.",
        ["Ce fichier est déjà demandé au PC serveur."] = "This file has already been asked of the server PC.",
        ["Une meilleure version de ce titre est déjà demandée."] = "A better version of this title is already requested.",
        ["Demande inconnue."] = "Unknown request.",
        ["La recherche n’a pas abouti."] = "The search didn’t go through.",
        ["TorLink a refusé ce téléchargement."] = "TorLink refused this download.",
        ["PC serveur"] = "Server PC",
        // « Suivre une série ».
        ["Série inconnue."] = "Unknown series.",
        ["Trop de séries suivies : arrête d’en suivre une d’abord."] = "Too many series followed: stop following one first.",
        // « Inviter par lien » (GuestController, InvitesController).
        ["Trop d’essais : attends une minute."] = "Too many tries: wait a minute.",
        ["Ce lien ne marche plus : demande-en un nouveau à la personne qui t’a invité."] = "This link no longer works: ask the person who invited you for a new one.",
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
    };

    public static List<RelayFollow> For(List<RelayFollow> follows, bool french) => french ? follows
        : follows.Select(x => x with { Picks = x.Picks.Select(p => p with { Error = For(p.Error, false) }).ToList() }).ToList();

    public static RelaySearch For(RelaySearch search, bool french) => french ? search : search with { Error = For(search.Error, false) };
}
