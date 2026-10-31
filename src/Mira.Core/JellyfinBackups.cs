using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>
/// An archive of Jellyfin's own backup (10.11 and later, checked on 12.1): its database (accounts, progress, libraries),
/// its settings, and the parts asked for, in one zip of its data folder's « backups ». <paramref name="Path"/> is where
/// Jellyfin wrote it, on its own computer.
/// </summary>
public sealed record JellyfinBackup(string Path, DateTimeOffset Created, string ServerVersion, bool Metadata, bool Subtitles, bool Trickplay)
{
    /// <summary>« jellyfin-backup-20261027075228.zip »: the name, whichever separator Jellyfin's system writes.</summary>
    public string FileName => Path[(Path.LastIndexOfAny(['/', '\\']) + 1)..];
}

/// <summary>
/// « Sauvegarder Jellyfin » (Windows, on the PC of Jellyfin, administrator): Jellyfin's backup made through its API
/// (POST Backup/Create answers once the zip is written; 500 without 5 GiB free in its backups folder, checked on 12.1),
/// then copied to a folder of the person's (a USB key, another disk), where the newest ones are kept. Recalled after 30
/// days without one, and made before every update of Jellyfin. The archive holds the accounts (their password hashes)
/// and the sign-in tokens of every device: the person is told so.
/// </summary>
public static partial class JellyfinBackups
{
    /// <summary>What Jellyfin puts in the archive besides its database and settings: its metadata and pictures, and the subtitles it extracted; never the trickplay pictures, big and made again each night.</summary>
    public static readonly object Options = new { Metadata = true, Subtitles = true, Trickplay = false };
    /// <summary>Jellyfin's own floor in its backups folder: below it, it refuses (InvalidOperationException, answered as 500).</summary>
    public const long MinimumFree = 5L << 30;
    /// <summary>A backup older than this is due.</summary>
    public static readonly TimeSpan Due = TimeSpan.FromDays(30);
    /// <summary>While one is due, the reminder comes back after this.</summary>
    public static readonly TimeSpan RemindAgain = TimeSpan.FromDays(7);
    /// <summary>Copies kept in the person's folder: the older ones Mira copied there are removed.</summary>
    public const int CopiesKept = 3;
    /// <summary>The longest Jellyfin may take to write one (its metadata of a big library).</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(30);

