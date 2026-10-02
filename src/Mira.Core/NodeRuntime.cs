using System.IO.Compression;

namespace Mira.Core;

/// <summary>
/// The Node.js runtime Mira installs for TorLink: one fixed build of the official Windows zip from nodejs.org, at the
/// SHA-256 listed in its SHASUMS256.txt (whose signature by a Node.js releaser was checked when it was pinned here).
/// </summary>
public sealed record NodePackage(string Version, Uri Url, long Size, string Sha256)
{
    /// <summary>Node.js 24 LTS ("Krypton"); TorLink needs 22 or later.</summary>
    public static readonly NodePackage Current = new("24.21.0", new("https://nodejs.org/dist/v24.21.0/node-v24.21.0-win-x64.zip"),
        37_618_919, "158f7685b44de51f6c0df1d153526cbcd3e1bc739a8dfc607721cef75de9e541");
    public string ArchiveName => Path.GetFileName(Url.AbsolutePath);
    /// <summary>The single top folder of the official zip.</summary>
    public string Root => Path.GetFileNameWithoutExtension(ArchiveName) + "/";
}

/// <summary>Installs <see cref="NodePackage"/> into the profile ("data\node"): only node.exe, npm and the licence, for this user, without administrator rights.</summary>
public static class NodeInstaller
{
    public const string VersionFile = "version.txt", SourceFile = "SOURCE.txt";
    public const int MinimumMajor = 22;
    public static string Folder(string profile) => Path.Combine(profile, "node");
    public static string Executable(string profile) => Path.Combine(Folder(profile), "node.exe");
    public static string NpmCli(string profile) => Path.Combine(Folder(profile), "node_modules", "npm", "bin", "npm-cli.js");
    public static bool IsInstalled(string profile, NodePackage? package = null)
    {
        package ??= NodePackage.Current;
        try { return File.Exists(Executable(profile)) && File.Exists(NpmCli(profile)) && File.ReadAllText(Path.Combine(Folder(profile), VersionFile)).Trim() == package.Version; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    /// <summary>
    /// Downloads, checks and extracts Node.js; returns node.exe. Already installed: nothing is downloaded. The new copy is
    /// built beside the old one and only then put in its place. <paramref name="progress"/> goes from 0 to 1, the
    /// download taking the first 85 %.
    /// </summary>
    public static async Task<string> InstallAsync(string profile, NodePackage? package = null, HttpMessageHandler? handler = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        package ??= NodePackage.Current;
        if (IsInstalled(profile, package)) { progress?.Report(1); return Executable(profile); }
        var folder = Folder(profile); Directory.CreateDirectory(profile);
        var archive = Path.Combine(profile, package.ArchiveName + ".partial");
        var staging = folder + ".partial";
        try
        {
            await VerifiedDownload.DownloadAsync(package.Url, archive, package.Sha256, package.Size, package.Size, "Node.js", handler, Scaled.By(progress, .85), ct).ConfigureAwait(false);
            await Task.Run(() => Extract(package, archive, staging, ct), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, SourceFile),
                $"Node.js {package.Version}, installé par Mira pour TorLink depuis {package.Url}\nSHA-256 : {package.Sha256}\n" +
                "Licence : LICENSE (MIT et licences des composants inclus) ; sources : https://github.com/nodejs/node\n", ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, VersionFile), package.Version, ct).ConfigureAwait(false);
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                Directory.Move(staging, folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new InstallException("Node.js est en cours d’utilisation : ferme TorLink, puis réessaie."); }
            progress?.Report(1);
            return Executable(profile);
        }
        catch (Exception ex) when (ex is not InstallException && ex is IOException or UnauthorizedAccessException) { throw DiskError(); }
        finally { TryDelete(archive); TryDeleteFolder(staging); }
    }
    /// <summary>
    /// node.exe, LICENSE and npm only (no corepack, no shell scripts), each entry kept inside <paramref name="target"/>.
    /// The archive was checked by its SHA-256 before this point.
    /// </summary>
    private static void Extract(NodePackage package, string archivePath, string target, CancellationToken ct)
    {
        TryDeleteFolder(target); Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var found = false;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!entry.FullName.StartsWith(package.Root, StringComparison.Ordinal)) continue;
                var relative = entry.FullName[package.Root.Length..];
                if (!Wanted(relative) || relative.EndsWith('/')) continue;
                var destination = Path.GetFullPath(Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InstallException("L’archive de Node.js contient un chemin inattendu : elle n’est pas installée.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
                found |= relative == "node.exe";
            }
            if (!found || !File.Exists(Path.Combine(target, "node_modules", "npm", "bin", "npm-cli.js")))
                throw new InstallException("L’archive téléchargée ne contient pas Node.js et npm : rien n’est installé.");
        }
        catch (InstallException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw DiskError(); }
        catch (InvalidDataException) { throw new InstallException("L’archive de Node.js est illisible : elle n’est pas installée."); }
    }
    private static bool Wanted(string relative) => relative is "node.exe" or "LICENSE" || relative.StartsWith("node_modules/npm/", StringComparison.Ordinal);
    /// <summary>The major version of a node.exe from its file version (22 for "22.15.0"); null when unreadable.</summary>
    public static int? MajorVersion(string? productVersion) =>
        productVersion is not null && int.TryParse(productVersion.TrimStart('v', 'V').Split('.')[0], out var major) && major > 0 ? major : null;
    private static InstallException DiskError() => new("Node.js n’a pas pu être enregistré dans le dossier de Mira : vérifie l’espace libre, puis réessaie.");
    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    private static void TryDeleteFolder(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
}
