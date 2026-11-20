using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mira.Jellyfin;

/// <summary>
/// « Inviter un ami » by a link (ogsas, 2026-11-20, maquette A « Le lien »): the administrator makes a link for a
/// friend's account; it carries a Tailscale key for one device, which is also the invitation, (<see cref="HomeTailnet.FriendTag"/>, or the home's when
/// Tailscale refuses friends' own) and an invitation, single-use, that ends with the key (<see cref="Life"/>). The
/// friend's Mira joins the administrator's tailnet with the key, reaches Jellyfin, and trades the invitation for a
/// session of that account (<see cref="FriendController"/>): no address, password nor code to type. Never an
/// administrator's account. What it keeps, in one file beside the plugins' settings: each invitation by the hash of its
/// token (used ones a while, so a second try reads « already used »), and each device that came in, with its address on
/// the tailnet to delete its node when it is taken out. A device that used a link waits for the administrator to let it in
/// (ogsas, 2026-11-20: « securiser ce lien »): a link forwarded or stolen opens nothing without that click. Wrong invitations are counted here, in memory, per address and
/// in all. Free of ASP.NET, on a clock of its own, for the tests.
/// </summary>
public sealed class FriendInvites
{
    /// <summary>How long an invitation (and its key) can be used, once.</summary>
    public static readonly TimeSpan Life = HomeTailnet.LinkLife;
    /// <summary>How long a used or ended invitation is remembered, to say so rather than « wrong ».</summary>
    public static readonly TimeSpan Remembered = TimeSpan.FromDays(30);
    /// <summary>Invitations not used yet, devices of friends, at once.</summary>
    public const int MaxOpen = 30, MaxDevices = 100;
    /// <summary>Wrong invitations from one address, then from all, within <see cref="HomeFamily.TryWindow"/>.</summary>
    public const int TriesPerAddress = 10, TriesInAll = 30;
    /// <summary>The prefix of the Jellyfin device id of a friend's session opened by an invitation: one per device.</summary>
    public const string DevicePrefix = "mira-friend-";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string? _file;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _tries = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _allTries = new();
    private Store _store = new();

    public FriendInvites(string? file, Func<DateTimeOffset> now)
    {
        _file = file;
        _now = now;
        Load();
    }

    /// <summary>What became of an invitation brought by a device.</summary>
    public enum Redeeming { Done, Wrong, Used, Ended, TooMany, Full }

    /// <summary>Whether Tailscale refused a key of friends' own tag to the home's OAuth client: the home's tag is used instead.</summary>
    public bool FriendTagRefused
    {
        get { lock (_gate) return _store.FriendTagRefused; }
        set
        {
            lock (_gate)
            {
                if (_store.FriendTagRefused != value)
                {
                    _store.FriendTagRefused = value;
                    Save();
                }
            }
        }
    }

    /// <summary>
    /// A new invitation for an account, whose token is <paramref name="token"/> (the link's Tailscale key: the link carries
    /// nothing else secret), kept only by its hash; or a new token when none is given. Null when too many are still open.
    /// </summary>
    public (string Token, FriendInvite Invite)? Invite(string user, string name, string keyId, string tag, DateTimeOffset until, string? token = null)
    {
        lock (_gate)
        {
            Prune();
            if (_store.Invites.Count(x => x.Used is null && x.Until > _now()) >= MaxOpen)
            {
                return null;
            }

            token ??= GuestRoutes.NewPassword().Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var invite = new FriendInvite
            {
                Id = NewId(),
                User = user,
                Name = name,
                Token = GuestRoutes.TokenHash(token),
                KeyId = keyId,
                Tag = tag,
                Created = _now(),
                Until = until,
            };
            _store.Invites.Add(invite);
            Save();
            return (token, invite.Copy());
        }
    }

    /// <summary>
    /// An invitation brought by a device (<paramref name="device"/>, its app's own id) from <paramref name="from"/>: used
    /// now, and the device written down for its account. The same device coming again for the same account takes the
    /// place of its earlier entry, returned to end its session.
    /// </summary>
    public (Redeeming Result, FriendInvite? Invite, FriendDevice? Device, FriendDevice? Replaced) Redeem(string? token, string device, string name, string from) =>
        Redeem(token, device, name, from, out _);

