using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mira.Core;

/// <summary>
/// Collections made by hand (« Soirées », « À montrer à Léa »), beside the sagas Jellyfin makes (web, Windows, Mac; the
/// rules shared with Mira web's collections.js in tests/fixtures/collections.json). Both are Jellyfin's collections: a
/// list of titles kept in its data folder (« Soirées [boxset] »), never a file moved or copied; deleting one leaves its
/// titles where they are. Jellyfin lets an administrator make, fill and delete them, or an account given « Gérer les
/// collections » (EnableCollectionManagement); only an administrator renames one (Items/{id}).
/// </summary>
public static class TitleCollections
{
    /// <summary>The longest name Mira gives a collection.</summary>
    public const int MaxName = 100;

    /// <summary>Why a name is refused (<see cref="NameProblem"/>): <see cref="Taken"/> names the collection that has it.</summary>
    public enum Problem { Empty, TooLong, Unusable, Taken }
    public sealed record Refusal(Problem Problem, string? Taken = null)
    {
        public string Text => Problem switch
        {
            Problem.Empty => L("Donne un nom à la collection.", "Give the collection a name."),
            Problem.TooLong => L($"Ce nom est trop long : {MaxName} caractères au plus.", $"This name is too long: {MaxName} characters at most."),
            Problem.Unusable => L("Ce nom ne peut pas servir : mets-y des lettres ou des chiffres.", "This name can’t be used: put letters or numbers in it."),
            _ => L($"Ce nom est déjà pris par la collection « {Taken} ».", $"This name is already taken by the collection “{Taken}”."),
        };
    }

    /// <summary>What this account may do with collections: make, fill, empty and delete them; rename them.</summary>
    public sealed record Rights(bool Manage, bool Rename)
    {
        public static readonly Rights None = new(false, false);
        /// <summary>From the policy of Users/Me: an administrator, or an account allowed to manage collections (Jellyfin 10.9 and later).</summary>
        public static Rights From(JsonElement policy)
        {
            bool Flag(string name) => policy.ValueKind == JsonValueKind.Object && policy.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
            var admin = Flag("IsAdministrator");
            return new(admin || Flag("EnableCollectionManagement"), admin);
        }
    }

    /// <summary>The titles a collection takes from a card's menu or a page: films and series.</summary>
    public static bool Holds(MediaItem item) => item.Type is "Movie" or "Series";

    /// <summary>A name as typed, without the spaces around it.</summary>
    public static string Clean(string? name) => (name ?? "").Trim();

    /// <summary>
    /// Why a new name is refused, or null. Jellyfin keeps a collection in a folder named after it, its forbidden
    /// characters (« / : * ? " &lt; &gt; | \ ») made spaces, and a second collection of the same folder takes the place of
    /// the first, which loses its titles (checked on 12.1): a name is refused when its folder, or its name, is already
    /// another collection's, whatever the case (Windows does not tell « soirées » from « Soirées »). A collection renamed
    /// keeps its first folder: its old name stays taken. <paramref name="renamed"/>: the id of the one being renamed,
    /// which may keep its own.
    /// </summary>
    public static Refusal? NameProblem(string? name, IEnumerable<MediaItem> collections, string? renamed = null)
    {
        var clean = Clean(name);
        if (clean.Length == 0) return new(Problem.Empty);
        if (clean.Length > MaxName) return new(Problem.TooLong);
        var key = Key(clean);
        if (key.Trim().Length == 0) return new(Problem.Unusable);
        foreach (var other in collections)
        {
            if (renamed is not null && string.Equals(other.Id, renamed, StringComparison.OrdinalIgnoreCase)) continue;
            if (Key(other.Name) == key || Folder(other.Path) is { } folder && Key(folder) == key) return new(Problem.Taken, other.Name);
        }
        return null;
    }

    /// <summary>The name of a collection's folder without « [boxset] » (the last part of its path, from Windows or not); null without one.</summary>
    public static string? Folder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var last = path.TrimEnd('/', '\\').Split('/', '\\')[^1];
        const string suffix = " [boxset]";
        return last.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? last[..^suffix.Length] : last;
    }

    /// <summary>What two names that would share a folder have in common: Jellyfin's forbidden characters made spaces, lower case.</summary>
    public static string Key(string name)
    {
        var text = new StringBuilder(name.Normalize(NormalizationForm.FormC));
        for (var i = 0; i < text.Length; i++) if (text[i] < 32 || "\"<>|:*?\\/".Contains(text[i])) text[i] = ' ';
        return text.ToString().ToLowerInvariant();
    }

    /// <summary>Collections by name, as a menu lists them.</summary>
    public static List<MediaItem> Sorted(IEnumerable<MediaItem> collections) =>
        collections.OrderBy(x => x.Name, StringComparer.Create(Culture, CompareOptions.IgnoreCase)).ToList();

    /// <summary>« Ajouté à « Soirées ». », « Retiré de « Soirées ». »</summary>
    public static string Added(string collection) => L($"Ajouté à « {collection} ».", $"Added to “{collection}”.");
    public static string Removed(string collection) => L($"Retiré de « {collection} ».", $"Removed from “{collection}”.");
    /// <summary>What a collection's deletion takes, asked before it: never its titles.</summary>
    public static string DeleteQuestion(string collection) => L($"Supprimer la collection « {collection} » ? Ses titres restent dans ta bibliothèque.",
        $"Delete the collection “{collection}”? Its titles stay in your library.");
    public static string Deleted(string collection) => L($"Collection « {collection} » supprimée. Ses titres restent dans ta bibliothèque.", $"Collection “{collection}” deleted. Its titles stay in your library.");
    /// <summary>Said when Jellyfin refuses: this account may not manage collections.</summary>
    public static string Refused => L("Ton compte Jellyfin n’a pas le droit de gérer les collections : son administrateur peut le lui donner (Tableau de bord → Utilisateurs).",
        "Your Jellyfin account isn’t allowed to manage collections: its administrator can allow it (Dashboard → Users).");
}
