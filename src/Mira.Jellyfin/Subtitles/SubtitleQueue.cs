using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mira.Jellyfin;

/// <summary>
/// A title waiting for subtitles in one language: when it arrived, when it is tried next, how many tries so far.
/// Backlog: a title that was already in the library, asked for once (« Chercher aussi pour ce qui est déjà là »).
/// </summary>
public sealed record SubtitleWait(string ItemId, string Language, DateTimeOffset FirstSeen, DateTimeOffset Next, int Attempts, bool Backlog = false);

/// <summary>
/// The titles « Sous-titres automatiques » still looks for, one entry per title and language, kept in one file beside
/// the plugins' settings (Mira.subtitles-queue.json) so a restart of Jellyfin loses nothing. A new title is tried
/// <see cref="FirstDelay"/> after it arrives (Jellyfin has read its streams by then), then again after each delay of
/// <see cref="Delays"/> (subtitles of a new episode often come within hours or days), then every week, until
/// <see cref="GiveUpAfter"/> after it arrived. Free of Jellyfin, for the tests.
/// </summary>
public sealed class SubtitleQueue
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan[] Delays = [TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromDays(1), TimeSpan.FromDays(3), TimeSpan.FromDays(7)];
    public static readonly TimeSpan Week = TimeSpan.FromDays(7);
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(30);
    /// <summary>A whole library added at once waits too, up to this many entries (the oldest go first).</summary>
    public const int Keep = 5000;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly object _gate = new();
    private readonly string? _file;
    private readonly Func<DateTimeOffset> _now;
    private List<SubtitleWait> _waits = [];

    public SubtitleQueue(string? file, Func<DateTimeOffset>? now = null)
    {
        _file = file;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        if (file is not null && File.Exists(file))
        {
            try { _waits = JsonSerializer.Deserialize<List<SubtitleWait>>(File.ReadAllText(file), Json) ?? []; }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { _waits = []; }
        }
    }

    public int Count
    {
        get { lock (_gate) return _waits.Count; }
    }

    /// <summary>The next try after <paramref name="attempts"/> tries (the last one now), or null when it is time to give up.</summary>
    public static DateTimeOffset? NextAttempt(DateTimeOffset firstSeen, int attempts, DateTimeOffset now)
    {
        var next = now + (attempts >= 1 && attempts <= Delays.Length ? Delays[attempts - 1] : Week);
        return next - firstSeen > GiveUpAfter ? null : next;
    }

    /// <summary>A title that arrived, for one language; a title already waiting for it keeps its place.</summary>
    public void Add(string itemId, string language)
    {
        lock (_gate)
        {
            if (_waits.Any(x => x.ItemId == itemId && x.Language == language)) return;
            var now = _now();
            _waits.Add(new SubtitleWait(itemId, language, now, now + FirstDelay, 0));
            if (_waits.Count > Keep) _waits.RemoveRange(0, _waits.Count - Keep);
            Save();
        }
    }

    /// <summary>
    /// The titles already in the library, asked for once each (never tried again, they would hold the new ones back),
    /// after those waiting already. Those already waiting keep their place. Answers how many were added.
    /// </summary>
    public int AddBacklog(IEnumerable<(string ItemId, string Language)> titles)
    {
        lock (_gate)
        {
            var now = _now();
            var known = _waits.Select(x => (x.ItemId, x.Language)).ToHashSet();
            var room = Keep - _waits.Count;
            var added = 0;
            foreach (var title in titles)
            {
                if (added >= room) break;
                if (!known.Add(title)) continue;
                _waits.Add(new SubtitleWait(title.ItemId, title.Language, now, now + FirstDelay, 0, true));
                added++;
            }
            if (added > 0) Save();
            return added;
        }
    }

    /// <summary>What is due now, the longest waiting first.</summary>
    public List<SubtitleWait> Due(int count)
    {
        var now = _now();
        lock (_gate) return _waits.Where(x => x.Next <= now).OrderBy(x => x.Next).Take(count).ToList();
    }

    /// <summary>Found, or no longer wanted (title gone, language no longer asked for).</summary>
    public void Done(string itemId, string language) => Change(itemId, language, _ => null);

    /// <summary>A title gone from the library: every language.</summary>
    public void Forget(string itemId)
    {
        lock (_gate)
            if (_waits.RemoveAll(x => x.ItemId == itemId) > 0) Save();
    }

    /// <summary>Tried and nothing kept: tried again later, or given up (false).</summary>
    public bool Retry(string itemId, string language)
    {
        var now = _now();
        lock (_gate)
        {
            var i = _waits.FindIndex(x => x.ItemId == itemId && x.Language == language);
            if (i < 0) return false;
            var wait = _waits[i];
            var next = wait.Backlog ? null : NextAttempt(wait.FirstSeen, wait.Attempts + 1, now);
            if (next is { } at) _waits[i] = wait with { Next = at, Attempts = wait.Attempts + 1 };
            else _waits.RemoveAt(i);
            Save();
            return next is not null;
        }
    }

    /// <summary>Not tried (a source's quota spent, nothing set to ask): tried at <paramref name="until"/>, the try not counted.</summary>
    public void Postpone(string itemId, string language, DateTimeOffset until) => Change(itemId, language, w => w with { Next = until });

    private void Change(string itemId, string language, Func<SubtitleWait, SubtitleWait?> change)
    {
        lock (_gate)
        {
            var i = _waits.FindIndex(x => x.ItemId == itemId && x.Language == language);
            if (i < 0) return;
            if (change(_waits[i]) is { } changed) _waits[i] = changed;
            else _waits.RemoveAt(i);
            Save();
        }
    }

    private void Save()
    {
        if (_file is null) return;
        try
        {
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_waits, Json));
            File.Move(temp, _file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