    /// <summary>
    /// As <see cref="Redeem(string?, string, string, string)"/>, the device waiting for the administrator to let it in
    /// (« Laisser entrer »): <paramref name="wait"/> is the token it asks with (kept only by its hash) until then.
    /// </summary>
    public (Redeeming Result, FriendInvite? Invite, FriendDevice? Device, FriendDevice? Replaced) Redeem(string? token, string device, string name, string from, out string? wait)
    {
        wait = null;
        lock (_gate)
        {
            var now = _now();
            if (!Try(from, now))
            {
                return (Redeeming.TooMany, null, null, null);
            }

            if (string.IsNullOrEmpty(token) || token.Length > 128)
            {
                return (Redeeming.Wrong, null, null, null);
            }

            var hash = GuestRoutes.TokenHash(token);
            var invite = _store.Invites.FirstOrDefault(x => Same(x.Token, hash));
            if (invite is null)
            {
                return (Redeeming.Wrong, null, null, null);
            }

            // A right invitation is not a wrong try.
            Untry(from);
            if (invite.Used is not null)
            {
                return (Redeeming.Used, invite.Copy(), null, null);
            }

            if (now >= invite.Until || invite.Cancelled)
            {
                return (Redeeming.Ended, invite.Copy(), null, null);
            }

            var replaced = _store.Devices.FirstOrDefault(x => x.Device == device && x.User == invite.User);
            if (replaced is null && _store.Devices.Count >= MaxDevices)
            {
                return (Redeeming.Full, invite.Copy(), null, null);
            }

            if (replaced is not null)
            {
                _store.Devices.Remove(replaced);
            }

            var entry = new FriendDevice
            {
                Id = NewId(),
                User = invite.User,
                Device = device,
                Name = name,
                Joined = now,
                Invite = invite.Id,
                // Joined through the tailnet (the link's key): the node to delete from it when the device is taken out.
                Tailnet = HomeTailnet.TailnetAddress(from),
            };
            wait = GuestRoutes.NewPassword().Replace('+', '-').Replace('/', '_').TrimEnd('=');
            entry.Wait = GuestRoutes.TokenHash(wait);
            _store.Devices.Add(entry);
            invite.Used = now;
            invite.Device = entry.Id;
            Save();
            return (Redeeming.Done, invite.Copy(), entry.Copy(), replaced?.Copy());
        }
    }

    /// <summary>A redeemed invitation given back: Jellyfin did not open the session, the friend may try again with the same link.</summary>
    public void Undo(FriendInvite invite, FriendDevice device)
    {
        lock (_gate)
        {
            if (_store.Invites.FirstOrDefault(x => x.Id == invite.Id) is { } kept && kept.Device == device.Id)
            {
                kept.Used = null;
                kept.Device = null;
            }

            _store.Devices.RemoveAll(x => x.Id == device.Id);
            Save();
        }
    }

    /// <summary>
    /// The device that asks with this wait token, and whether it may come in: null when it was refused, taken out, or the
    /// token is not one. A device left waiting past its invitation's end goes.
    /// </summary>
    public FriendDevice? Waiting(string? wait)
    {
        if (string.IsNullOrEmpty(wait) || wait.Length > 128)
        {
            return null;
        }

        var hash = GuestRoutes.TokenHash(wait);
        lock (_gate)
        {
            var device = _store.Devices.FirstOrDefault(x => x.Wait is { } kept && Same(kept, hash));
            if (device is null)
            {
                return null;
            }

            var invite = _store.Invites.FirstOrDefault(x => x.Id == device.Invite);
            if (!device.Approved && (invite is null || _now() >= invite.Until))
            {
                _store.Devices.Remove(device);
                Save();
                return null;
            }

            return device.Copy();
        }
    }

    /// <summary>A waiting device let in by the administrator: its entry; null when it is not there.</summary>
    public FriendDevice? Approve(string id)
    {
        lock (_gate)
        {
            var device = _store.Devices.FirstOrDefault(x => x.Id == id);
            if (device is null)
            {
                return null;
            }

            device.Approved = true;
            device.Approval = _now();
            Save();
            return device.Copy();
        }
    }

