using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace Mira.Desktop.Services;

/// <summary>
/// mira-tailscale.exe, the node of « Tailscale intégré » (src/Mira.Tailscale, built by tools/tailscale/build.mjs):
/// beside Mira.exe in the folder distribution; inside the single-file Mira, which writes it into its profile (« app »)
/// the first time it is needed. Run only when its SHA-256 is the one this Mira was built with (Mira.Desktop.csproj):
/// never a copy left by another version, cut off while being written, or changed since.
/// </summary>
internal static class TailnetExecutable
{
    public const string Name = "mira-tailscale.exe";
    /// <summary>Lower-case SHA-256 written into Mira at build time; null when Mira was built without the node.</summary>
    public static string? Expected { get; } = typeof(TailnetExecutable).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(x => x.Key == "MiraTailscaleSha256")?.Value?.ToLowerInvariant();
    /// <summary>Windows refused to open or run the file: its antivirus found something in it (ERROR_VIRUS_INFECTED, ERROR_VIRUS_DELETED).</summary>
    public static bool Blocked(Exception ex) => ex is Win32Exception { NativeErrorCode: 225 or 226 } || ex is IOException { HResult: unchecked((int)0x800700E1) or unchecked((int)0x800700E2) };

    /// <summary>The executable to run once checked, or why there is none, in words. Reads about 20 MB: off the UI thread.</summary>
    public static (string? Path, string? Problem) Locate(string profile)
    {
        if (Expected is null) return (null, L("Cette version de Mira a été compilée sans Tailscale intégré.", "This build of Mira doesn’t include Built-in Tailscale."));
        var beside = Path.Combine(AppContext.BaseDirectory, Name);
        var found = Check(beside);
        if (found is null) return (beside, null);
#if MIRA_TAILSCALE_EMBEDDED
        return Extract(profile);
#else
        return (null, found);
#endif
    }

    /// <summary>Null when the file is there and is the expected one, otherwise why not.</summary>
    private static string? Check(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Digest(file) == Expected ? null : Replaced;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return Missing; }
        catch (Exception ex) when (Blocked(ex)) { return Quarantined; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Unreadable(ex); }
    }

    /// <summary>
    /// SHA-256 of the executable as it was built: its PE checksum and certificate table entry read as zeros, and nothing
    /// from its certificate table on (an Authenticode signature, SignPath's). A build has zeros there and nothing after
    /// (tools/tailscale/build.mjs checks), so the same file signed since has the same digest. Not a PE: all its bytes.
    /// </summary>
    internal static string Digest(Stream file)
    {
        var head = new byte[4096];
        var read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        var end = file.Length;
        var pe = read >= 0x40 ? BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0x3c)) : -1;
        if (pe > 0 && pe + 24 + 112 + 5 * 8 <= read && head.AsSpan(pe, 4).SequenceEqual("PE\0\0"u8))
        {
            var optional = pe + 24;
            var certificates = optional + (BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(optional)) == 0x20b ? 112 : 96) + 4 * 8;
            var (start, size) = (BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(certificates)), BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(certificates + 4)));
            if (size > 0 && start >= certificates + 8 && start <= end) end = start;
            head.AsSpan(optional + 64, 4).Clear(); head.AsSpan(certificates, 8).Clear();
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(head, 0, (int)Math.Min(read, end));
        var buffer = new byte[81920];
        for (long at = read; at < end;)
        {
            var count = file.Read(buffer, 0, (int)Math.Min(buffer.Length, end - at));
            if (count == 0) break;
            hash.AppendData(buffer, 0, count); at += count;
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

#if MIRA_TAILSCALE_EMBEDDED
    /// <summary>
    /// The copy the single-file Mira carries, written once into its profile under a name of its own version: a copy of
    /// another one still running (a Mira closed abruptly) is never overwritten, and goes once nothing runs it.
    /// </summary>
    private static (string? Path, string? Problem) Extract(string profile)
    {
        var folder = Path.Combine(profile, "app");
        var target = Path.Combine(folder, $"mira-tailscale-{Expected![..12]}.exe");
        if (Check(target) is null) return (target, null);
        var temporary = target + ".tmp";
        try
        {
            using (var resource = typeof(TailnetExecutable).Assembly.GetManifestResourceStream("Mira.App/" + Name))
            {
                if (resource is null) return (null, Missing);
                Directory.CreateDirectory(folder);
                using var file = File.Create(temporary);
                resource.CopyTo(file);
            }
            if (Check(temporary) is { } problem) { TryDelete(temporary); return (null, problem); }
            File.Move(temporary, target, true);
        }
        catch (Exception ex) when (Blocked(ex)) { TryDelete(temporary); return (null, Quarantined); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { TryDelete(temporary); return (null, Unreadable(ex)); }
        foreach (var other in Directory.EnumerateFiles(folder, "mira-tailscale-*.exe"))
            if (!string.Equals(other, target, StringComparison.OrdinalIgnoreCase)) TryDelete(other);
        return (target, null);
    }
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
#endif

    private static string Missing => L("Le fichier mira-tailscale.exe manque dans le dossier de Mira : réinstalle Mira. S’il a été retiré par l’antivirus, l’historique de protection de Sécurité Windows le dit.",
        "mira-tailscale.exe is missing from Mira’s folder: reinstall Mira. If your antivirus removed it, Windows Security’s protection history says so.");
    private static string Replaced => L("Le fichier mira-tailscale.exe n’est pas celui de cette version de Mira : réinstalle Mira.",
        "mira-tailscale.exe isn’t the one from this version of Mira: reinstall Mira.");
    /// <summary>A false positive or not, Mira never asks to exclude it: the Tailscale app does the same job.</summary>
    public static string Quarantined => L("Sécurité Windows bloque mira-tailscale.exe, le Tailscale intégré de Mira : il ne peut pas servir sur ce PC. L’app Tailscale fait la même chose (tailscale.com/download).",
        "Windows Security blocks mira-tailscale.exe, Mira’s Built-in Tailscale: it can’t be used on this PC. The Tailscale app does the same job (tailscale.com/download).");
    private static string Unreadable(Exception ex) => L("Mira ne peut pas lire mira-tailscale.exe : ", "Mira can’t read mira-tailscale.exe: ") + ex.Message;
}
