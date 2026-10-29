namespace Mira.Core;

/// <summary>
/// What a replacement did. <paramref name="NewPath"/>: the new file in the old one's place, null when nothing changed
/// (<paramref name="Problem"/> says why; the old file is where it was) or when only half of it went through
/// (<paramref name="Recycled"/>: the old file is in the Recycle Bin, the new one kept under <paramref name="Left"/>).
/// <paramref name="Moved"/>: TorLink's file left its folder (it no longer shares it). <paramref name="Busy"/>: the old file
/// is open (played, read by Jellyfin): nothing was done, to try again later.
/// </summary>
public sealed record SwapResult(string? NewPath, string? Problem, bool Recycled, string? Left, bool Moved, IReadOnlyList<string> Subtitles, bool Busy = false);

/// <summary>
/// « Remplacer par une meilleure version », the files (on the PC of Jellyfin, by Mira for Windows): the new video takes
/// the old one's place, in its folder and under its name (with its own extension), and the old one goes to the Recycle
/// Bin. Nothing is lost on the way: the new file is first put beside under a name Jellyfin ignores, as a hard link of
/// TorLink's or a copy (TorLink's own stays until the end); the old one goes only then, and when it cannot (open,
/// refused), the new one is taken away again and nothing has changed. Never overwrites a file.
/// </summary>
public static class TitleSwap
{
    /// <summary>The new file is put beside under this suffix (Jellyfin does not read it), then renamed once the old one is gone.</summary>
    public const string Suffix = MediaImporter.PartialSuffix;

    /// <summary>Where the new video goes: the old one's folder and name, with the new one's extension (the same path when they match).</summary>
    public static string Destination(string oldPath, string newFile) =>
        Path.Combine(Path.GetDirectoryName(oldPath) ?? "", Path.GetFileNameWithoutExtension(oldPath) + Path.GetExtension(newFile).ToLowerInvariant());

    /// <param name="oldPath">The title's file, as Jellyfin has it (checked inside a library folder by the caller).</param>
    /// <param name="video">The new video, in TorLink's folder.</param>
    /// <param name="subtitles">The subtitles of the release, placed beside the new video when their name is free.</param>
    /// <param name="mode">Whether TorLink keeps sharing the download (its file stays, a link of it in the library) or not.</param>
    /// <param name="recycle">Sends a file to the Recycle Bin; true once it is no longer there.</param>
    public static async Task<SwapResult> SwapAsync(string oldPath, SourceFile video, IReadOnlyList<SourceFile> subtitles, ImportMode mode, Func<string, bool> recycle, CancellationToken ct = default)
    {
        SwapResult Refused(string problem) => new(null, problem, false, null, false, []);
        SwapResult Busy() => new(null, L("L’ancien fichier est ouvert en ce moment.", "The old file is open right now."), false, null, false, [], Busy: true);
        if (!File.Exists(oldPath)) return Refused(L("L’ancien fichier n’est plus à sa place : rien n’a été remplacé.", "The old file is no longer where it was: nothing was replaced."));
        if (!File.Exists(video.Path) || !ReleaseName.IsVideo(video.Path)) return Refused(L("La nouvelle vidéo est introuvable dans le dossier de TorLink.", "The new video can’t be found in TorLink’s folder."));
        // Open somewhere (played, read by Jellyfin): it could not go to the Recycle Bin; nothing is copied for nothing.
        if (Open(oldPath)) return Busy();
        var destination = Destination(oldPath, video.Path);
        var same = string.Equals(Path.GetFullPath(destination), Path.GetFullPath(oldPath), StringComparison.OrdinalIgnoreCase);
        if (!same && File.Exists(destination)) return Refused(L($"Un autre fichier porte déjà le nom de la nouvelle version ({Path.GetFileName(destination)}) : rien n’a été remplacé.", $"Another file already has the new version’s name ({Path.GetFileName(destination)}): nothing was replaced."));
        var temporary = destination + Suffix;
        bool linked;
        try
        {
            // Only ever a link or a partial copy of an earlier attempt: TorLink's file was never moved there.
            if (File.Exists(temporary)) File.Delete(temporary);
            linked = MediaImporter.TryHardLink(video.Path, temporary);
            if (!linked) await CopyAsync(video.Path, temporary, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Remove(temporary);
            if (ex is OperationCanceledException) throw;
            return Refused(Reason(ex));
        }
        // The old file goes; when it does not (opened meanwhile, refused), the new one goes away again and nothing changed.
        if (!recycle(oldPath) && File.Exists(oldPath))
        {
            Remove(temporary);
            return Open(oldPath) ? Busy()
                : Refused(L("L’ancien fichier n’a pas pu aller à la Corbeille : rien n’a été remplacé.", "The old file couldn’t go to the Recycle Bin: nothing was replaced."));
        }
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temporary, destination, false); break; }
            // An antivirus or an indexer reading the new file a moment: tried again over about 7 seconds.
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33 && attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(1 << attempt), CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(null, L($"L’ancien fichier est dans la Corbeille, mais la nouvelle version n’a pas pu prendre son nom : elle est restée sous {Path.GetFileName(temporary)}.",
                    $"The old file is in the Recycle Bin, but the new version couldn’t take its name: it’s still called {Path.GetFileName(temporary)}."), true, temporary, false, []);
            }
        }
        JellyfinServiceAccess.ShareWithFolder(destination);
        var placed = new List<(string Source, string Target)>();
        var stem = Path.Combine(Path.GetDirectoryName(destination) ?? "", Path.GetFileNameWithoutExtension(destination));
        foreach (var subtitle in subtitles)
        {
            var target = stem + ReleaseName.SubtitleTags(subtitle.RelativePath) + Path.GetExtension(subtitle.Path).ToLowerInvariant();
            // A subtitle already beside (one kept for the title, another of the release) stays as it is.
            if (File.Exists(target) || !File.Exists(subtitle.Path)) continue;
            try
            {
                if (!MediaImporter.TryHardLink(subtitle.Path, target)) File.Copy(subtitle.Path, target, false);
                JellyfinServiceAccess.ShareWithFolder(target);
                placed.Add((subtitle.Path, target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        // TorLink's own files go when they were copied (another drive), or when it stops sharing: never twice on the disk.
        var moved = !linked || mode == ImportMode.Move;
        if (moved) foreach (var file in placed.Select(x => x.Source).Prepend(video.Path)) Remove(file);
        return new(destination, null, true, null, moved, placed.Select(x => x.Target).ToList());
    }

    /// <summary>Whether another program has the file open (a sharing violation): Windows would not let it go.</summary>
    private static bool Open(string path)
    {
        try { using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { } return false; }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        output.SetLength(input.Length);
        await input.CopyToAsync(output, 1 << 20, ct);
    }

    private static void Remove(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Reason(Exception ex) => ex switch
    {
        IOException io when (io.HResult & 0xFFFF) is 112 or 39 => L("Pas assez de place sur le disque de la bibliothèque : rien n’a été remplacé.", "Not enough space on the library’s drive: nothing was replaced."),
        UnauthorizedAccessException => L("Accès refusé au dossier du titre : rien n’a été remplacé.", "Access to the title’s folder denied: nothing was replaced."),
        _ => L("La nouvelle version n’a pas pu être posée à côté de l’ancienne : rien n’a été remplacé.", "The new version couldn’t be put beside the old one: nothing was replaced.")
    };
}