    /// <summary>The session of a device let in was handed over: its wait token stops working.</summary>
    public void Delivered(string id)
    {
        lock (_gate)
        {
            if (_store.Devices.FirstOrDefault(x => x.Id == id) is { } device)
            {
                device.Wait = null;
                Save();
            }
        }
    }

    /// <summary>Every invitation still remembered, latest first.</summary>
    public IReadOnlyList<FriendInvite> Invites()
    {
        lock (_gate)
        {
            Prune();
            return _store.Invites.OrderByDescending(x => x.Created).Select(x => x.Copy()).ToList();
        }
    }

    /// <summary>Every device of friends, latest first.</summary>
    public IReadOnlyList<FriendDevice> Devices()
    {
        lock (_gate) return _store.Devices.OrderByDescending(x => x.Joined).Select(x => x.Copy()).ToList();
    }

    /// <summary>An invitation not used yet cancelled: its entry, to revoke its key; null when it is not there or was used.</summary>
    public FriendInvite? Cancel(string id)
    {
        lock (_gate)
        {
            var invite = _store.Invites.FirstOrDefault(x => x.Id == id);
            if (invite is null || invite.Used is not null)
            {
                return null;
            }

            invite.Cancelled = true;
            Save();
            return invite.Copy();
        }
    }

    /// <summary>A device of a friend taken out: its entry, to end its session and delete its node; null when it was not there.</summary>
    public FriendDevice? RemoveDevice(string id)
    {
        lock (_gate)
        {
            var device = _store.Devices.FirstOrDefault(x => x.Id == id);
            if (device is null)
            {
                return null;
            }

            _store.Devices.Remove(device);
            Save();
            return device;
        }
    }

    /// <summary>
    /// Everything of an account gone (the friend removed, or their account deleted): its invitations not used yet, to
    /// revoke their keys, and its devices, to end their sessions and delete their nodes.
    /// </summary>
    public (IReadOnlyList<FriendInvite> Open, IReadOnlyList<FriendDevice> Devices) Forget(string user)
    {
        lock (_gate)
        {
            var now = _now();
            var open = _store.Invites.Where(x => x.User == user && x.Used is null && !x.Cancelled && x.Until > now).Select(x => x.Copy()).ToList();
            var devices = _store.Devices.Where(x => x.User == user).ToList();
            _store.Invites.RemoveAll(x => x.User == user);
            _store.Devices.RemoveAll(x => x.User == user);
            Save();
            return (open, devices);
        }
    }

    /// <summary>The Jellyfin device id of a friend's device: what its session is opened under, and ended by.</summary>
    public static string JellyfinDevice(FriendDevice device) => DevicePrefix + device.Id;

    // ---- The link ----

    /// <summary>
    /// A friend's link, short (<see cref="Mira.Core.Family.JoinLink"/>): this PC's Tailscale address and port, Jellyfin's
    /// path, and the key, which is the invitation too. Null when the address is not one Mira can write.
    /// </summary>
    public static string? Link(string server, int port, string? path, string key) =>
        System.Net.IPAddress.TryParse(server, out var address) ? Mira.Core.Family.JoinLink.Make(Mira.Core.Family.JoinLink.FriendKind, address, port, path, key) : null;

    // ---- Inside ----

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private void Prune()
    {
        var now = _now();
        if (_store.Invites.RemoveAll(x => now - (x.Used ?? x.Until) >= Remembered) > 0)
        {
            Save();
        }
    }

    private bool Try(string from, DateTimeOffset now)
    {
        var since = now - HomeFamily.TryWindow;
        while (_allTries.Count > 0 && _allTries.Peek() <= since)
        {
            _allTries.Dequeue();
        }

        foreach (var key in _tries.Where(x => x.Value.All(t => t <= since)).Select(x => x.Key).ToList())
        {
            _tries.Remove(key);
        }

        if (!_tries.TryGetValue(from, out var mine))
        {
            _tries[from] = mine = new Queue<DateTimeOffset>();
        }

        if (mine.Count >= TriesPerAddress || _allTries.Count >= TriesInAll)
        {
            return false;
        }

        mine.Enqueue(now);
        _allTries.Enqueue(now);
        return true;
    }

