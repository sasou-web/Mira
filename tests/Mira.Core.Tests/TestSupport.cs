using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Mira.Core;
using Mira.Core.Updates;

/// <summary>What the checks share: assertions, stand-ins for Jellyfin and GitHub, frozen copies of older readers.</summary>
internal static class TestSupport
{
    public static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    /// <summary>The repository's folder (the one with Mira.sln), for the checks that read its files; null outside it.</summary>
    public static string? Repository()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var folder = new DirectoryInfo(start); folder is not null; folder = folder.Parent)
                if (File.Exists(Path.Combine(folder.FullName, "Mira.sln"))) return folder.FullName;
        return null;
    }
    public static PlaybackReport Report(long position = 0) => new() { ItemId = "item-1", MediaSourceId = "source-1", PlaySessionId = "session-1", PositionTicks = position };
    public static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    public static async Task WaitUntil(Func<bool> ready, int seconds = 4) { var timeout = DateTime.UtcNow.AddSeconds(seconds); while (!ready()) { if (DateTime.UtcNow > timeout) throw new TimeoutException(); await Task.Delay(20); } }
    public static Dictionary<string, string> EnvironmentBlock()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && key.Length > 0 && !key.StartsWith('=') && entry.Value is string value) values[key] = value;
        return values;
    }
    // Minimal bencode writer for synthetic .torrent metadata (dictionary keys sorted, as the format requires).
    public static byte[] Ben(object value)
    {
        using var stream = new MemoryStream();
        void Write(object item)
        {
            switch (item)
            {
                case long number: stream.Write(Encoding.ASCII.GetBytes($"i{number}e")); break;
                case string text: var bytes = Encoding.UTF8.GetBytes(text); stream.Write(Encoding.ASCII.GetBytes($"{bytes.Length}:")); stream.Write(bytes); break;
                case IEnumerable<KeyValuePair<string, object>> map: stream.WriteByte((byte)'d'); foreach (var (key, entry) in map.OrderBy(x => x.Key, StringComparer.Ordinal)) { Write(key); Write(entry); } stream.WriteByte((byte)'e'); break;
                case System.Collections.IEnumerable list: stream.WriteByte((byte)'l'); foreach (var entry in list) Write(entry!); stream.WriteByte((byte)'e'); break;
            }
        }
        Write(value);
        return stream.ToArray();
    }
    public static byte[] TorrentBytes(string name, (string[] Path, long Length)[] files, bool single = false)
    {
        var info = new Dictionary<string, object> { ["name"] = name, ["piece length"] = 16384L, ["pieces"] = "" };
        if (single) info["length"] = files[0].Length;
        else info["files"] = files.Select(f => (object)new Dictionary<string, object> { ["length"] = f.Length, ["path"] = f.Path.Cast<object>().ToList() }).ToList();
        return Ben(new Dictionary<string, object> { ["announce"] = "udp://tracker.invalid:1337", ["info"] = info });
    }
    // Terminal text without VT control sequences (cursor moves, colours, titles).
    public static string Plain(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\x1b\[[0-9;?<>=]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-_]", "");
    // A Jellyfin repository manifest as tools/web/package.sh writes it, with what the checks look at made to vary.
    public static string JellyfinRepository(string version, string sourceUrl, string checksum, string? guid = null, string name = "Mira", int versions = 1) =>
        JsonSerializer.Serialize(new[]
        {
            new
            {
                guid = guid ?? MiraWeb.PluginId.ToString(), name, description = "Mira sur iPhone", overview = "Mira", owner = "sasou-web", category = "General",
                versions = Enumerable.Range(0, versions).Select(i => new { version, changelog = "", targetAbi = "10.9.0.0", sourceUrl, checksum, timestamp = "2026-10-10T00:00:00Z" }).ToArray()
            }
        });
    // UpdateManifest.Parse as Mira 0.7.4 shipped it (commit 066d1ae), frozen: what installed copies up to 0.7.4 still read.
    public static (Version Version, List<UpdateFile> Files) Parse074(byte[] json)
    {
        static string Text(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        static long Number(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : -1;
        string[] kinds = ["installer", "portable", "zip"];
        var fileName = new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var hex = new System.Text.RegularExpressions.Regex("^[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var product = Text(root, "product");
            var version = ReleaseFeed.ParseVersion(Text(root, "version")) ?? throw new UpdateException("Le manifeste de mise à jour n’indique pas de version valide.");
            if (product != "Mira") throw new UpdateException("Le manifeste de mise à jour ne concerne pas Mira.");
            if (!root.TryGetProperty("files", out var list) || list.ValueKind != JsonValueKind.Array) throw new UpdateException("Le manifeste de mise à jour ne liste aucun fichier.");
            var files = new List<UpdateFile>();
            foreach (var entry in list.EnumerateArray())
            {
                var file = new UpdateFile(Text(entry, "kind"), Text(entry, "name"), Number(entry, "size"), Text(entry, "sha256").ToLowerInvariant());
                if (!kinds.Contains(file.Kind) || !fileName.IsMatch(file.Name) || file.Size is <= 0 or > 1L << 30 || !hex.IsMatch(file.Sha256) || files.Any(f => f.Kind == file.Kind))
                    throw new UpdateException("Le manifeste de mise à jour contient un fichier invalide.");
                files.Add(file);
            }
            if (files.Count == 0) throw new UpdateException("Le manifeste de mise à jour ne liste aucun fichier.");
            return (version, files);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new UpdateException("Le manifeste de mise à jour est illisible."); }
    }
    // UpdateManifest.Parse as Mira 0.7.5 ships it (lot 5b of the review), frozen: what installed 0.7.5 copies will read in
    // every later release's manifest to update themselves. Its "others" reading must stay lenient.
    public static UpdateManifest Parse075(byte[] json)
    {
        static string Text(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        static long Number(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : -1;
        string[] kinds = ["installer", "portable", "zip"], otherKinds = ["mac", "jellyfin"];
        var fileName = new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var hex = new System.Text.RegularExpressions.Regex("^[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        UpdateFile? Entry(JsonElement entry, string[] allowed, List<UpdateFile> files)
        {
            var file = new UpdateFile(Text(entry, "kind"), Text(entry, "name"), Number(entry, "size"), Text(entry, "sha256").ToLowerInvariant());
            return allowed.Contains(file.Kind) && fileName.IsMatch(file.Name) && file.Size is > 0 and <= 1L << 30 && hex.IsMatch(file.Sha256) && !files.Any(f => f.Kind == file.Kind) ? file : null;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var product = Text(root, "product");
            var version = ReleaseFeed.ParseVersion(Text(root, "version")) ?? throw new UpdateException("Le manifeste de mise à jour n’indique pas de version valide.");
            if (product != "Mira") throw new UpdateException("Le manifeste de mise à jour ne concerne pas Mira.");
            if (!root.TryGetProperty("files", out var list) || list.ValueKind != JsonValueKind.Array) throw new UpdateException("Le manifeste de mise à jour ne liste aucun fichier.");
            var files = new List<UpdateFile>();
            foreach (var entry in list.EnumerateArray())
                files.Add(Entry(entry, kinds, files) ?? throw new UpdateException("Le manifeste de mise à jour contient un fichier invalide."));
            if (files.Count == 0) throw new UpdateException("Le manifeste de mise à jour ne liste aucun fichier.");
            var others = new List<UpdateFile>();
            if (root.TryGetProperty("others", out var more) && more.ValueKind == JsonValueKind.Array)
                foreach (var entry in more.EnumerateArray())
                    if (!(entry.ValueKind == JsonValueKind.Object && !otherKinds.Contains(Text(entry, "kind"))) && Entry(entry, otherKinds, others) is { } file) others.Add(file);
            return new UpdateManifest(product, version, files) { Others = others };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new UpdateException("Le manifeste de mise à jour est illisible."); }
    }
}
/// <summary>Progress reported on the caller's thread, in order (Progress<T> would post it to the thread pool).</summary>
sealed class SyncProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
/// <summary>The macOS keychain for the Mac profile's tests: keeps tokens in memory, or refuses everything.</summary>
sealed class TokenStandIn : Mira.Mac.Services.ITokenStore
{
    public Dictionary<string, string> Tokens { get; } = [];
    public Dictionary<string, string> Labels { get; } = [];
    /// <summary>Refuses everything (locked, question declined); only storing; only removing.</summary>
    public bool Refuse { get; set; }
    public bool RefuseStore { get; set; }
    public bool RefuseDelete { get; set; }
    public bool Missing { get; private set; }
    public bool Store(string account, string token, string label = "Mira") { if (Refuse || RefuseStore) return false; Tokens[account] = token; Labels[account] = label; return true; }
    public string? Read(string account) { Missing = !Refuse && !Tokens.ContainsKey(account); return Refuse ? null : Tokens.GetValueOrDefault(account); }
    public bool Delete(string account) { if (Refuse || RefuseDelete) return false; Tokens.Remove(account); return true; }
}
sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request).WaitAsync(cancellationToken); }
