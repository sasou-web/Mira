using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mira.Core;

/// <summary>A file of a torrent, relative to the download folder, exactly where WebTorrent writes it.</summary>
public sealed record TorrentFileEntry(string RelativePath, long Length);
public sealed record TorrentInfo(string Name, IReadOnlyList<TorrentFileEntry> Files);
/// <summary>
/// A .torrent someone gives Mira: its info hash (SHA-1 of the info dictionary, lower case), name, total size and whether
/// it is private (« private » = 1: a private tracker's, never shared outside its tracker).
/// </summary>
public sealed record TorrentFile(string InfoHash, string Name, long Size, bool Private);

/// <summary>
/// Minimal .torrent reader: only the name and the file list of the info dictionary are extracted. A file larger than
/// <see cref="MaxFileSize"/> or holding more than <see cref="MaxNodes"/> values is refused before it costs memory, and
/// the pieces' hashes, most of a real .torrent, are skipped without being copied.
/// </summary>
public static class Bencode
{
    private const int MaxDepth = 32;
    /// <summary>Real .torrent files stay far below: 20 bytes of hash per piece, a few thousand pieces.</summary>
    public const int MaxFileSize = 10 * 1024 * 1024;
    /// <summary>Values of all kinds: about 50 000 files of a pack, each with its length and a few path parts.</summary>
    public const int MaxNodes = 500_000;

    public static TorrentInfo? ReadTorrent(ReadOnlySpan<byte> data) => ReadTorrent(data, out _);
    /// <summary><paramref name="refused"/>: the file is over the limits above, as opposed to damaged.</summary>
    public static TorrentInfo? ReadTorrent(ReadOnlySpan<byte> data, out bool refused)
    {
        refused = data.Length > MaxFileSize;
        if (refused) return null;
        object root;
        try { int position = 0, nodes = 0; root = Read(data, ref position, 0, ref nodes); }
        catch (LimitException) { refused = true; return null; }
        catch (Exception ex) when (ex is FormatException or OverflowException) { return null; }
        if (root is not Dictionary<string, object> top || top.GetValueOrDefault("info") is not Dictionary<string, object> info) return null;
        var name = Text(info.GetValueOrDefault("name.utf-8")) ?? Text(info.GetValueOrDefault("name"));
        if (string.IsNullOrWhiteSpace(name)) return null;
        var files = new List<TorrentFileEntry>();
        if (info.GetValueOrDefault("files") is List<object> entries)
        {
            foreach (var entry in entries.OfType<Dictionary<string, object>>())
            {
                if (entry.GetValueOrDefault("length") is not long length || length < 0) continue;
                if ((entry.GetValueOrDefault("path.utf-8") ?? entry.GetValueOrDefault("path")) is not List<object> parts || parts.Count == 0) continue;
                var segments = parts.Select(Text).ToList();
                if (segments.Any(x => x is null)) continue;
                if (StoredPath([name, .. segments!]) is { } path) files.Add(new(path, length));
            }
        }
        else if (info.GetValueOrDefault("length") is long single && single >= 0 && StoredPath([name]) is { } path) files.Add(new(path, single));
        return new(name, files);
    }