    private void Untry(string from)
    {
        if (_tries.TryGetValue(from, out var mine) && mine.Count > 0)
        {
            // The latest try was this one: it no longer counts.
            var kept = mine.Take(mine.Count - 1).ToList();
            mine.Clear();
            foreach (var time in kept)
            {
                mine.Enqueue(time);
            }
        }

        if (_allTries.Count > 0)
        {
            var kept = _allTries.Take(_allTries.Count - 1).ToList();
            _allTries.Clear();
            foreach (var time in kept)
            {
                _allTries.Enqueue(time);
            }
        }
    }

    private static bool Same(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private void Load()
    {
        if (_file is null || !File.Exists(_file))
        {
            return;
        }

        try
        {
            ServerSecrets.Restrict(_file);
            _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(_file), Json) ?? new Store();
            _store.Invites.RemoveAll(x => string.IsNullOrEmpty(x.Id) || string.IsNullOrEmpty(x.Token) || HomeFamily.Plain(x.User) is null);
            _store.Devices.RemoveAll(x => string.IsNullOrEmpty(x.Id) || HomeFamily.Plain(x.User) is null);
            foreach (var device in _store.Devices)
            {
                device.Tailnet = HomeTailnet.TailnetAddress(device.Tailnet);
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable: put aside. Every invitation stops working (never one left open by a damaged file); the friends'
            // sessions stay Jellyfin's.
            try { File.Move(_file, _file + ".bad-" + _now().ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), true); }
            catch (Exception moving) when (moving is IOException or UnauthorizedAccessException) { }
            _store = new Store();
        }
    }

    private void Save()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            // Hashes only (passwords, codes, tokens), but a code of 4 digits is soon found from its hash: Jellyfin's account's alone.
            ServerSecrets.Write(_file, JsonSerializer.Serialize(_store, Json));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Kept in memory until the next change.
        }
    }

    internal sealed class Store
    {
        public bool FriendTagRefused { get; set; }
        public List<FriendInvite> Invites { get; set; } = [];
        public List<FriendDevice> Devices { get; set; } = [];
    }
}

/// <summary>An invitation of a friend: for which account, until when, used or not.</summary>
public sealed class FriendInvite
{
    public string Id { get; set; } = "";
    /// <summary>The Jellyfin account's id (32 hexadecimal digits).</summary>
    public string User { get; set; } = "";
    /// <summary>The account's name when the link was made.</summary>
    public string Name { get; set; } = "";
    /// <summary>The hash of its token (<see cref="GuestRoutes.TokenHash"/>).</summary>
    public string Token { get; set; } = "";
    /// <summary>Its Tailscale key's id, to revoke it when the invitation is cancelled.</summary>
    public string KeyId { get; set; } = "";
    /// <summary>The tag its key carries.</summary>
    public string Tag { get; set; } = HomeTailnet.FriendTag;
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Until { get; set; }
    public DateTimeOffset? Used { get; set; }
    public bool Cancelled { get; set; }
    /// <summary>The device that used it.</summary>
    public string? Device { get; set; }

    public FriendInvite Copy() => (FriendInvite)MemberwiseClone();
}

/// <summary>A device of a friend, come in by an invitation.</summary>
public sealed class FriendDevice
{
    public string Id { get; set; } = "";
    /// <summary>The Jellyfin account's id.</summary>
    public string User { get; set; } = "";
    /// <summary>Its app's own device id.</summary>
    public string Device { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset Joined { get; set; }
    /// <summary>The invitation it came with.</summary>
    public string? Invite { get; set; }
    /// <summary>Its address on the tailnet, when it came through it: its node, deleted when it is taken out.</summary>
    public string? Tailnet { get; set; }
    /// <summary>Let in by the administrator (« Laisser entrer »): only then is its session opened.</summary>
    public bool Approved { get; set; }
    public DateTimeOffset? Approval { get; set; }
    /// <summary>The hash of the token it asks with while it waits, until its session is handed over.</summary>
    public string? Wait { get; set; }

    public FriendDevice Copy() => (FriendDevice)MemberwiseClone();
}
