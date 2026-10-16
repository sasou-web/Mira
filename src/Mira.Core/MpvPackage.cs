using System.Security.Cryptography;
using SharpCompress.Archives.SevenZip;

namespace Mira.Core;

/// <summary>
/// The libmpv build Mira installs on request: one fixed archive of the Windows builds listed on mpv.io (shinchiro,
/// published on SourceForge), checked by its SHA-256 before anything is extracted, and the extracted library by its own.
/// The archive is downloaded from its distributor, never shipped with Mira: its licence and sources stay theirs.
/// </summary>
public sealed record MpvPackage(string Version, Uri Url, long Size, string ArchiveSha256, string LibrarySha256)
{
    public const string LibraryName = "libmpv-2.dll";
    /// <summary>Generic x86_64 build (the "v3" builds need AVX2). Its SHA-1 and MD5 match the ones SourceForge publishes.</summary>
    public static readonly MpvPackage Current = new("20260927-git-a1bf4b6559",
        new("https://downloads.sourceforge.net/project/mpv-player-windows/libmpv/mpv-dev-x86_64-20260927-git-a1bf4b6559.7z"),
        31_487_676, "3a80c48d271a8aa77f7b6617cd7f182a87370361cd23635562927c9eac74ac5c", "0a81c004aae0ee7d512b9a26e38f66281f9591e84e1663215cc3a36a4bde6f6a");
    public string ArchiveName => Path.GetFileName(Url.AbsolutePath);
}

