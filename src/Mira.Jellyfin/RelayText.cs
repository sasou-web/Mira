using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Mira.Core.TorLink;

namespace Mira.Jellyfin;

/// <summary>
/// What <see cref="TorLinkRelay"/> says, in the language of the request: French when its Accept-Language puts French
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
        ["Demande inconnue."] = "Unknown request.",
        ["La recherche n’a pas abouti."] = "The search didn’t go through.",
        ["TorLink a refusé ce téléchargement."] = "TorLink refused this download.",
        ["PC serveur"] = "Server PC",
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
    };

    public static RelaySearch For(RelaySearch search, bool french) => french ? search : search with { Error = For(search.Error, false) };
}
