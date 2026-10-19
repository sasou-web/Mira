using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mira.Core;

/// <summary>
/// « Tes choix gardés sur ton Jellyfin »: the choices of the account that follow it from one device to another (the
/// tracks of each series, the titles removed from « Continuer à regarder », the languages and the intros; the quality
/// of Mira web), kept in Jellyfin's display preferences. Each entry has its time in milliseconds; the newest wins. The
/// rules of Mira web's choices.js, with their cases shared in tests/fixtures/choices.json.
/// </summary>
public static class AccountChoices
{
    /// <summary>The custom preference that holds them, in the display preferences « mira » of the client « mira ».</summary>
    public const string Preference = "mira.choices";
    public const string DisplayPreferences = "mira";
    /// <summary>The series whose tracks are kept, and the titles removed or put back, newest first.</summary>
    public const int TrackLimit = 100, HiddenLimit = 200;
    /// <summary>
    /// The settings kept on Jellyfin, with Mira's defaults: a setting never changed anywhere is not written. Those of the
    /// tracks follow Mira's language (<see cref="PlayerSettings.DefaultAudioLanguage"/>): a device that never chose does not
    /// impose its language's order on the others.
    /// </summary>
    public static IReadOnlyDictionary<string, JsonNode> Settings => new Dictionary<string, JsonNode>
    {
        ["audioLanguages"] = JsonValue.Create(PlayerSettings.DefaultAudioLanguage),
        ["subtitleLanguages"] = JsonValue.Create(PlayerSettings.DefaultSubtitleLanguage),
        ["autoSkip"] = JsonValue.Create(false),
        ["quality"] = JsonValue.Create("auto"),
    };
    private static readonly string[] Sections = ["settings", "tracks", "hidden"];

