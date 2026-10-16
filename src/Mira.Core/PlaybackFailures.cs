using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mira.Core;

/// <summary>
/// « Lecture impossible » reports. Mira web sends one when the person asks (« Envoyer le détail à Jellyfin »,
/// player/failure.js); Jellyfin keeps it among its logs (ClientLog/Document: upload_Mira_&lt;version&gt;_….log, deleted
/// with them, after LogFileRetentionDays); Mira for Windows lists them for its administrator (Réglages → Jellyfin &amp;
/// synchronisation), ready to copy. The same report for both in tests/fixtures/failure.json.
/// </summary>
public static class PlaybackFailures
{
    /// <summary>What the report says it is (its « mira » field).</summary>
    public const string Kind = "lecture-impossible";
    /// <summary>How many of the latest reports are read.</summary>
    public const int Shown = 10;
    /// <summary>Larger files are not read: a report is a few kilobytes.</summary>
    public const long MaxSize = 256 * 1024;

    /// <summary>A file Mira sent Jellyfin's logs (its client name is « Mira »), not one of Jellyfin's own or another app's.</summary>
    public static bool IsMira(string? name) => name is not null
        && name.StartsWith("upload_Mira_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);

    /// <summary>The report in a file of Jellyfin's logs; null for anything else (another text, a file cut short).</summary>
    public static PlaybackFailure? Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "mira") != Kind) return null;
            if (Text(root, "at") is not { } at || !DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)) return null;
            var attempts = root.TryGetProperty("attempts", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0).ToArray() : [];
            return new(when, Text(root, "title") ?? L("Un titre", "A title"), Text(root, "device") ?? L("un appareil", "a device"), Text(root, "user"), Text(root, "file"),
                Text(root, "media"), Text(root, "message") ?? "", attempts, Text(root, "version"), Text(root, "browser"), text.Trim());
        }
        catch (JsonException) { return null; }
    }

    /// <summary>« « Courte Web · S1 · É2 · Le départ » sur iPhone (mira) ».</summary>
    public static string Heading(PlaybackFailure failure) =>
        L($"« {failure.Title} » sur {failure.Device}", $"“{failure.Title}” on {failure.Device}") + (failure.User is { } user ? $" ({user})" : "");

    private static readonly string[] Months = ["janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre", "octobre", "novembre", "décembre"];
    /// <summary>
    /// « aujourd’hui à 21 h 03 », « hier à 9 h 30 », « le 3 octobre à 21 h 03 » ("today at 9:03 PM", "on October 3 at
    /// 9:03 PM"): both times in the same zone (local, on the PC).
    /// </summary>
    public static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var days = (now.Date - at.Date).Days;
        if (!IsFrench)
        {
            var time = Time(at);
            return days == 0 ? $"today at {time}" : days == 1 ? $"yesterday at {time}" : $"on {(at.Year != now.Year ? Date(at) : Day(at))} at {time}";
        }
        var hour = string.Create(CultureInfo.InvariantCulture, $"{at.Hour} h {at.Minute:00}");
        return days == 0 ? $"aujourd’hui à {hour}" : days == 1 ? $"hier à {hour}"
            : string.Create(CultureInfo.InvariantCulture, $"le {at.Day} {Months[at.Month - 1]}{(at.Year != now.Year ? $" {at.Year}" : "")} à {hour}");
    }

    /// <summary>What « Copier » puts on the clipboard: the report in words, then as Mira web sent it.</summary>
    public static string Text(PlaybackFailure failure, DateTimeOffset at)
    {
        var text = new StringBuilder();
        var who = failure.User is { } user ? $" ({user})" : "";
        text.Append(IsFrench ? string.Create(CultureInfo.InvariantCulture, $"« {failure.Title} » n’a pas pu être lu sur {failure.Device}{who}, le {at.Day} {Months[at.Month - 1]} {at.Year} à {at.Hour} h {at.Minute:00}.\n")
            : $"“{failure.Title}” couldn’t be played on {failure.Device}{who}, on {Date(at)} at {Time(at)}.\n");
        if (failure.File is { } file) text.Append(L("Fichier : ", "File: ")).Append(file).Append('\n');
        if (failure.Media is { } media) text.Append(L("Média : ", "Media: ")).Append(media).Append('\n');
        if (failure.Message.Length > 0) text.Append(L("Message : ", "Message: ")).Append(failure.Message).Append('\n');
        if (failure.Attempts.Length > 0) text.Append(L("Essayé :\n", "Tried:\n")).Append(string.Concat(failure.Attempts.Select(x => $"- {x}\n")));
        if (failure.Version is { } version) text.Append($"Mira web {version}{(failure.Browser is { } browser ? $" · {browser}" : "")}\n");
        return text.Append(L("\nDétail technique :\n", "\nTechnical details:\n")).Append(failure.Document).Append('\n').ToString();
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } text ? text : null;
}

/// <summary>A « Lecture impossible » report (<see cref="PlaybackFailures"/>); Document: the file as Jellyfin keeps it.</summary>
public sealed record PlaybackFailure(DateTimeOffset At, string Title, string Device, string? User, string? File, string? Media,
    string Message, string[] Attempts, string? Version, string? Browser, string Document);

/// <summary>A file of Jellyfin's logs (System/Logs).</summary>
public sealed record LogFile(string Name, DateTimeOffset DateModified, long Size);
