using System.Text.Json.Nodes;

namespace Mira.Core;

/// <summary>A title's image as Jellyfin lists it (Items/{id}/Images): its type, its index among several backdrops, its file.</summary>
public sealed record ItemImage(string ImageType, int? ImageIndex = null, string? Path = null);

/// <summary>
/// Metadata kept beside the files (idea 2 of 2026-11-18): each film and series library of the Jellyfin on this PC
/// writes its titles' NFO files and images in their folders (SaveLocalMetadata with the « Nfo » saver: « Enregistrer
/// les illustrations dans les dossiers des médias »), so that a Jellyfin installed again, or another server given the
/// same disk, reads every title back as it was, corrections by hand included, without asking TMDb again. Turned on by
/// Mira for Windows on the PC of Jellyfin (administrator), once per server; what Jellyfin kept in its own data before
/// is written beside the files once (<see cref="CatchUpAsync"/>). Checked on Jellyfin 12.1: a library scan writes
/// nothing for titles already known; « Rechercher les métadonnées manquantes » writes their NFO and keeps what was
/// edited; an image given again is saved beside the files and its old copy deleted by Jellyfin.
/// </summary>
public static class MetadataBeside
{
    /// <summary>The kinds of library concerned: films and series (Animes is one of series).</summary>
    public static readonly string[] Kinds = ["movies", "tvshows"];
    /// <summary>Jellyfin's saver of NFO files (XbmcMetadata), the only one it has.</summary>
    public const string Saver = "Nfo";
    /// <summary>Images a title keeps beside its files; never chapters (Jellyfin's own) nor people's.</summary>
    private static readonly HashSet<string> Movable = new(StringComparer.Ordinal) { "Primary", "Art", "Backdrop", "Banner", "Logo", "Thumb", "Disc", "Box", "BoxRear", "Menu", "Screenshot" };

    /// <summary>
    /// Whether a library keeps its metadata beside the files: SaveLocalMetadata on, and the NFO saver not left out
    /// (MetadataSavers null = every saver, as a library made through the API has it). Null when unknown.
    /// </summary>
    public static bool? Keeps(JsonObject? options)
    {
        if (options?["SaveLocalMetadata"] is not JsonValue value || !value.TryGetValue<bool>(out var on)) return null;
        return on && Saves(options);
    }
    private static bool Saves(JsonObject options) => options["MetadataSavers"] is not JsonArray savers
        || savers.OfType<JsonValue>().Any(x => x.TryGetValue<string>(out var name) && name == Saver);
    /// <summary>The same options keeping metadata beside the files, everything else as it was; null when they already do (or are unknown).</summary>
    public static JsonObject? Kept(JsonObject options)
    {
        if (Keeps(options) is not false) return null;
        var changed = (JsonObject)options.DeepClone();
        changed["SaveLocalMetadata"] = true;
        if (!Saves(changed)) ((JsonArray)changed["MetadataSavers"]!).Add(Saver);
        return changed;
    }