    /// <summary>
    /// What Mira needs of a .torrent before sending it on: null for anything that isn't one (no info dictionary, no name, no
    /// file), or one over the limits above.
    /// </summary>
    public static TorrentFile? ReadFile(ReadOnlySpan<byte> data)
    {
        if (ReadTorrent(data) is not { Files.Count: > 0 } info || data[0] != (byte)'d') return null;
        // The info dictionary's own bytes, as the file has them: their SHA-1 is the info hash.
        int start = -1, end = -1;
        try
        {
            var position = 1;
            while (position < data.Length && data[position] != (byte)'e')
            {
                var keyStart = position;
                position = End(data, position, 0);
                var key = Key(data[keyStart..position]);
                var valueStart = position;
                position = End(data, position, 0);
                if (key == "info") { start = valueStart; end = position; }
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException) { return null; }
        if (start < 0) return null;
        var dictionary = data[start..end];
        return new(Convert.ToHexString(SHA1.HashData(dictionary)).ToLowerInvariant(), info.Name, info.Files.Sum(x => x.Length), Private(dictionary));
    }

    /// <summary>The text of a byte string key (« 4:info »).</summary>
    private static string Key(ReadOnlySpan<byte> token)
    {
        var colon = token.IndexOf((byte)':');
        return colon < 1 ? "" : Encoding.UTF8.GetString(token[(colon + 1)..]);
    }

    /// <summary>« private » = 1 among the info dictionary's keys (read already: its bytes are well formed).</summary>
    private static bool Private(ReadOnlySpan<byte> info)
    {
        var position = 1;
        while (position < info.Length && info[position] != (byte)'e')
        {
            var keyStart = position;
            position = End(info, position, 0);
            var key = Key(info[keyStart..position]);
            var valueStart = position;
            position = End(info, position, 0);
            if (key == "private") return info[valueStart..position].SequenceEqual("i1e"u8);
        }
        return false;
    }

    /// <summary>Where the value starting at <paramref name="position"/> ends, without reading it.</summary>
    private static int End(ReadOnlySpan<byte> data, int position, int depth)
    {
        if (depth > MaxDepth || position >= data.Length) throw new FormatException();
        var token = data[position];
        if (token == (byte)'i')
        {
            var end = data[(position + 1)..].IndexOf((byte)'e');
            if (end is < 1 or > 20) throw new FormatException();
            return position + end + 2;
        }
        if (token is (byte)'l' or (byte)'d')
        {
            position++;
            while (position < data.Length && data[position] != (byte)'e') position = End(data, position, depth + 1);
            if (position >= data.Length) throw new FormatException();
            return position + 1;
        }
        if (token is >= (byte)'0' and <= (byte)'9')
        {
            var colon = data.Slice(position, Math.Min(11, data.Length - position)).IndexOf((byte)':');
            if (colon < 1 || colon > 10) throw new FormatException();
            var length = int.Parse(Encoding.ASCII.GetString(data.Slice(position, colon)), NumberStyles.None, CultureInfo.InvariantCulture);
            position += colon + 1;
            if (length > data.Length - position) throw new FormatException();
            return position + length;
        }
        throw new FormatException();
    }

    /// <summary>
    /// Mirrors parse-torrent (path.join of the name and path parts, which normalises "." and "..")
    /// and fs-chunk-store (reserved characters removed from the file name only).
    /// </summary>
    private static string? StoredPath(IEnumerable<string> parts)
    {
        var stack = new List<string>();
        foreach (var segment in parts.SelectMany(x => x.Split('/', '\\')))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
            stack.Add(segment);
        }
        if (stack.Count == 0) return null;
        stack[^1] = new string(stack[^1].Where(c => c >= 32 && "<>:\"/\\|?*".IndexOf(c) < 0).ToArray());
        // Joined by hand: Path.Combine would restart from any segment that looks rooted ("C:").
        return stack[^1].Length == 0 ? null : string.Join(Path.DirectorySeparatorChar, stack);
    }

    private static string? Text(object? value) => value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>Raised once a file holds more values than <see cref="MaxNodes"/>.</summary>
    private sealed class LimitException : Exception;

    /// <summary><paramref name="skip"/>: a byte string Mira never reads (the pieces' hashes), passed over without a copy.</summary>
    private static object Read(ReadOnlySpan<byte> data, ref int position, int depth, ref int nodes, bool skip = false)
    {
        if (depth > MaxDepth || position >= data.Length) throw new FormatException();
        if (++nodes > MaxNodes) throw new LimitException();
        var token = data[position];
        if (token == (byte)'i')
        {
            // Twenty characters at most ("-9223372036854775808"): the end is looked for that far only.
            var end = data.Slice(position + 1, Math.Min(22, data.Length - position - 1)).IndexOf((byte)'e');
            if (end is < 1 or > 20) throw new FormatException();
            var number = long.Parse(Encoding.ASCII.GetString(data.Slice(position + 1, end)), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            position += end + 2;
            return number;
        }
        if (token == (byte)'l')
        {
            position++;
            var list = new List<object>();
            while (position < data.Length && data[position] != (byte)'e') list.Add(Read(data, ref position, depth + 1, ref nodes));
            if (position >= data.Length) throw new FormatException();
            position++;
            return list;
        }
        if (token == (byte)'d')
        {
            position++;
            var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
            while (position < data.Length && data[position] != (byte)'e')
            {
                if (Read(data, ref position, depth + 1, ref nodes) is not byte[] key) throw new FormatException();
                var name = Encoding.UTF8.GetString(key);
                dictionary[name] = Read(data, ref position, depth + 1, ref nodes, skip: name == "pieces");
            }
            if (position >= data.Length) throw new FormatException();
            position++;
            return dictionary;
        }
        if (token is >= (byte)'0' and <= (byte)'9')
        {
            var colon = data.Slice(position, Math.Min(11, data.Length - position)).IndexOf((byte)':');
            if (colon < 1 || colon > 10) throw new FormatException();
            var length = int.Parse(Encoding.ASCII.GetString(data.Slice(position, colon)), NumberStyles.None, CultureInfo.InvariantCulture);
            position += colon + 1;
            if (length > data.Length - position) throw new FormatException();
            var bytes = skip ? Array.Empty<byte>() : data.Slice(position, length).ToArray();
            position += length;
            return bytes;
        }
        throw new FormatException();
    }
}
