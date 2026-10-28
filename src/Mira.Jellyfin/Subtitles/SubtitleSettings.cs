using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mira.Core.Subtitles;

namespace Mira.Jellyfin;

/// <summary>
/// The settings of « Sous-titres automatiques », kept by the extension in one file beside the plugins' settings
/// (Mira.subtitles.json): the languages wanted (ISO 639-2, as Jellyfin), what is accepted, and each source with what it
/// needs. The secrets (account, keys) never go back to a device: <see cref="SubtitleConfig.Public"/> says only whether
/// they are set. Free of ASP.NET, for the tests.
/// </summary>
public sealed class SubtitleSettings
{
    /// <summary>Fetch by itself when a title arrives, and try again later (the extension's own loop).</summary>
    public bool Enabled { get; set; }
    public List<string> Languages { get; set; } = [];
    /// <summary>« any », « prefer » or « avoid » (<see cref="HearingPreference"/>).</summary>
    public string Hearing { get; set; } = "any";
    public bool MachineTranslated { get; set; }
    /// <summary>Align the file on the video's audio before keeping it (when Mira can).</summary>
    public bool Sync { get; set; } = true;
    public OpenSubtitlesSettings OpenSubtitles { get; set; } = new();
    public SubDlSettings SubDl { get; set; } = new();

    public HearingPreference HearingPreference => Hearing switch { "prefer" => HearingPreference.Prefer, "avoid" => HearingPreference.Avoid, _ => HearingPreference.Any };
    public MatchPreferences Preferences => new(HearingPreference, MachineTranslated);
}

public sealed class OpenSubtitlesSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>A key of the person's own (an « API consumer » of theirs); Mira's own key serves otherwise.</summary>
    public string ApiKey { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>For the checks: another address than the real service.</summary>
    public string BaseUrl { get; set; } = "";
}

public sealed class SubDlSettings
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string FilesUrl { get; set; } = "";
}

/// <summary>The settings in their file, and the view of them a device gets.</summary>
public sealed class SubtitleConfig
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
    private readonly object _gate = new();
    private readonly string? _file;
    private SubtitleSettings _settings = new();

    public SubtitleConfig(string? file)
    {
        _file = file;
        if (file is not null && File.Exists(file))
        {
            try { _settings = JsonSerializer.Deserialize<SubtitleSettings>(File.ReadAllText(file), Json) ?? new SubtitleSettings(); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { _settings = new SubtitleSettings(); }
        }
    }

    /// <summary>A copy of the settings as they stand.</summary>
    public SubtitleSettings Current
    {
        get { lock (_gate) return JsonSerializer.Deserialize<SubtitleSettings>(JsonSerializer.Serialize(_settings, Json), Json)!; }
    }

    /// <summary>What a device may see: everything but the secrets, said as set or not.</summary>
    public JsonObject Public()
    {
        var s = Current;
        return new JsonObject
        {
            ["enabled"] = s.Enabled,
            ["languages"] = new JsonArray(s.Languages.Select(x => (JsonNode)x).ToArray()),
            ["hearing"] = s.Hearing,
            ["machineTranslated"] = s.MachineTranslated,
            ["sync"] = s.Sync,
            ["openSubtitles"] = new JsonObject
            {
                ["enabled"] = s.OpenSubtitles.Enabled,
                ["ready"] = s.OpenSubtitles.ApiKey.Length > 0 || OpenSubtitlesApi.MiraKey.Length > 0,
                ["ownKey"] = s.OpenSubtitles.ApiKey.Length > 0,
                ["account"] = s.OpenSubtitles.Username.Length > 0 ? s.OpenSubtitles.Username : null,
            },
            ["subDl"] = new JsonObject { ["enabled"] = s.SubDl.Enabled, ["ready"] = s.SubDl.ApiKey.Length > 0 },
        };
    }

    /// <summary>
    /// Changes what a device sends (the fields it gives; a secret given empty is cleared, a secret not given is kept),
    /// and writes the file. Languages are ISO 639-2 codes a source knows; unknown ones are dropped.
    /// </summary>
    public void Update(JsonObject changes)
    {
        lock (_gate)
        {
            if (changes["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var on)) _settings.Enabled = on;
            if (changes["languages"] is JsonArray languages)
                _settings.Languages = languages.Select(x => x is JsonValue v && v.TryGetValue<string>(out var code) ? code.Trim().ToLowerInvariant() : null)
                    .Where(x => x is not null && SubtitleLanguages.Two(x) is not null).Select(x => x!).Distinct().Take(5).ToList();
            if (changes["hearing"] is JsonValue hearing && hearing.TryGetValue<string>(out var h) && h is "any" or "prefer" or "avoid") _settings.Hearing = h;
            if (changes["machineTranslated"] is JsonValue machine && machine.TryGetValue<bool>(out var m)) _settings.MachineTranslated = m;
            if (changes["sync"] is JsonValue sync && sync.TryGetValue<bool>(out var align)) _settings.Sync = align;
            if (changes["openSubtitles"] is JsonObject os)
            {
                if (os["enabled"] is JsonValue e && e.TryGetValue<bool>(out var osOn)) _settings.OpenSubtitles.Enabled = osOn;
                if (Secret(os, "apiKey") is { } key) _settings.OpenSubtitles.ApiKey = key;
                if (Secret(os, "username") is { } user) _settings.OpenSubtitles.Username = user;
                if (Secret(os, "password") is { } password) _settings.OpenSubtitles.Password = password;
            }
            if (changes["subDl"] is JsonObject sd)
            {
                if (sd["enabled"] is JsonValue e && e.TryGetValue<bool>(out var sdOn)) _settings.SubDl.Enabled = sdOn;
                if (Secret(sd, "apiKey") is { } key) _settings.SubDl.ApiKey = key;
            }
            Save();
        }
    }

    private static string? Secret(JsonObject node, string name) => node[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;

    private void Save()
    {
        if (_file is null) return;
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_settings, Json));
        File.Move(temp, _file, true);
    }
}

/// <summary>One thing that happened to a title: found and kept, nothing found, set aside and why.</summary>
public sealed record SubtitleJournalEntry(DateTimeOffset When, string ItemId, string Name, string Language, string Kind, string? Source = null, string? Release = null, int? Score = null, string? Detail = null);

/// <summary>
/// What « Sous-titres automatiques » did, the last <see cref="Keep"/> things, in one file beside the plugins' settings
/// (Mira.subtitles-journal.json), for the Réglages of the apps. Kinds: « kept », « none », « rejected », « failed », « quota ».
/// </summary>
public sealed class SubtitleJournal
{
    public const int Keep = 200;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly object _gate = new();
    private readonly string? _file;
    private List<SubtitleJournalEntry> _entries = [];

    public SubtitleJournal(string? file)
    {
        _file = file;
        if (file is not null && File.Exists(file))
        {
            try { _entries = JsonSerializer.Deserialize<List<SubtitleJournalEntry>>(File.ReadAllText(file), Json) ?? []; }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { _entries = []; }
        }
    }

    public void Add(SubtitleJournalEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > Keep) _entries.RemoveRange(0, _entries.Count - Keep);
            if (_file is null) return;
            try { File.WriteAllText(_file, JsonSerializer.Serialize(_entries, Json)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The most recent first.</summary>
    public List<SubtitleJournalEntry> Recent(int count = 50)
    {
        lock (_gate) return _entries.AsEnumerable().Reverse().Take(count).ToList();
    }
}