    /// <summary>
    /// Where Jellyfin writes, or reads, a title's NFO (MovieNfoSaver, SeriesNfoSaver, EpisodeNfoSaver): beside the file
    /// with its name; « movie.nfo » too for a film (read by Jellyfin as well); a disc's folder: « VIDEO_TS.nfo » or its
    /// own name. Empty for what has no NFO of its own to wait for (a season, a title without a file).
    /// </summary>
    public static IReadOnlyList<string> NfoPaths(MediaItem item)
    {
        if (item.Path is not { Length: > 0 } path || !System.IO.Path.IsPathFullyQualified(path)) return [];
        switch (item.Type)
        {
            case "Series": return [System.IO.Path.Combine(path, "tvshow.nfo")];
            case "Episode": return [System.IO.Path.ChangeExtension(path, ".nfo")];
            case "Movie" when Directory.Exists(path):
                return [System.IO.Path.Combine(path, "VIDEO_TS", "VIDEO_TS.nfo"), System.IO.Path.Combine(path, System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)) + ".nfo")];
            case "Movie": return [System.IO.Path.ChangeExtension(path, ".nfo"), System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? "", "movie.nfo")];
            default: return [];
        }
    }
    /// <summary>Whether the title has its NFO beside it (none to wait for: true).</summary>
    public static bool HasNfo(MediaItem item) { var paths = NfoPaths(item); return paths.Count == 0 || paths.Any(File.Exists); }

    /// <summary>Whether a file is inside one of the libraries' folders (Jellyfin's own data is not).</summary>
    public static bool Inside(string? path, IEnumerable<string> folders) => path is { Length: > 0 } && folders.Any(folder => folder.Length > 0
        && path.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(folder) + (path.Contains('\\') ? '\\' : '/'), StringComparison.OrdinalIgnoreCase));
    /// <summary>The images of a title still in Jellyfin's own data, to give it again; backdrops in their order.</summary>
    public static IReadOnlyList<ItemImage> Outside(IEnumerable<ItemImage> images, IEnumerable<string> folders) =>
        images.Where(x => Movable.Contains(x.ImageType) && x.Path is { Length: > 0 } && !Inside(x.Path, folders)).OrderBy(x => x.ImageType == "Backdrop").ThenBy(x => x.ImageIndex ?? 0).ToList();

    /// <summary>What a pass did: images written beside the files, titles asked again, and whether nothing is left.</summary>
    /// <param name="Writes">False when Jellyfin saved an image in its own data again: it may not write in the folders.</param>
    public sealed record Pass(int Images, int Refreshed, bool Writes, bool Done);

    /// <summary>
    /// Once SaveLocalMetadata is on, what Jellyfin kept in its own data before: each image given again (it is then
    /// saved beside the files and its NFO written), then, with <paramref name="refresh"/>, each film or series still
    /// without an NFO (itself, or one of its episodes) asked for its missing metadata, which keeps what is known and
    /// writes the NFO (queued by Jellyfin: the next pass sees it). Only the libraries that keep their metadata beside the
    /// files; stopped at the first image Jellyfin saves in its own data again (<see cref="Pass.Writes"/>). Done when
    /// no image was left outside and no NFO was missing.
    /// </summary>
    public static async Task<Pass> CatchUpAsync(JellyfinClient client, bool refresh, CancellationToken ct = default)
    {
        const int Page = 500;
        if (await client.MetadataLibrariesAsync(ct) is not { } libraries) return new(0, 0, true, false);
        var kept = libraries.Where(x => x.Keeps == true).ToList();
        var folders = kept.SelectMany(x => x.Locations).ToList();
        int moved = 0, asked = 0;
        var missing = false;
        foreach (var library in kept)
        {
            var titles = new List<MediaItem>();
            for (var start = 0; ; start += Page)
            {
                var page = await client.LibraryFilesAsync(library.Id, start, Page, ct);
                titles.AddRange(page.Items);
                if (page.Items.Count < Page || titles.Count >= page.TotalRecordCount) break;
            }
            foreach (var item in titles)
            {
                if (item.Path is not { Length: > 0 }) continue;
                var outside = Outside(await client.ItemImagesAsync(item.Id, ct), folders);
                if (outside.Count == 0) continue;
                missing = true;
                foreach (var image in outside) if (await client.SaveImageAgainAsync(item.Id, image, ct)) moved++;
                // The old backdrops, once their copies follow them: from the last, so the others keep their index.
                foreach (var old in outside.Where(x => x.ImageType == "Backdrop").OrderByDescending(x => x.ImageIndex ?? 0))
                    await client.RemoveImageAsync(item.Id, old.ImageType, old.ImageIndex ?? 0, ct);
                var after = await client.ItemImagesAsync(item.Id, ct);
                if (outside.Any(x => x.ImageType != "Backdrop") && Outside(after, folders).Any(x => x.ImageType != "Backdrop")
                    || after.Any(x => x.ImageType == "Backdrop" && x.Path is { Length: > 0 } && !Inside(x.Path, folders)))
                    return new(moved, asked, false, false);
            }
            // Each film, and each series with every one of its episodes, has its NFO.
            var series = titles.Where(x => x.Type == "Episode" && !HasNfo(x) && x.SeriesId is { Length: > 0 }).Select(x => x.SeriesId!).ToHashSet();
            foreach (var title in titles.Where(x => x.Type == "Movie" && !HasNfo(x) || x.Type == "Series" && (!HasNfo(x) || series.Contains(x.Id))))
            {
                missing = true;
                if (!refresh) continue;
                await client.FillMissingMetadataAsync(title.Id, ct);
                asked++;
            }
        }
        return new(moved, asked, true, !missing);
    }
}
