using System.Collections.Concurrent;
using System.IO;

namespace Mira.Desktop.Services;

/// <summary>
/// Files Mira needs on disk: mpv's key bindings and the Shell icon. The folder distribution ships them beside
/// Mira.exe; the single-file Mira.exe carries embedded copies and writes them once into its profile ("app" folder).
/// </summary>
internal static class AppFiles
{
    private static readonly ConcurrentDictionary<string, string> Resolved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Profile of this run, set at startup; the default one beside the executable otherwise.</summary>
    public static string ProfileDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "data");

    public static string InputConf => Resolve("mira-input.conf");
    public static string Icon => Resolve(Path.Combine("Assets", "mira-mark-v1.ico"));

    private static string Resolve(string relative) => Resolved.GetOrAdd(relative, key =>
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, key);
        if (File.Exists(shipped)) return shipped;
        var name = Path.GetFileName(key);
        var target = Path.Combine(ProfileDirectory, "app", name);
        try
        {
            using var resource = typeof(AppFiles).Assembly.GetManifestResourceStream("Mira.App/" + name);
            if (resource is null) return shipped;
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            var bytes = buffer.ToArray();
            // Rewritten only when missing or from another version, atomically.
            if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + ".tmp";
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, target, true);
            }
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return File.Exists(target) ? target : shipped; }
    });
}
