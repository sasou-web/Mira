namespace Mira.Core;

/// <summary>
/// A title's bonus videos placed a few at a time (ogsas, 2026-11-18: 48 textless credits of « Monogatari Series » placed
/// at once, then 47 ffprobe of Jellyfin together on the USB drive, frozen, 0.9 GB of memory left). Jellyfin reads every
/// extra of a title at once as soon as their list changes (<c>BaseItem.RefreshExtras</c>: one task each, all awaited
/// together, no limit, and its concurrency settings don't cover them); only the new ones are analysed, so placing
/// <see cref="Size"/> at a time keeps it to that many. The next batch goes once Jellyfin lists every extra Mira placed
/// in that title's folder (it lists one only once it has read it), or after <see cref="Patience"/> without that.
/// </summary>
public sealed record HeldExtras(IReadOnlyList<ImportOperation> Operations, ImportMode Mode, DateTimeOffset At);

public static class ExtrasBatches
{
    public const int Size = 4;
    /// <summary>The next batch goes after this long even when Jellyfin never lists the last one (an extra it can't read).</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(10);

    /// <summary>The folder of the title an extra belongs to: the one holding its « Extras » folder.</summary>
    public static string OwnerFolder(string destination) => Path.GetDirectoryName(Path.GetDirectoryName(destination) ?? "") ?? "";

    /// <summary>The next batch: up to <paramref name="size"/> extras of the same title as the first one waiting.</summary>
    public static IReadOnlyList<ImportOperation> Next(IReadOnlyList<ImportOperation> held, int size = Size)
    {
        if (held.Count == 0) return [];
        var owner = OwnerFolder(held[0].Destination);
        return held.Where(x => SamePath(OwnerFolder(x.Destination), owner)).Take(size).ToList();
    }

    /// <summary>Jellyfin lists every extra Mira placed (by its path): it is done reading them.</summary>
    public static bool Settled(IEnumerable<string> placed, IEnumerable<string?> listed)
    {
        var known = listed.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return placed.All(known.Contains);
    }

    /// <summary>Time for the next batch: Jellyfin settled the last one, or it was placed <see cref="Patience"/> ago.</summary>
    public static bool Due(HeldExtras held, DateTimeOffset now, bool settled) => settled || now - held.At >= Patience;

    public static bool Under(string path, string folder) =>
        folder.Length > 0 && path.StartsWith(folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b) => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
