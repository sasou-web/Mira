using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mira.Core;

public sealed class LibraryStore
{
    private readonly string _connectionString, _path;
    private readonly object _gate = new();
    /// <summary>Jellyfin's default "maximum resume percentage".</summary>
    public const double WatchedThreshold = .9;
    public LibraryStore(string directory, string profile)
    {
        Directory.CreateDirectory(directory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile)))[..24];
        _path = Path.Combine(directory, $"library-{hash}.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _path }.ToString();
        try { CreateSchema(); }
        // A damaged file (power cut, failing disk) would refuse this account at every start. Everything in it but the
        // reports not yet sent comes back from Jellyfin, so a fresh one replaces it and the damaged copy is kept beside.
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) { SetAside(); CreateSchema(); }
        Prune(DateTimeOffset.UtcNow - CacheLifetime);
    }
    /// <summary>Where this opening set aside a database SQLite could not read; null when it was intact.</summary>
    public string? DamagedCopy { get; private set; }
    private void CreateSchema()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS cache (key TEXT PRIMARY KEY, json TEXT NOT NULL, updated TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS progress (item TEXT PRIMARY KEY, ticks INTEGER NOT NULL, updated TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS completed (item TEXT PRIMARY KEY, updated TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS outbox (id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, session TEXT NOT NULL, item TEXT NOT NULL, json TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }
    private void SetAside()
    {
        using (var pooled = new SqliteConnection(_connectionString)) SqliteConnection.ClearPool(pooled);
        DamagedCopy = $"{_path}.bad-{DateTime.UtcNow:yyyyMMddHHmmss}";
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(_path + suffix)) File.Move(_path + suffix, DamagedCopy + suffix, overwrite: true);
    }
    /// <summary>Pages kept for an offline start: each filter, library and sort has its own, so unused ones expire.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    /// <summary>
    /// Forgets cached pages, and local positions already delivered, not updated since <paramref name="before"/>.
    /// The home page, the resume row, the playback history and anything still waiting to be sent stay.
    /// </summary>
    public int Prune(DateTimeOffset before)
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = """
                DELETE FROM cache WHERE updated < $before AND key NOT IN ('home', 'resume', 'recent-playback');
                DELETE FROM progress WHERE updated < $before AND item NOT IN (SELECT item FROM outbox);
                DELETE FROM completed WHERE updated < $before AND item NOT IN (SELECT item FROM outbox);
                """;
            cmd.Parameters.AddWithValue("$before", before.ToUniversalTime().ToString("O"));
            return cmd.ExecuteNonQuery();
        }
    }
    private SqliteConnection Open() { var db = new SqliteConnection(_connectionString); db.Open(); return db; }
    public void Save<T>(string key, T value)
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO cache VALUES ($key,$json,$at)";
            cmd.Parameters.AddWithValue("$key", key); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, Json.Options));
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
        }
    }
    public T? Load<T>(string key)
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT json FROM cache WHERE key=$key"; cmd.Parameters.AddWithValue("$key", key);
            var text = cmd.ExecuteScalar() as string;
            return text is null ? default : JsonSerializer.Deserialize<T>(text, Json.Options);
        }
    }
    public void Enqueue(string kind, PlaybackReport report)
    {
        lock (_gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            // Replace only the tail progress row. A new id avoids deleting a newer update when an in-flight request completes.
            if (kind == "progress")
            {
                using var coalesce = db.CreateCommand(); coalesce.Transaction = tx;
                coalesce.CommandText = "DELETE FROM outbox WHERE id=(SELECT MAX(id) FROM outbox) AND kind='progress' AND session=$session";
                coalesce.Parameters.AddWithValue("$session", report.PlaySessionId); coalesce.ExecuteNonQuery();
            }
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO outbox(kind,session,item,json) VALUES($kind,$session,$item,$json); INSERT OR REPLACE INTO progress VALUES($item,$ticks,$at)";
            // A completed playback is one durable transaction: stop first, then mark watched.
            // Keep two outbox entries so a failed watched request never replays a successful stop.
            var completed = kind == "complete";
            cmd.Parameters.AddWithValue("$kind", completed ? "stop" : kind); cmd.Parameters.AddWithValue("$session", report.PlaySessionId);
            cmd.Parameters.AddWithValue("$item", report.ItemId); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(report, Json.Options));
            cmd.Parameters.AddWithValue("$ticks", completed ? 0 : report.PositionTicks); cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            if (completed)
            {
                cmd.CommandText = "INSERT INTO outbox(kind,session,item,json) VALUES('watched',$session,$item,$json); INSERT OR REPLACE INTO completed VALUES($item,$at)";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
    public PendingReport? Peek()
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,kind,json FROM outbox ORDER BY id LIMIT 1";
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new PendingReport(reader.GetInt64(0), reader.GetString(1), JsonSerializer.Deserialize<PlaybackReport>(reader.GetString(2), Json.Options)!) : null;
        }
    }
    public void Acknowledge(long id)
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM outbox WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
        }
    }
    /// <summary>Drops the local resume point, e.g. after marking a title watched or unwatched from Mira.</summary>
    public void ForgetProgress(string itemId)
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM progress WHERE item=$item; DELETE FROM completed WHERE item=$item";
            cmd.Parameters.AddWithValue("$item", itemId); cmd.ExecuteNonQuery();
            Save("recent-playback", RecentPlayback().Where(x => x.Id != itemId && x.SeriesId != itemId).ToList());
            Save("resume", (Load<List<MediaItem>>("resume") ?? []).Where(x => x.Id != itemId && x.SeriesId != itemId).ToList());
        }
    }
    public List<MediaItem> RecentPlayback() => Load<List<MediaItem>>("recent-playback") ?? [];
    public void RememberPlayback(MediaItem item)
    {
        lock (_gate)
        {
            var history = ContinueWatching.History(RecentPlayback().Append(item)); Save("recent-playback", history);
            // Save the local resume immediately, including while offline or before Jellyfin's next refresh.
            var cached = Load<List<MediaItem>>("resume") ?? [];
            var updated = history.First(x => x.Id == item.Id);
            cached.RemoveAll(x => x.Id == item.Id);
            if (!updated.UserData.Played && updated.UserData.PlaybackPositionTicks > 0) cached.Insert(0, updated);
            Save("resume", ContinueWatching.Order(cached, [], history));
        }
    }
    /// <summary>Bridge a late server response only while the local playback is recent or not acknowledged.</summary>
    public List<MediaItem> MergeResume(IEnumerable<MediaItem> server, IEnumerable<MediaItem> history)
    {
        lock (_gate)
        {
            var result = server.ToList();
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT item FROM outbox";
            using var reader = cmd.ExecuteReader(); var pending = new HashSet<string>();
            while (reader.Read()) pending.Add(reader.GetString(0));
            foreach (var item in history)
            {
                if (item.UserData.LastPlayedDate is not { } playedAt || playedAt < DateTimeOffset.UtcNow.AddMinutes(-1) && !pending.Contains(item.Id)) continue;
                var known = result.FirstOrDefault(x => x.Id == item.Id);
                if (known?.UserData.LastPlayedDate > item.UserData.LastPlayedDate) continue;
                result.RemoveAll(x => x.Id == item.Id);
                if (!item.UserData.Played && item.UserData.PlaybackPositionTicks > 0) result.Add(item with { UserData = item.UserData with { } });
            }
            return result;
        }
    }
    public int PendingCount
    {
        get { lock (_gate) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM outbox"; return Convert.ToInt32(cmd.ExecuteScalar()); } }
    }
    public void ApplyLocalProgress(IEnumerable<MediaItem> items)
    {
        lock (_gate)
        {
            using var db = Open();
            foreach (var item in items)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT ticks, EXISTS(SELECT 1 FROM completed WHERE item=$item AND (updated > $cutoff OR EXISTS(SELECT 1 FROM outbox WHERE item=$item AND kind='watched'))), updated FROM progress WHERE item=$item AND (updated > $cutoff OR EXISTS(SELECT 1 FROM outbox WHERE item=$item))";
                cmd.Parameters.AddWithValue("$item", item.Id); cmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) continue;
                var ticks = reader.GetInt64(0); var completed = reader.GetBoolean(1);
                var playedAt = DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture);
                if (item.UserData.LastPlayedDate > playedAt) continue;
                item.UserData.LastPlayedDate = playedAt;
                // Like Jellyfin, a stop past 90 % means watched: resuming there would only replay the credits.
                if (completed || item.RunTimeTicks is > 0 && ticks >= item.RunTimeTicks.Value * WatchedThreshold) { item.UserData.PlaybackPositionTicks = 0; item.UserData.Played = true; }
                else item.UserData.PlaybackPositionTicks = ticks;
            }
        }
    }
}
