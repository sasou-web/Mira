using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>
/// « Ajouter un ami » (Mira for Windows, Réglages → Jellyfin): an account of their own on this Jellyfin, which sees only
/// the libraries chosen and downloads nothing. Jellyfin makes a new account see every library and download (checked on
/// 12.1): its policy is read, changed here and sent back whole (JellyfinClient.CreateFriendAsync).
/// </summary>
public static partial class Friends
{
    /// <summary>Letters and digits easy to read out and to type on a phone: no 0 or o, no 1, l or i.</summary>
    public const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
    /// <summary>
    /// A password of four groups of four (« k7mq-t3vx-9hcw-p2ra », about 79 bits). With Quick Connect the friend never
    /// types it; never left empty: Jellyfin lets in an account without a password to anyone who knows its name.
    /// </summary>
    public static string NewPassword() =>
        string.Join('-', Enumerable.Range(0, 4).Select(_ => RandomNumberGenerator.GetString(Alphabet, 4)));
    // Jellyfin's own rule for a name (checked on 12.1): letters of any language, digits, spaces and - _ ' . @ +, no
    // space first or last; and no two accounts with the same name, whatever the case.
    [GeneratedRegex(@"^(?!\s)[\w\ \-'._@+]+(?<!\s)$")]
    private static partial Regex NameRule();
    /// <summary>A name as typed, as sent: spaces around it dropped, a typographic apostrophe made Jellyfin's.</summary>
    public static string CleanName(string? name) => (name ?? "").Trim().Replace('\u2019', '\'');
    /// <summary>What is wrong with a friend's name (cleaned first), in words; null when Jellyfin takes it.</summary>
    public static string? NameProblem(string? name, IEnumerable<string> taken)
    {
        var trimmed = CleanName(name);
        if (trimmed.Length == 0) return "Écris son prénom.";
        if (!NameRule().IsMatch(trimmed)) return "Son prénom ne peut contenir que des lettres, des chiffres, des espaces et ces signes : - _ ' . @ +";
        return taken.Any(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase)) ? $"Un compte « {trimmed} » existe déjà sur ton Jellyfin." : null;
    }
    /// <summary>The libraries a friend may be given, in the server's order: Collections and Playlists come with them.</summary>
    public static IReadOnlyList<VirtualFolder> Choices(IEnumerable<VirtualFolder> folders) =>
        [.. folders.Where(x => !string.IsNullOrEmpty(x.ItemId) && !LibrarySection.Groups(x.CollectionType))];
    /// <summary>
    /// The folders a friend sees: those chosen, and Jellyfin's Collections and Playlists, which only show them titles of
    /// those (a saga of films they cannot see stays hidden, checked on 12.1). None chosen, none at all.
    /// </summary>
    public static IReadOnlyList<string> Folders(IEnumerable<VirtualFolder> folders, IEnumerable<string> chosen)
    {
        var all = folders.Where(x => !string.IsNullOrEmpty(x.ItemId)).ToList();
        var picked = chosen.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mine = all.Where(x => !LibrarySection.Groups(x.CollectionType) && picked.Contains(x.ItemId!)).Select(x => x.ItemId!).ToList();
        return mine.Count == 0 ? [] : [.. mine, .. all.Where(x => LibrarySection.Groups(x.CollectionType)).Select(x => x.ItemId!)];
    }
    /// <summary>Libraries named as a sentence says them: « Films », « Films et Séries », « Animes, Films et Séries ».</summary>
    public static string Joined(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " et " + names[^1];
    /// <summary>
    /// A new account's policy, made a friend's: these folders only, no download (nor its conversions), no
    /// administration. Everything else is kept as Jellyfin set it, hidden from its sign-in screen and SyncPlay allowed.
    /// </summary>
    public static JsonObject Policy(JsonObject policy, IEnumerable<string> folders)
    {
        var friend = (JsonObject)policy.DeepClone();
        friend["IsAdministrator"] = false;
        friend["EnableAllFolders"] = false;
        friend["EnabledFolders"] = new JsonArray([.. folders.Select(x => (JsonNode?)JsonValue.Create(x))]);
        friend["EnableContentDownloading"] = false;
        friend["EnableSyncTranscoding"] = false;
        friend["EnableMediaConversion"] = false;
        return friend;
    }
}
