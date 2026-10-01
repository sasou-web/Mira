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

/// <summary>Installs <see cref="MpvPackage"/> into the profile ("data\mpv"), where updates of Mira never touch it.</summary>
public static class MpvInstaller
{
    public const string VersionFile = "version.txt", SourceFile = "SOURCE.txt";
    public static string Folder(string profile) => Path.Combine(profile, "mpv");
    public static string LibraryPath(string profile) => Path.Combine(Folder(profile), MpvPackage.LibraryName);
    public static bool IsInstalled(string profile, MpvPackage? package = null)
    {
        package ??= MpvPackage.Current;
        try { return File.Exists(LibraryPath(profile)) && File.ReadAllText(Path.Combine(Folder(profile), VersionFile)).Trim() == package.Version; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    /// <summary>
    /// Downloads, checks and extracts the library; returns its path. Already installed: nothing is downloaded.
    /// <paramref name="progress"/> goes from 0 to 1, the download taking the first 80 %.
    /// </summary>
    public static async Task<string> InstallAsync(string profile, MpvPackage? package = null, HttpMessageHandler? handler = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        package ??= MpvPackage.Current;
        var folder = Folder(profile); var library = LibraryPath(profile);
        if (IsInstalled(profile, package)) { progress?.Report(1); return library; }
        Directory.CreateDirectory(folder);
        var archive = Path.Combine(folder, package.ArchiveName + ".partial");
        var extracted = library + ".partial";
        try
        {
            await VerifiedDownload.DownloadAsync(package.Url, archive, package.ArchiveSha256, package.Size, package.Size, "Le moteur vidéo", handler,
                Scaled.By(progress, .8), ct).ConfigureAwait(false);
            await Task.Run(() => Extract(package, archive, extracted), ct).ConfigureAwait(false);
            progress?.Report(.98);
            try { File.Move(extracted, library, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new InstallException("Le moteur vidéo actuel est en cours d’utilisation : arrête la lecture, puis réessaie."); }
            await File.WriteAllTextAsync(Path.Combine(folder, SourceFile),
                $"libmpv {package.Version}, installé par Mira depuis {package.Url}\nSHA-256 de l’archive : {package.ArchiveSha256}\nSHA-256 de {MpvPackage.LibraryName} : {package.LibrarySha256}\n" +
                "Build Windows de mpv par shinchiro (https://github.com/shinchiro/mpv-winbuild-cmake), listée sur https://mpv.io/installation/.\n" +
                "mpv et les bibliothèques qu’il inclut (dont FFmpeg) restent sous leurs propres licences (GPL) ; sources : https://github.com/mpv-player/mpv\n", ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(folder, VersionFile), package.Version, ct).ConfigureAwait(false);
            progress?.Report(1);
            return library;
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
                ?? throw new InstallException("L’archive téléchargée ne contient pas le moteur vidéo attendu.");
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var input = entry.OpenEntryStream())
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buffer = new byte[1 << 16]; int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { sha.AppendData(buffer, 0, read); output.Write(buffer, 0, read); }
            }
            if (!VerifiedDownload.Matches(sha.GetHashAndReset(), package.LibrarySha256)) throw new InstallException("Le moteur extrait ne correspond pas à la version vérifiée par Mira : il n’est pas installé.");
        }
        catch (InstallException) { throw; }
        // The archive was checked by its hash before this point: a read or write failure here comes from the disk.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw DiskError(); }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        { throw new InstallException("L’archive du moteur vidéo est illisible : elle n’est pas installée."); }
    }
    private static InstallException DiskError() => new("Le moteur vidéo n’a pas pu être enregistré dans le dossier de Mira : vérifie l’espace libre, puis réessaie.");
    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
}
