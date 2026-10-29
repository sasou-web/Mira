using System.Text.Json.Nodes;
using static Mira.Core.Lang;

namespace Mira.Core;

/// <summary>
/// Jellyfin's official TheTVDB plugin, used by Mira for one thing only: its « Missing Episode Fetcher », which adds the
/// episodes a series has on TheTVDB but not in the library as virtual ones, with their air date. Mira turns its
/// metadata and image fetchers off: TheTVDB never becomes a source of names, summaries or pictures. Only the Jellyfin
/// server talks to TheTVDB; the episode lists of each series it knows the TheTVDB id of, at each library scan.
/// </summary>
public static class TvdbExtension
{
    public static readonly Guid PluginId = new("a677c0da-fac5-4cde-941a-7134223f14c8");
    public const string PluginName = "TheTVDB";
    /// <summary>The name Jellyfin gates the virtual episodes by, as it does any metadata fetcher of a library.</summary>
    public const string MissingEpisodes = "Missing Episode Fetcher";
    /// <summary>Jellyfin's own plugin repository (the one it registers at its first start since 10.10).</summary>
    public const string Repository = "https://repo.jellyfin.org/files/plugin/manifest.json";
    /// <summary>The item types the plugin may fetch for: its fetchers named « TheTVDB » are turned off for each.</summary>
    public static readonly string[] ItemTypes = ["Series", "Season", "Episode", "Movie", "Person", "BoxSet"];

    public enum State { Absent, Restart, Active, Off }

    /// <summary>The plugin as Jellyfin lists it (GET Plugins): an update waiting for a restart counts as waiting.</summary>
    public static State Read(JsonArray plugins)
    {
        var states = plugins.OfType<JsonObject>()
            .Where(x => x["Id"] is JsonValue id && id.TryGetValue<string>(out var text) && Guid.TryParse(text, out var guid) && guid == PluginId)
            .Select(x => x["Status"] is JsonValue value && value.TryGetValue<string>(out var status) ? status : "").ToList();
        if (states.Contains("Restart")) return State.Restart;
        if (states.Contains("Active")) return State.Active;
        return states.Any(x => x is "Disabled" or "NotSupported" or "Malfunctioned") ? State.Off : State.Absent;
    }

    /// <summary>Jellyfin's own repository among <paramref name="repositories"/>, under any of its addresses; null without it.</summary>
    public static string? OfficialRepository(JsonArray repositories) => repositories.OfType<JsonObject>()
        .Select(x => x["Url"] is JsonValue url && url.TryGetValue<string>(out var text) ? text : null)
        .FirstOrDefault(x => Uri.TryCreate(x, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "repo.jellyfin.org");

    /// <summary>
    /// Jellyfin's server settings with the plugin's metadata and image fetchers (« TheTVDB ») turned off for every item
    /// type it serves, the « Missing Episode Fetcher » left on. True when anything changed.
    /// </summary>
    public static bool KeepToMissingEpisodes(JsonObject configuration)
    {
        if (configuration["MetadataOptions"] is not JsonArray options) configuration["MetadataOptions"] = options = [];
        var changed = false;
        foreach (var type in ItemTypes)
        {
            var entry = options.OfType<JsonObject>().FirstOrDefault(x => x["ItemType"] is JsonValue value && value.TryGetValue<string>(out var name) && name == type);
            if (entry is null)
            {
                options.Add(entry = new JsonObject { ["ItemType"] = type, ["DisabledMetadataSavers"] = new JsonArray(), ["LocalMetadataReaderOrder"] = new JsonArray(), ["DisabledMetadataFetchers"] = new JsonArray(), ["MetadataFetcherOrder"] = new JsonArray(), ["DisabledImageFetchers"] = new JsonArray(), ["ImageFetcherOrder"] = new JsonArray() });
                changed = true;
            }
            changed |= Add(entry, "DisabledMetadataFetchers", PluginName);
            changed |= Add(entry, "DisabledImageFetchers", PluginName);
            changed |= Remove(entry, "DisabledMetadataFetchers", MissingEpisodes);
        }
        return changed;
    }

    /// <summary>
    /// A series library's options with the « Missing Episode Fetcher » listed for series and seasons when the library
    /// lists its fetchers itself (TypeOptions): Jellyfin then runs only the ones listed. A library without them follows
    /// the server settings (<see cref="KeepToMissingEpisodes"/>). True when anything changed.
    /// </summary>
    public static bool ListMissingEpisodes(JsonObject libraryOptions)
    {
        if (libraryOptions["TypeOptions"] is not JsonArray types) return false;
        var changed = false;
        foreach (var entry in types.OfType<JsonObject>())
            if (entry["Type"] is JsonValue value && value.TryGetValue<string>(out var type) && type is "Series" or "Season")
                changed |= Add(entry, "MetadataFetchers", MissingEpisodes);
        return changed;
    }

    /// <summary>What goes to TheTVDB, said before turning it on and beside the setting.</summary>
    public static string Privacy => L(
        "Ton serveur Jellyfin (pas Mira, pas ton téléphone) demande à TheTVDB la liste des épisodes de chaque série qu’il connaît, par son identifiant TheTVDB, à chaque analyse de la bibliothèque (toutes les 12 h) : TheTVDB voit l’adresse IP du serveur et les séries demandées. Mira coupe ses noms, résumés et images : tes fiches restent celles de TMDb.",
        "Your Jellyfin server (not Mira, not your phone) asks TheTVDB for the episode list of each series it knows, by its TheTVDB id, at each library scan (every 12 h): TheTVDB sees the server’s IP address and the series asked for. Mira turns off its names, summaries and pictures: your titles keep TMDb’s.");

    private static bool Add(JsonObject entry, string list, string name)
    {
        if (entry[list] is not JsonArray names) entry[list] = names = [];
        if (names.Any(x => x is JsonValue value && value.TryGetValue<string>(out var text) && string.Equals(text, name, StringComparison.OrdinalIgnoreCase))) return false;
        names.Add(name);
        return true;
    }

    private static bool Remove(JsonObject entry, string list, string name)
    {
        if (entry[list] is not JsonArray names) return false;
        var found = names.Where(x => x is JsonValue value && value.TryGetValue<string>(out var text) && string.Equals(text, name, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var item in found) names.Remove(item);
        return found.Count > 0;
    }
}
