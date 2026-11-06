using System.IO;
using System.Text.Json;

namespace Mira.Mac.Services;

/// <summary>
/// Every secret of a profile in one keychain item (« Mira », the profile's folder as its account): the session's token
/// and the key of « Tailscale intégré », as a JSON object by account. Mira is signed ad hoc, so macOS takes each version
/// for another program and asks once per item before it reads it: one item, one question per update. Read once per
/// launch, then kept in memory. The items of earlier versions move in: the session's token was already this item (its
/// bare text), « Mira (Tailscale) » leaves its own item once copied. Only an Apple-issued signature (a Team ID) would
/// remove the question itself: the keychain checks the code's partition, a cdhash for any other signature. So when Mira
/// installs its own update, the version leaving hands the entries over (<see cref="HandOver"/>): the new one makes its
/// own item of them, and macOS has nothing to ask (an update made by Homebrew or by hand still asks once).
/// </summary>
public sealed class KeychainVault(ITokenStore keychain, string account) : ITokenStore
{
    /// <summary>The entries handed over by the version before, in the profile's folder (the account), of this macOS account only.</summary>
    public const string HandOverName = "keychain-handover";
    private readonly string _handOver = Path.Combine(account, HandOverName);
    private readonly Lock _lock = new();
    private Dictionary<string, string>? _items;
    // Accounts whose own item of an earlier version was looked for: once per launch.
    private readonly HashSet<string> _looked = [];

    public bool Missing { get; private set; }

    public string? Read(string name)
    {
        lock (_lock)
        {
            Missing = false;
            if (Load() is not { } items) return null;
            if (items.TryGetValue(name, out var value)) return value;
            if (name != account && _looked.Add(name))
            {
                // An item of its own, from an earlier version: copied in, then removed.
                var old = keychain.Read(name);
                if (old is null && !keychain.Missing) { _looked.Remove(name); return null; }
                if (old is not null)
                {
                    items[name] = old;
                    if (Save(items)) keychain.Delete(name);
                    return old;
                }
            }
            Missing = true;
            return null;
        }
    }

    public bool Store(string name, string token, string label = "Mira")
    {
        lock (_lock)
        {
            // Unreadable now (locked, question declined): writing would drop what the item holds for the other accounts.
            if (Load() is not { } items) return false;
            var before = items.GetValueOrDefault(name);
            items[name] = token;
            if (Save(items)) return true;
            if (before is null) items.Remove(name); else items[name] = before;
            return false;
        }
    }

    public bool Delete(string name)
    {
        lock (_lock)
        {
            if (Load() is not { } items) return false;
            // An item of its own from an earlier version goes too, or it would come back at the next launch.
            var removed = name == account || keychain.Delete(name);
            if (!items.TryGetValue(name, out var before)) return removed;
            items.Remove(name);
            if (items.Count == 0 ? Kept(keychain.Delete(account)) : Save(items)) return removed;
            items[name] = before;
            return false;
        }
    }

    /// <summary>
    /// At the quit that installs the new version (Updates.InstallAtQuit): macOS would ask the Mac's password before that
    /// copy reads this item, which trusts this one only. The entries leave the item for a file of this macOS account only
    /// (as session.json when the keychain refuses the token), read at the next launch and put back in an item of its own
    /// (<see cref="Load"/>). Nothing when the item was not read in this launch: there would be nothing to give.
    /// </summary>
    public bool HandOver()
    {
        lock (_lock)
        {
            if (_items is not { Count: > 0 } items) return false;
            var temporary = _handOver + ".tmp";
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporary, options)) JsonSerializer.Serialize(stream, items);
                File.Move(temporary, _handOver, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Forget(temporary); return false; }
            // Left there, it would only make macOS ask: the file is the newest copy now, read first at the next launch.
            keychain.Delete(account);
            return true;
        }
    }

    /// <summary>The item's entries, read the first time; null when it cannot be read now (asked again next time).</summary>
    private Dictionary<string, string>? Load()
    {
        if (_items is not null) return _items;
        // Handed over by the version before this one: an item of this copy's own, never the question for the old one.
        if (HandedOver() is { } handed)
        {
            _items = handed;
            Save(handed);
            return _items;
        }
        var text = keychain.Read(account);
        if (text is null) return keychain.Missing ? _items = [] : null;
        return _items = Parse(text) ?? new() { [account] = text };
    }
    /// <summary>The entries of a JSON object; null for the bare token an earlier version kept there.</summary>
    private static Dictionary<string, string>? Parse(string text)
    {
        if (!text.StartsWith('{')) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(text) ?? []; }
        // Damaged: nothing kept, each secret is made or asked again.
        catch (JsonException) { return []; }
    }
    // A new item each time (Keychain.Store): it trusts this copy of Mira, which reads it without a question until the next version.
    private bool Save(Dictionary<string, string> items) => Kept(keychain.Store(account, JsonSerializer.Serialize(items)));
    /// <summary>The keychain has the entries (or their removal): a handed-over file left by a refused store would bring older ones back.</summary>
    private bool Kept(bool done)
    {
        if (done) Forget(_handOver);
        return done;
    }
    /// <summary>The entries the version before left at its quit; null without them, or unreadable (the keychain is read instead).</summary>
    private Dictionary<string, string>? HandedOver()
    {
        try
        {
            if (!File.Exists(_handOver)) return null;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(_handOver)) is { Count: > 0 } entries ? entries : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    private static void Forget(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
