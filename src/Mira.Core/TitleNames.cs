using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>
/// A title's name as Jellyfin gives it, without what a download tool put before its file's name: a film named after
/// its file (alone in a folder with others, not identified) keeps that file's name, and some tools save a file as
/// « 0c76ce5c44cd079b008af19790bb4cc3_The Nightmare Before Christmas 1993.mkv » (a friend of ogsas, 2026-11-19: the
/// name with its code in « À reprendre » and on Discord). A run of 32 or 40 hexadecimal digits and « _ » before the
/// name is never part of a title: it goes, in every app (MediaItem.Name, the iPhone's Item.name, the party's card) and
/// in Jellyfin itself through the extension (TitleNameRepair), which then asks its sources again with the real name.
/// </summary>
public static partial class TitleNames
{
    [GeneratedRegex("^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{32})_+(?=\\S)")]
    private static partial Regex FilePrefix();

    /// <summary><paramref name="name"/> without a download tool's code before it; the same when there is none.</summary>
    public static string Clean(string name) => name.Length > 33 && FilePrefix().Match(name) is { Success: true } prefix ? name[prefix.Length..] : name;

    /// <summary><see cref="Clean(string)"/>, null kept.</summary>
    public static string? CleanOrNull(string? name) => name is null ? null : Clean(name);
}