/// <summary>
/// Installs <see cref="MpvPackage"/> into the profile ("data\mpv"), where updates of Mira never touch it. When the pinned
/// build is newer than the installed one, it is prepared beside it ("data\mpv-next") and takes its place at the next
/// start, before anything loads the library (<see cref="ActivatePrepared"/>).
/// </summary>
public static class MpvInstaller
{
    public const string VersionFile = "version.txt", SourceFile = "SOURCE.txt";
    public static string Folder(string profile) => Path.Combine(profile, "mpv");
    public static string NextFolder(string profile) => Path.Combine(profile, "mpv-next");
    public static string LibraryPath(string profile) => Path.Combine(Folder(profile), MpvPackage.LibraryName);
    public static bool IsInstalled(string profile, MpvPackage? package = null) => IsReady(Folder(profile), package ?? MpvPackage.Current);
    private static bool IsReady(string folder, MpvPackage package)
    {
        try { return File.Exists(Path.Combine(folder, MpvPackage.LibraryName)) && File.ReadAllText(Path.Combine(folder, VersionFile)).Trim() == package.Version; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    /// <summary>
    /// Mira's own library is an older build than <paramref name="package"/> (its version.txt is dated earlier, or missing).
    /// A newer build, left by a newer Mira, is kept.
    /// </summary>
    public static bool IsOutdated(string profile, MpvPackage? package = null)
    {
        package ??= MpvPackage.Current;
        if (!File.Exists(LibraryPath(profile))) return false;
        string installed;
        try { installed = File.ReadAllText(Path.Combine(Folder(profile), VersionFile)).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        // "20260927-git-a1bf4b6559": builds are named after their date.
        static string? Date(string version) => version.Length >= 8 && version[..8].All(char.IsAsciiDigit) ? version[..8] : null;
        return installed != package.Version && (Date(installed) is not { } date || Date(package.Version) is not { } pinned || string.CompareOrdinal(date, pinned) < 0);
    }
    /// <summary>
    /// Downloads, checks and extracts the library; returns its path. Already installed: nothing is downloaded.
    /// <paramref name="progress"/> goes from 0 to 1, the download taking the first 80 %.
    /// </summary>
    public static async Task<string> InstallAsync(string profile, MpvPackage? package = null, HttpMessageHandler? handler = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        package ??= MpvPackage.Current;
        var library = LibraryPath(profile);
        if (IsInstalled(profile, package)) { progress?.Report(1); return library; }
        await FetchAsync(Folder(profile), package, handler, progress, ct).ConfigureAwait(false);
        return library;
    }
    /// <summary>
    /// The pinned build into "mpv-next" when Mira's own library is older (<see cref="IsOutdated"/>); the library in use is
    /// never touched. True once a build waits there for the next start.
    /// </summary>
    public static async Task<bool> PrepareUpdateAsync(string profile, MpvPackage? package = null, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        package ??= MpvPackage.Current;
        var next = NextFolder(profile);
        if (!IsOutdated(profile, package)) return false;
        if (IsReady(next, package)) return true;
        try { await FetchAsync(next, package, handler, null, ct).ConfigureAwait(false); return true; }
        catch { TryDeleteFolder(next); throw; }
    }
    /// <summary>A newer build waits in "mpv-next" for the next start.</summary>
    public static bool IsPrepared(string profile, MpvPackage? package = null) => IsReady(NextFolder(profile), package ?? MpvPackage.Current);
    /// <summary>
    /// At startup, before any engine is created: the build prepared in "mpv-next" replaces Mira's own library. True when it
    /// did; false when nothing waits or the library could not be replaced, which stays as it was.
    /// </summary>
    public static bool ActivatePrepared(string profile, MpvPackage? package = null)
    {
        package ??= MpvPackage.Current;
        var next = NextFolder(profile); var folder = Folder(profile);
        if (!IsReady(next, package)) return false;
        if (!IsOutdated(profile, package)) { TryDeleteFolder(next); return false; }
        try
        {
            File.Move(Path.Combine(next, MpvPackage.LibraryName), LibraryPath(profile), overwrite: true);
            File.Copy(Path.Combine(next, SourceFile), Path.Combine(folder, SourceFile), overwrite: true);
            // Last: until it is written, the library counts as the older build and is prepared again if this is cut short.
            File.Copy(Path.Combine(next, VersionFile), Path.Combine(folder, VersionFile), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        TryDeleteFolder(next);
        return true;
    }
    /// <summary>Download, check and extraction into <paramref name="folder"/>; version.txt is written last.</summary>
    private static async Task FetchAsync(string folder, MpvPackage package, HttpMessageHandler? handler, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var library = Path.Combine(folder, MpvPackage.LibraryName);
        var archive = Path.Combine(folder, package.ArchiveName + ".partial");
        var extracted = library + ".partial";
        try
        {
            await VerifiedDownload.DownloadAsync(package.Url, archive, package.ArchiveSha256, package.Size, package.Size, L("Le moteur vidéo", "The video engine"), handler,
                Scaled.By(progress, .8), ct).ConfigureAwait(false);
            await Task.Run(() => Extract(package, archive, extracted), ct).ConfigureAwait(false);
            progress?.Report(.98);
            try { File.Move(extracted, library, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new InstallException(L("Le moteur vidéo actuel est en cours d’utilisation : arrête la lecture, puis réessaie.", "The current video engine is in use: stop playback, then try again.")); }
            await File.WriteAllTextAsync(Path.Combine(folder, SourceFile),
                $"libmpv {package.Version}, installé par Mira depuis {package.Url}\nSHA-256 de l’archive : {package.ArchiveSha256}\nSHA-256 de {MpvPackage.LibraryName} : {package.LibrarySha256}\n" +
                "Build Windows de mpv par shinchiro (https://github.com/shinchiro/mpv-winbuild-cmake), listée sur https://mpv.io/installation/.\n" +
                "mpv et les bibliothèques qu’il inclut (dont FFmpeg) restent sous leurs propres licences (GPL) ; sources : https://github.com/mpv-player/mpv\n", ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(folder, VersionFile), package.Version, ct).ConfigureAwait(false);
            progress?.Report(1);
        }
        catch (Exception ex) when (ex is not InstallException && ex is IOException or UnauthorizedAccessException) { throw DiskError(); }
        finally { TryDelete(archive); TryDelete(extracted); }
    }
    /// <summary>Only the library leaves the archive, and only if its own SHA-256 is the expected one.</summary>
    private static void Extract(MpvPackage package, string archivePath, string target)
    {
        try
        {
            using var archive = SevenZipArchive.Open(archivePath);
            var entry = archive.Entries.FirstOrDefault(x => !x.IsDirectory && x.Key is MpvPackage.LibraryName)
                ?? throw new InstallException(L("L’archive téléchargée ne contient pas le moteur vidéo attendu.", "The downloaded archive doesn’t contain the expected video engine."));
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var input = entry.OpenEntryStream())
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buffer = new byte[1 << 16]; int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { sha.AppendData(buffer, 0, read); output.Write(buffer, 0, read); }
                // On the disk before version.txt: after a power cut, a stamped engine is a whole one.
                output.Flush(flushToDisk: true);
            }
            if (!VerifiedDownload.Matches(sha.GetHashAndReset(), package.LibrarySha256)) throw new InstallException(L("Le moteur extrait ne correspond pas à la version vérifiée par Mira : il n’est pas installé.", "The extracted engine doesn’t match the version Mira checked: it isn’t installed."));
        }
        catch (InstallException) { throw; }
        // The archive was checked by its hash before this point: a read or write failure here comes from the disk.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw DiskError(); }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        { throw new InstallException(L("L’archive du moteur vidéo est illisible : elle n’est pas installée.", "The video engine’s archive can’t be read: it isn’t installed.")); }
    }
    private static InstallException DiskError() => new(L("Le moteur vidéo n’a pas pu être enregistré dans le dossier de Mira : vérifie l’espace libre, puis réessaie.", "The video engine couldn’t be saved in Mira’s folder: check the free space, then try again."));
    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    private static void TryDeleteFolder(string path) => PreparedFolder.Delete(path);
}