    /// <summary>Jellyfin's list (GET Backup, or the answer of Backup/Create alone), newest first; what does not read is skipped.</summary>
    public static IReadOnlyList<JellyfinBackup> Parse(JsonElement json)
    {
        var items = json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().ToList() : json.ValueKind == JsonValueKind.Object ? [json] : [];
        return items.Select(One).OfType<JellyfinBackup>().OrderByDescending(x => x.Created).ToList();
    }
    private static JellyfinBackup? One(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || Text(item, "Path") is not { } path || !item.TryGetProperty("DateCreated", out var date)
            || date.ValueKind != JsonValueKind.String || !date.TryGetDateTimeOffset(out var created)) return null;
        var options = item.TryGetProperty("Options", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
        bool Flag(string name) => options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        return new(path, created, Text(item, "ServerVersion") ?? "", Flag("Metadata"), Flag("Subtitles"), Flag("Trickplay"));
    }
    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } text ? text : null;

    /// <summary>
    /// Whether a backup is due: the newest is <see cref="Due"/> old, or, without any, Mira has watched this Jellyfin that
    /// long (<paramref name="watchedSince"/>, the first time it found none): a Jellyfin just installed is not nagged.
    /// </summary>
    public static bool Overdue(DateTimeOffset? newest, DateTimeOffset watchedSince, DateTimeOffset now) => now - (newest ?? watchedSince) >= Due;
    /// <summary>Whether to remind now: due, and not reminded within <see cref="RemindAgain"/>.</summary>
    public static bool Reminds(DateTimeOffset? newest, DateTimeOffset watchedSince, DateTimeOffset? remindedAt, DateTimeOffset now) =>
        Overdue(newest, watchedSince, now) && (remindedAt is not { } last || now - last >= RemindAgain || last > now);

    /// <summary>« Dernière sauvegarde le 3 octobre, il y a 24 jours. », « aujourd’hui à 14:02 », or none yet.</summary>
    public static string Summary(JellyfinBackup? newest, DateTimeOffset now)
    {
        if (newest is null) return L("Aucune sauvegarde pour l’instant.", "No backup yet.");
        var local = newest.Created.ToLocalTime(); var days = (now.ToLocalTime().Date - local.Date).Days;
        return days <= 0 ? L($"Dernière sauvegarde aujourd’hui à {Time(local)}.", $"Last backup today at {Time(local)}.")
            : days == 1 ? L($"Dernière sauvegarde hier à {Time(local)}.", $"Last backup yesterday at {Time(local)}.")
            : L($"Dernière sauvegarde le {Day(local)}, il y a {days} jours.", $"Last backup on {Day(local)}, {days} days ago.");
    }
    /// <summary>The reminder, with « Sauvegarder ».</summary>
    public static string Reminder(JellyfinBackup? newest, DateTimeOffset now) => newest is null
        ? L("Jellyfin n’a jamais été sauvegardé : ses comptes, ta progression et tes bibliothèques ne sont que sur ce PC.", "Jellyfin has never been backed up: its accounts, your progress and your libraries are only on this PC.")
        : L($"Jellyfin n’a pas été sauvegardé depuis {(now - newest.Created).Days} jours.", $"Jellyfin hasn’t been backed up for {(now - newest.Created).Days} days.");
    /// <summary>Said when the backups folder has less than <see cref="MinimumFree"/>, before asking Jellyfin (which would only answer 500).</summary>
    public static string NoRoom(string drive, long free) =>
        L($"Jellyfin exige 5 Go libres sur {drive} pour se sauvegarder : il en reste {OfflineCopies.SizeText(free)}. Fais de la place, puis réessaie.",
            $"Jellyfin needs 5 GB free on {drive} to back itself up: {OfflineCopies.SizeText(free)} left. Free up some space, then try again.");

    /// <summary>A name Jellyfin gives its archives: the only files Mira removes from the person's folder.</summary>
    public static bool IsArchiveName(string name) => ArchiveName().IsMatch(name);
    /// <summary>Of the archives in the person's folder, those beyond the <paramref name="keep"/> newest (their names carry their date).</summary>
    public static IReadOnlyList<string> Superseded(IEnumerable<string> names, int keep = CopiesKept) =>
        names.Where(IsArchiveName).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).Skip(keep).ToList();
    /// <summary>
    /// Copies the archive into <paramref name="folder"/> under its name, through a « .part » file renamed once whole, then
    /// removes the copies beyond <see cref="CopiesKept"/>. The copy's path; an <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> says why not.
    /// </summary>
    public static string Copy(string archive, string folder)
    {
        var name = Path.GetFileName(archive);
        if (!IsArchiveName(name)) throw new IOException(L($"« {name} » n’est pas une sauvegarde de Jellyfin.", $"“{name}” isn’t a Jellyfin backup."));
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(L($"Le dossier {folder} n’est pas là (clé ou disque débranché ?).", $"The folder {folder} isn’t there (key or disk unplugged?)."));
        var target = Path.Combine(folder, name); var part = target + ".part";
        try { File.Copy(archive, part, overwrite: true); File.Move(part, target, overwrite: true); }
        catch { try { File.Delete(part); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } throw; }
        Prune(folder);
        return target;
    }
    /// <summary>
    /// The archives of <paramref name="folder"/> beyond the <see cref="CopiesKept"/> newest removed, as far as Windows
    /// lets Mira: the person's folder, or Jellyfin's own after a backup Mira made by itself (a month each, they pile up).
    /// </summary>
    public static void Prune(string folder)
    {
        try
        {
            foreach (var old in Superseded(Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>()))
            {
                try { File.Delete(Path.Combine(folder, old)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    [GeneratedRegex(@"^jellyfin-backup-\d{14}\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex ArchiveName();
}