    /// <summary>Equal as JSON, whatever the order of the keys.</summary>
    public static bool Same(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is JsonObject x && b is JsonObject y)
            return x.Count == y.Count && x.All(p => y.TryGetPropertyValue(p.Key, out var other) && Same(p.Value, other));
        if (a is JsonArray l && b is JsonArray r) return l.Count == r.Count && l.Zip(r).All(p => Same(p.First, p.Second));
        if (a is not JsonValue || b is not JsonValue) return false;
        var (ka, kb) = (a.GetValueKind(), b.GetValueKind());
        if (ka != kb) return false;
        return ka switch
        {
            JsonValueKind.Number => Number(a) == Number(b),
            JsonValueKind.String => a.GetValue<string>() == b.GetValue<string>(),
            _ => true,
        };
    }

    /// <summary>A series' tracks as every device keeps them; <c>subtitles</c> false: subtitles off.</summary>
    public static JsonObject Track(JsonNode? x)
    {
        string? Text(string name) => x is JsonObject o && o.TryGetPropertyValue(name, out var v) && v?.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: > 0 } s ? s : null;
        bool Flag(string name) => x is JsonObject o && o.TryGetPropertyValue(name, out var v) && v?.GetValueKind() == JsonValueKind.True;
        var on = Flag("subtitles");
        return new JsonObject
        {
            ["audio"] = Text("audio"), ["audioTitle"] = Text("audioTitle"), ["subtitles"] = on,
            ["subtitle"] = on ? Text("subtitle") : null, ["subtitleTitle"] = on ? Text("subtitleTitle") : null, ["forced"] = on && Flag("forced"),
        };
    }
    public static JsonObject Track(TrackMemory memory) => Track(new JsonObject
    {
        ["audio"] = memory.Audio, ["audioTitle"] = memory.AudioTitle, ["subtitles"] = memory.Subtitles,
        ["subtitle"] = memory.Subtitle, ["subtitleTitle"] = memory.SubtitleTitle, ["forced"] = memory.Forced,
    });
    public static TrackMemory Memory(JsonNode? x)
    {
        var t = Track(x);
        return new((string?)t["audio"], (string?)t["audioTitle"], (bool)t["subtitles"]!, (string?)t["subtitle"], (string?)t["subtitleTitle"], (bool)t["forced"]!);
    }

    /// <summary>What a document read from Jellyfin (or kept on this device) holds; anything unreadable counts as nothing.</summary>
    public static JsonObject Read(JsonNode? node)
    {
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            try { node = JsonNode.Parse(value.GetValue<string>()); } catch (JsonException) { node = null; }
        var doc = node is JsonObject o ? (JsonObject)o.DeepClone() : [];
        foreach (var section in Sections) if (doc[section] is not JsonObject) doc[section] = new JsonObject();
        return doc;
    }
    public static JsonObject Read(string? text)
    {
        try { return Read(string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text)); }
        catch (JsonException) { return Read((JsonNode?)null); }
    }

    /// <summary>A number, whether Jellyfin's JSON or Mira's own (a long kept as it is).</summary>
    private static double Number(JsonNode? x) => x is JsonValue v && v.GetValueKind() == JsonValueKind.Number
        ? v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? d : v.TryGetValue<decimal>(out var m) ? (double)m : double.NaN
        : double.NaN;
    private static long Time(JsonNode? x) => x is JsonObject o && Number(o["at"]) is var d && double.IsFinite(d) ? (long)d : 0;

    /// <summary>
    /// The document of this device with what changed here since it was last kept: <paramref name="local"/> is what the
    /// device uses now ({ settings: { key: value }, tracks: { series: tracks }, hidden: { id: time in ms } }). A change
    /// made here takes <paramref name="now"/>; the first time (a document never kept), what this device had before is the
    /// oldest choice (0), so what Jellyfin already keeps wins over it, and a setting left as Mira's default is not written.
    /// </summary>
    public static JsonObject Capture(JsonNode? doc, JsonObject local, long now)
    {
        var result = Read(doc);
        var first = result["seen"]?.GetValueKind() != JsonValueKind.True;
        var settings = (JsonObject)result["settings"]!; var tracks = (JsonObject)result["tracks"]!; var hidden = (JsonObject)result["hidden"]!;
        if (local["settings"] is JsonObject mySettings)
            foreach (var (key, value) in mySettings)
            {
                var known = settings[key] as JsonObject;
                var @base = known is not null ? known["value"] : Settings.GetValueOrDefault(key);
                if (!Same(value, @base)) settings[key] = new JsonObject { ["value"] = value?.DeepClone(), ["at"] = first ? 0 : now };
            }
        if (local["tracks"] is JsonObject myTracks)
            foreach (var (id, value) in myTracks)
            {
                var mine = Track(value);
                if (tracks[id] is JsonObject known && Same(Track(known), mine)) continue;
                mine["at"] = first ? 0 : now; tracks[id] = mine;
            }
        var removed = local["hidden"] as JsonObject ?? [];
        foreach (var (id, value) in removed)
        {
            var at = Number(value) is var d && double.IsFinite(d) ? (long)d : 0;
            if (hidden[id] is not JsonObject known || at > Time(known)) hidden[id] = new JsonObject { ["hidden"] = true, ["at"] = at };
        }
        // Put back here: « Continuer à regarder » shows it again on the other devices too.
        foreach (var id in hidden.Where(p => p.Value?["hidden"]?.GetValueKind() == JsonValueKind.True && !removed.ContainsKey(p.Key)).Select(p => p.Key).ToList())
            hidden[id] = new JsonObject { ["hidden"] = false, ["at"] = now };
        result["seen"] = true;
        return result;
    }

    /// <summary>The newest <paramref name="limit"/> entries, newest first (then by key, the same on every device).</summary>
    private static JsonObject Newest(JsonObject entries, int limit)
    {
        var kept = new JsonObject();
        foreach (var (key, value) in entries.OrderByDescending(p => Time(p.Value)).ThenBy(p => p.Key, StringComparer.Ordinal).Take(limit).ToList())
            kept[key] = value?.DeepClone();
        return kept;
    }

    /// <summary>
    /// This device's document and Jellyfin's, together: each entry from the side that changed it last (Jellyfin's when
    /// both did at the same time). What Jellyfin keeps that Mira does not know (a later version's) stays.
    /// </summary>
    public static JsonObject Merge(JsonNode? doc, JsonNode? remote)
    {
        var mine = Read(doc); var theirs = Read(remote);
        var merged = (JsonObject)theirs.DeepClone();
        merged.Remove("seen");
        foreach (var section in Sections)
        {
            var all = (JsonObject)mine[section]!.DeepClone();
            foreach (var (key, value) in (JsonObject)theirs[section]!)
            {
                if (value is not JsonObject) continue;
                if (all[key] is not JsonObject known || Time(value) >= Time(known)) all[key] = value.DeepClone();
            }
            merged[section] = section == "settings" ? all : Newest(all, section == "tracks" ? TrackLimit : HiddenLimit);
        }
        if (mine["seen"]?.GetValueKind() == JsonValueKind.True) merged["seen"] = true;
        return merged;
    }

    /// <summary>Whether Jellyfin must be told: the document differs from what it keeps.</summary>
    public static bool Differs(JsonNode? merged, JsonNode? remote)
    {
        var a = Read(merged); var b = Read(remote);
        return Sections.Any(section => !Same(a[section], b[section]));
    }

    /// <summary>A value this version of Mira can use for a setting it keeps on Jellyfin.</summary>
    public static bool Fits(string key, JsonNode? value)
    {
        if (!Settings.TryGetValue(key, out var standard) || value is not JsonValue) return false;
        var kind = value.GetValueKind();
        if (key == "quality") return kind == JsonValueKind.String ? value.GetValue<string>() is "auto" or "max" : kind == JsonValueKind.Number && Number(value) > 0;
        var wanted = standard.GetValueKind();
        return kind == wanted || (kind is JsonValueKind.True or JsonValueKind.False && wanted is JsonValueKind.True or JsonValueKind.False);
    }

    /// <summary>
    /// What the device uses from a document: the settings it holds, the tracks of each series (oldest first, as the device
    /// keeps them) and the titles removed, with when.
    /// </summary>
    public static JsonObject Apply(JsonNode? doc)
    {
        var all = Read(doc);
        var settings = new JsonObject();
        foreach (var (key, entry) in (JsonObject)all["settings"]!)
            if (entry is JsonObject e && Fits(key, e["value"])) settings[key] = e["value"]!.DeepClone();
        IEnumerable<KeyValuePair<string, JsonNode?>> ByTime(JsonObject entries) => entries.OrderBy(p => Time(p.Value)).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
        var tracks = new JsonObject();
        foreach (var (id, value) in ByTime((JsonObject)all["tracks"]!)) tracks[id] = Track(value);
        var hidden = new JsonObject();
        foreach (var (id, value) in ByTime((JsonObject)all["hidden"]!))
            if (value?["hidden"]?.GetValueKind() == JsonValueKind.True) hidden[id] = Time(value);
        return new JsonObject { ["settings"] = settings, ["tracks"] = tracks, ["hidden"] = hidden };
    }

    /// <summary>Where this device keeps its document, in the account's store.</summary>
    public const string Kept = "account-choices";
    /// <summary>What a sync brought this device: the settings to use now (null where unchanged) and the titles removed
    /// from « Continuer à regarder » when they changed (the store has them already, as it has the tracks).</summary>
    public sealed record Update(string? AudioLanguage, string? SubtitleLanguage, bool? AutoSkip, Dictionary<string, DateTimeOffset>? Hidden)
    {
        public bool SettingsChanged => AudioLanguage is not null || SubtitleLanguage is not null || AutoSkip is not null;
    }

    /// <summary>What Mira for Windows and Mac use now, in the shape of <see cref="Capture"/>.</summary>
    public static JsonObject Local(string audioLanguage, string subtitleLanguage, bool autoSkip, IEnumerable<(string Series, TrackMemory Tracks)> tracks, IReadOnlyDictionary<string, DateTimeOffset> hidden)
    {
        var mine = new JsonObject();
        foreach (var (series, memory) in tracks) mine[series] = Track(memory);
        var removed = new JsonObject();
        foreach (var (id, at) in hidden) removed[id] = at.ToUnixTimeMilliseconds();
        return new JsonObject
        {
            ["settings"] = new JsonObject { ["audioLanguages"] = audioLanguage, ["subtitleLanguages"] = subtitleLanguage, ["autoSkip"] = autoSkip },
            ["tracks"] = mine, ["hidden"] = removed,
        };
    }

    /// <summary>
    /// Keeps this device and Jellyfin in step: what changed here goes to Jellyfin, what changed elsewhere comes here. The
    /// tracks and the titles removed are written to <paramref name="store"/>; the settings (read with <paramref name="settings"/>,
    /// on the caller's thread) come back in the update, for the caller to use and save. Errors of the network or of the
    /// store go to the caller.
    /// </summary>
    public static async Task<Update> SyncAsync(JellyfinClient client, LibraryStore store, Func<(string Audio, string Subtitles, bool AutoSkip)> settings, CancellationToken ct)
    {
        async Task<(JsonObject Local, (string Audio, string Subtitles, bool AutoSkip) Settings, List<string> Order)> ReadAsync()
        {
            var current = settings();
            var (tracks, hidden) = await Task.Run(() => (store.AllTracks(), store.HiddenFromResume()), ct);
            return (Local(current.Audio, current.Subtitles, current.AutoSkip, tracks, hidden), current, tracks.Select(x => x.Series).ToList());
        }
        var doc = await Task.Run(() => store.Load<JsonObject>(Kept), ct);
        var mine = Capture(doc, (await ReadAsync()).Local, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        // What changed here keeps its time even if Jellyfin does not answer now.
        if (!Same(mine, doc)) await Task.Run(() => store.Save(Kept, mine), ct);
        var preferences = await client.ChoicesAsync(ct);
        var remote = Read(preferences["CustomPrefs"]?[Preference]);
        // A choice made here while Jellyfin answered counts too.
        var (local, current, order) = await ReadAsync();
        mine = Capture(mine, local, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var merged = Merge(mine, remote);
        var used = Apply(merged);
        var values = (JsonObject)used["settings"]!;
        string? Text(string key, string now) => values[key] is JsonNode v && v.GetValue<string>() is var text && text != now ? text : null;
        bool? Flag(string key, bool now) => values[key] is JsonNode v && v.GetValue<bool>() is var flag && flag != now ? flag : null;
        var usedTracks = ((JsonObject)used["tracks"]!).Select(p => (p.Key, Memory(p.Value))).ToList();
        var usedHidden = ((JsonObject)used["hidden"]!).ToDictionary(p => p.Key, p => DateTimeOffset.FromUnixTimeMilliseconds(p.Value!.GetValue<long>()));
        var tracksChanged = !Same(used["tracks"], local["tracks"]) || !usedTracks.Select(x => x.Key).SequenceEqual(order);
        var hiddenChanged = !Same(used["hidden"], local["hidden"]);
        await Task.Run(() =>
        {
            if (tracksChanged) store.ReplaceTracks(usedTracks);
            if (hiddenChanged) store.ReplaceHidden(usedHidden);
            store.Save(Kept, merged);
        }, ct);
        if (Differs(merged, remote))
        {
            var shared = (JsonObject)merged.DeepClone(); shared.Remove("seen");
            if (preferences["CustomPrefs"] is not JsonObject custom) preferences["CustomPrefs"] = custom = [];
            custom[Preference] = shared.ToJsonString();
            await client.SaveChoicesAsync(preferences, ct);
        }
        return new(Text("audioLanguages", current.Audio), Text("subtitleLanguages", current.Subtitles), Flag("autoSkip", current.AutoSkip), hiddenChanged ? usedHidden : null);
    }
}
