namespace Mira.Core.Updates;

/// <summary>Why Mira for Mac cannot replace itself where it runs: the update then waits as a verified disk image.</summary>
public enum MacInstallProblem
{
    None,
    /// <summary>Opened straight from a download: macOS runs it from a hidden read-only copy (App Translocation).</summary>
    Translocated,
    /// <summary>Opened from the disk image itself, not from Applications.</summary>
    DiskImage,
    /// <summary>A folder this account cannot change (Applications of an administrator, seen from a standard account).</summary>
    ReadOnly
}

/// <summary>How an installation started at the last quit ended, written by the update script and read at the next launch.</summary>
public sealed record MacInstallResult(bool Installed, Version Version, string Step);

/// <summary>
/// The parts of Mira for Mac's automatic update that do not need a Mac, so that they are checked everywhere: where the new
/// Mira.app waits (<see cref="Staging"/>, beside the bundle, on the same volume, so that the swap is a rename), whether the
/// bundle can be replaced at all (<see cref="Problem"/>), and the script that swaps it once Mira has quit
/// (<see cref="Script"/>). The disk image itself is checked like every update: size and SHA-256 from the signed manifest.
/// </summary>
public static class MacInstall
{
    public const string ResultName = "mac-result";

    /// <summary>The new bundle, copied out of the disk image and checked, hidden beside the running one.</summary>
    public static string Staging(string bundle) => Beside(bundle, "update");
    /// <summary>The running bundle, moved aside during the swap, then deleted.</summary>
    public static string Previous(string bundle) => Beside(bundle, "old");
    private static string Beside(string bundle, string suffix)
    {
        var full = Path.GetFullPath(bundle).TrimEnd('/');
        return Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{suffix}");
    }

    /// <summary>
    /// Whether <paramref name="bundle"/> can be replaced: its folder and its contents writable by this account (tried by
    /// writing a file in each, which is what the swap needs), outside a disk image and outside App Translocation.
    /// </summary>
    public static MacInstallProblem Problem(string bundle)
    {
        var full = Path.GetFullPath(bundle).TrimEnd('/');
        if (full.Contains("/AppTranslocation/", StringComparison.Ordinal)) return MacInstallProblem.Translocated;
        var writable = Writable(Path.GetDirectoryName(full)!) && Writable(Path.Combine(full, "Contents"));
        if (writable) return MacInstallProblem.None;
        return full.StartsWith("/Volumes/", StringComparison.Ordinal) ? MacInstallProblem.DiskImage : MacInstallProblem.ReadOnly;
    }
    private static bool Writable(string folder)
    {
        var probe = Path.Combine(folder, $".mira-write-{Guid.NewGuid():N}");
        try { using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { } return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally { try { File.Delete(probe); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>What to tell about <paramref name="problem"/>, with what to do instead.</summary>
    public static string Text(MacInstallProblem problem) => problem switch
    {
        MacInstallProblem.Translocated => L("Mira tourne depuis une copie que macOS protège : glisse Mira dans Applications, puis ouvre-la de là pour que ses mises à jour s’installent seules.",
            "Mira is running from a copy macOS protects: drag Mira to Applications, then open it from there so that its updates install by themselves."),
        MacInstallProblem.DiskImage => L("Mira tourne depuis son image disque : glisse Mira dans Applications, puis ouvre-la de là pour que ses mises à jour s’installent seules.",
            "Mira is running from its disk image: drag Mira to Applications, then open it from there so that its updates install by themselves."),
        MacInstallProblem.ReadOnly => L("Ce compte ne peut pas modifier Mira là où elle est : ouvre l’image disque et glisse Mira dans Applications.",
            "This account can’t change Mira where it is: open the disk image and drag Mira to Applications."),
        _ => ""
    };

    /// <summary>
    /// The script that swaps the bundles, run by /bin/sh with its values as arguments, never written into it:
    /// $1 Mira's process, $2 the bundle, $3 the new one, $4 where the old one goes, $5 the result file, $6 the version,
    /// $7 what opens the bundle again (/usr/bin/open), empty to leave Mira closed. It waits for Mira to quit (2 minutes at
    /// most), moves the old bundle aside, the new one in its place, and puts the old one back if the second move fails.
    /// The result file says how it ended, in one line (<see cref="ReadResult"/>).
    /// </summary>
    // Line ends written as \n whatever the checkout made of this file (CRLF for .cs): /bin/sh reads a \r as text.
    public static readonly string Script = """
        pid=$1 app=$2 new=$3 old=$4 result=$5 version=$6 open=$7
        i=0
        while kill -0 "$pid" 2>/dev/null; do
          i=$((i + 1))
          if [ "$i" -gt 600 ]; then echo "failed $version waiting" > "$result"; exit 1; fi
          sleep 0.2
        done
        rm -rf "$old" 2>/dev/null
        if [ ! -d "$new/Contents" ]; then echo "failed $version missing" > "$result"
        elif ! mv "$app" "$old"; then echo "failed $version move-old" > "$result"
        elif ! mv "$new" "$app"; then
          if mv "$old" "$app"; then echo "failed $version move-new" > "$result"; else echo "failed $version restore" > "$result"; app=$old; fi
        else
          echo "done $version" > "$result"
          rm -rf "$old" 2>/dev/null
        fi
        if [ -n "$open" ]; then exec "$open" "$app"; fi
        """.ReplaceLineEndings("\n");

    /// <summary>The arguments of /bin/sh for <see cref="Script"/>.</summary>
    public static string[] Arguments(int process, string bundle, string resultFile, Version version, string? opener) =>
        ["-c", Script, "mira-update", process.ToString(System.Globalization.CultureInfo.InvariantCulture), bundle, Staging(bundle), Previous(bundle), resultFile, version.ToString(3), opener ?? ""];

    /// <summary>The last result, once: the file is deleted as it is read. Null when there is none, or it cannot be read.</summary>
    public static MacInstallResult? ReadResult(string folder)
    {
        var path = Path.Combine(folder, ResultName);
        try
        {
            if (!File.Exists(path)) return null;
            var parts = File.ReadAllText(path).Trim().Split(' ', 3);
            File.Delete(path);
            if (parts.Length < 2 || ReleaseFeed.ParseVersion(parts[1]) is not { } version) return null;
            return parts[0] switch
            {
                "done" => new MacInstallResult(true, version, ""),
                "failed" => new MacInstallResult(false, version, parts.Length > 2 ? parts[2] : ""),
                _ => null
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
