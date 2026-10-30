using System.Text.Json;

namespace Mira.Mac.Services;

/// <summary>
/// Every secret of a profile in one keychain item (« Mira », the profile's folder as its account): the session's token
/// and the key of « Tailscale intégré », as a JSON object by account. Mira is signed ad hoc, so macOS takes each version
/// for another program and asks once per item before it reads it: one item, one question per update. Read once per
/// launch, then kept in memory. The items of earlier versions move in: the session's token was already this item (its
/// bare text), « Mira (Tailscale) » leaves its own item once copied. Only an Apple-issued signature (a Team ID) would
/// remove the question itself: the keychain checks the code's partition, a cdhash for any other signature.
/// </summary>
public sealed class KeychainVault(ITokenStore keychain, string account) : ITokenStore
{
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
            if (items.Count == 0 ? keychain.Delete(account) : keychain.Store(account, JsonSerializer.Serialize(items))) return removed;
            items[name] = before;
            return false;
        }
    }

    /// <summary>The item's entries, read the first time; null when it cannot be read now (asked again next time).</summary>
    private Dictionary<string, string>? Load()
    {
        if (_items is not null) return _items;
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
    private bool Save(Dictionary<string, string> items) => keychain.Store(account, JsonSerializer.Serialize(items));
}
