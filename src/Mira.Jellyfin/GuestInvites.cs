using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mira.Jellyfin;

/// <summary>
/// « Inviter par lien »: the links an administrator made for guests, each with its Jellyfin account (hidden, made for
/// it, deleted with it). A link serves one device, the first that opens it; it lasts until the host takes it back, or
/// until it went <see cref="Unused"/> without serving. Kept in one file beside the plugins' settings, so that a
/// restart of Jellyfin keeps the links sent; the session tokens only by their hash. Free of ASP.NET, on a clock of its
/// own, for the tests (<see cref="GuestController"/> and <see cref="InvitesController"/> serve it, the plugin's own
/// in Plugin.Invites).
/// </summary>
public sealed class GuestInvites
{
    /// <summary>A link that served nothing for this long stops working, and its account goes.</summary>
    public static readonly TimeSpan Unused = TimeSpan.FromDays(7);
    /// <summary>Links at once: each is an account on Jellyfin.</summary>
    public const int MaxInvites = 20;
    /// <summary>Devices one party link lets in at once (each its own guest account).</summary>
    public const int MaxPartyGuests = 12;
    /// <summary>Openings of a link tried from one address, then from all, within a minute.</summary>
    public const int TriesPerAddress = 10, TriesInAll = 60;
    // A link in use is written down again at most this often: its last use only moves its end.
    private static readonly TimeSpan Touch = TimeSpan.FromMinutes(10);

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

    public GuestInvites(string? file, Func<DateTimeOffset> now)
    {
        _file = file;
        _now = now;
        Load();
    }

    /// <summary>The address the guests' links start with (« https://pc.tailnet.ts.net »); null until it is given.</summary>
    public string? Address
    {
        get { lock (_gate) { return _store.Address; } }
    }

    /// <summary>The API key of Jellyfin that deletes the accounts of links gone unused when no administrator is there.</summary>
    public string? Key
    {
        get { lock (_gate) { return _store.Key; } }
        set { lock (_gate) { _store.Key = value; Save(); } }
    }

    /// <summary>
    /// The address the links start with: HTTPS, a name and nothing after it but « / » or « /Mira »; null takes it away.
    /// False, nothing changed, for any other.
    /// </summary>
    public bool SetAddress(string? address)
    {
        var clean = CleanAddress(address);
        if (address is not null && clean is null)
        {
            return false;
        }

        lock (_gate)
        {
            _store.Address = clean;
            Save();
        }

        return true;
    }

    /// <summary>The origin of an address as the links use it; null when it is not HTTPS to a name.</summary>
    public static string? CleanAddress(string? address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.HostNameType != UriHostNameType.Dns || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length > 0 && !path.Equals("/Mira", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    /// <summary>The link a guest opens: Mira web at the address, the code after « # », never sent to a server.</summary>
    public static string? Link(string? address, string code) => address is null ? null : $"{address}/Mira/#/invite/{code}";

    /// <summary>Every link, the newest first.</summary>
    public IReadOnlyList<GuestInvite> All()
    {
        lock (_gate)
        {
            return [.. _store.Invites.OrderByDescending(x => x.Created).Select(x => x.Copy())];
        }
    }

    public int Count
    {
        get { lock (_gate) { return _store.Invites.Count; } }
    }

    /// <summary>
    /// A new link, its account made (<paramref name="userId"/>, <paramref name="password"/>); for a guest come by a
    /// party link, that link's code and the device it is for, from the start.
    /// </summary>
    public GuestInvite Add(string name, string userId, string password, string hostId, string hostName, string? party = null, string? device = null)
    {
        lock (_gate)
        {
            var now = _now();
            var invite = new GuestInvite
            {
                Id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
                Code = GuestRoutes.NewCode(), Name = name, UserId = userId, Password = password, HostId = hostId, HostName = hostName,
                Created = now, Used = now, Party = party, Device = device,
            };
            _store.Invites.Add(invite);
            Save();
            return invite.Copy();
        }
    }

    /// <summary>A link taken back: no longer in the list, its account to delete. Null when there is none.</summary>
    public GuestInvite? Remove(string id)
    {
        lock (_gate)
        {
            var invite = _store.Invites.FirstOrDefault(x => x.Id == id);
            if (invite is null)
            {
                return null;
            }

            _store.Invites.Remove(invite);
            _store.Gone.Add(invite.UserId);
            Save();
            return invite.Copy();
        }
    }

    /// <summary>Links gone unused: out of the list, their accounts to delete. Then every account still to delete.</summary>
    public IReadOnlyList<string> Sweep()
    {
        lock (_gate)
        {
            var now = _now();
            var old = _store.Invites.Where(x => now - x.Used >= Unused).ToList();
            foreach (var invite in old)
            {
                _store.Invites.Remove(invite);
                _store.Gone.Add(invite.UserId);
            }

            if (old.Count > 0)
            {
                Save();
            }

            return [.. _store.Gone];
        }
    }

    /// <summary>An account deleted from Jellyfin (or already gone there): nothing more to do for it.</summary>
    public void Deleted(string userId)
    {
        lock (_gate)
        {
            if (_store.Gone.Remove(userId))
            {
                Save();
            }
        }
    }

    /// <summary>
    /// « Ta party »: the one link of a host (<paramref name="hostId"/>), made the first time it is asked for, the same
    /// afterwards, whoever opens it: each device that opens it gets a guest of its own (<see cref="OpenParty"/>).
    /// </summary>
    public GuestParty PartyOf(string hostId, string hostName)
    {
        lock (_gate)
        {
            var party = _store.Parties.FirstOrDefault(x => x.HostId == hostId);
            if (party is null)
            {
                party = new GuestParty { Code = GuestRoutes.NewCode(), HostId = hostId, HostName = hostName, Created = _now() };
                _store.Parties.Add(party);
                Save();
            }
            else if (party.HostName != hostName)
            {
                // Renamed on Jellyfin: their groups carry the new name.
                party.HostName = hostName;
                Save();
            }

            return party.Copy();
        }
    }

    /// <summary>
    /// A host's party link made again: the old one stops at once, and the guests it let in go, with their accounts.
    /// </summary>
    public GuestParty NewParty(string hostId, string hostName)
    {
        lock (_gate)
        {
            var old = _store.Parties.FirstOrDefault(x => x.HostId == hostId);
            if (old is not null)
            {
                _store.Parties.Remove(old);
                foreach (var guest in _store.Invites.Where(x => x.Party is not null && Same(x.Party, old.Code)).ToList())
                {
                    _store.Invites.Remove(guest);
                    _store.Gone.Add(guest.UserId);
                }
            }

            var party = new GuestParty { Code = GuestRoutes.NewCode(), HostId = hostId, HostName = hostName, Created = _now() };
            _store.Parties.Add(party);
            Save();
            return party.Copy();
        }
    }

    /// <summary>The party of a code; null for any other code.</summary>
    public GuestParty? Party(string? code)
    {
        if (!GuestRoutes.IsCode(code))
        {
            return null;
        }

        lock (_gate)
        {
            return _store.Parties.FirstOrDefault(x => Same(x.Code, code!))?.Copy();
        }
    }

    /// <summary>The guests a party link let in, the newest first.</summary>
    public IReadOnlyList<GuestInvite> Guests(string partyCode)
    {
        lock (_gate)
        {
            return [.. _store.Invites.Where(x => x.Party is not null && Same(x.Party, partyCode)).OrderByDescending(x => x.Created).Select(x => x.Copy())];
        }
    }

    /// <summary>
    /// A party link opened by a device: its guest when this device came before (<see cref="Opening.Opened"/>), else a
    /// guest to make (<see cref="Opening.Join"/>, the party given), unless the code is no party, the party is full,
    /// every link is taken, or too many tries came within a minute.
    /// </summary>
    public (Opening Result, GuestInvite? Invite, GuestParty? Party) OpenParty(string? code, string device, string from)
    {
        lock (_gate)
        {
            var now = _now();
            if (!Try(from, now))
            {
                return (Opening.TooMany, null, null);
            }

            var party = GuestRoutes.IsCode(code) ? _store.Parties.FirstOrDefault(x => Same(x.Code, code!)) : null;
            if (party is null)
            {
                return (Opening.Unknown, null, null);
            }

            var mine = _store.Invites.FirstOrDefault(x => x.Party is not null && Same(x.Party, party.Code) && x.Device == device && now - x.Used < Unused);
            if (mine is not null)
            {
                return (Opening.Opened, mine.Copy(), party.Copy());
            }

            if (_store.Invites.Count >= MaxInvites || _store.Invites.Count(x => x.Party is not null && Same(x.Party, party.Code)) >= MaxPartyGuests)
            {
                return (Opening.Full, null, party.Copy());
            }

            return (Opening.Join, null, party.Copy());
        }
    }

    public enum Opening { Opened, Unknown, OtherDevice, TooMany, Join, Full }

    /// <summary>
    /// A link opened by a device: refused when its code is no link (or one gone unused), when it already served another
    /// device, or when too many tries came within a minute (<paramref name="from"/>: the address they come from).
    /// </summary>
    public (Opening Result, GuestInvite? Invite) Open(string? code, string device, string from)
    {
        lock (_gate)
        {
            var now = _now();
            if (!Try(from, now))
            {
                return (Opening.TooMany, null);
            }

            var invite = GuestRoutes.IsCode(code) ? _store.Invites.FirstOrDefault(x => Same(x.Code, code!)) : null;
            if (invite is null || now - invite.Used >= Unused)
            {
                return (Opening.Unknown, null);
            }

            if (invite.Device is not null && invite.Device != device)
            {
                return (Opening.OtherDevice, null);
            }

            return (Opening.Opened, invite.Copy());
        }
    }

    /// <summary>
    /// The session Jellyfin gave the device that opened the link: the link is that device's from now on, and only this
    /// session goes through the gateway. False when the link went meanwhile, or another device took it.
    /// </summary>
    public bool Opened(string id, string device, string deviceName, string token)
    {
        lock (_gate)
        {
            var invite = _store.Invites.FirstOrDefault(x => x.Id == id);
            if (invite is null || (invite.Device is not null && invite.Device != device))
            {
                return false;
            }

            invite.Device = device;
            invite.DeviceName = deviceName;
            invite.Token = GuestRoutes.TokenHash(token);
            invite.Used = _now();
            Save();
            return true;
        }
    }

    /// <summary>The link of a session token while it serves (its last use moved); null for any other token.</summary>
    public GuestInvite? ByToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var hash = GuestRoutes.TokenHash(token);
        lock (_gate)
        {
            var now = _now();
            var invite = _store.Invites.FirstOrDefault(x => x.Token is not null && Same(x.Token, hash));
            if (invite is null || now - invite.Used >= Unused)
            {
                return null;
            }

            if (now - invite.Used >= Touch)
            {
                invite.Used = now;
                Save();
            }

            return invite.Copy();
        }
    }

    private bool Try(string from, DateTimeOffset now)
    {
        var minute = now - TimeSpan.FromMinutes(1);
        while (_allTries.Count > 0 && _allTries.Peek() <= minute)
        {
            _allTries.Dequeue();
        }

        foreach (var key in _tries.Where(x => x.Value.All(t => t <= minute)).Select(x => x.Key).ToList())
        {
            _tries.Remove(key);
        }

        if (!_tries.TryGetValue(from, out var mine))
        {
            _tries[from] = mine = new Queue<DateTimeOffset>();
        }

        while (mine.Count > 0 && mine.Peek() <= minute)
        {
            mine.Dequeue();
        }

        if (mine.Count >= TriesPerAddress || _allTries.Count >= TriesInAll)
        {
            return false;
        }

        mine.Enqueue(now);
        _allTries.Enqueue(now);
        return true;
    }

    private static bool Same(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    private void Load()
    {
        if (_file is null || !File.Exists(_file))
        {
            return;
        }

        try
        {
            _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(_file), Json) ?? new Store();
            _store.Gone.RemoveAll(x => !GuestRoutes.IsId(x));
            _store.Invites.RemoveAll(x => string.IsNullOrEmpty(x.Id) || !GuestRoutes.IsId(x.UserId) || !GuestRoutes.IsCode(x.Code));
            _store.Parties.RemoveAll(x => !GuestRoutes.IsCode(x.Code) || string.IsNullOrEmpty(x.HostId));
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable: put aside, so that the accounts it named can still be found; the links start again empty.
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
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var next = _file + ".next";
            File.WriteAllText(next, JsonSerializer.Serialize(_store, Json));
            File.Move(next, _file, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Kept in memory until the next change: a restart of Jellyfin would forget it.
        }
    }

    private sealed class Store
    {
        public string? Address { get; set; }
        public string? Key { get; set; }
        public List<GuestInvite> Invites { get; set; } = [];
        /// <summary>Each host's party link (<see cref="PartyOf"/>).</summary>
        public List<GuestParty> Parties { get; set; } = [];
        /// <summary>Accounts of links taken back or gone unused, still on Jellyfin until deleted there.</summary>
        public List<string> Gone { get; set; } = [];
    }
}

/// <summary>One link (see <see cref="GuestInvites"/>).</summary>
public sealed class GuestInvite
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    /// <summary>The guest's name, their account's on Jellyfin: what the group shows.</summary>
    public string Name { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>The administrator who made it: their groups are the only ones the guest sees and joins.</summary>
    public string HostId { get; set; } = "";
    public string HostName { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    /// <summary>When it last served (or was made): its end is <see cref="GuestInvites.Unused"/> after.</summary>
    public DateTimeOffset Used { get; set; }
    /// <summary>The device it serves, once opened: Mira web's id for it.</summary>
    public string? Device { get; set; }
    public string? DeviceName { get; set; }
    /// <summary>The hash of the session Jellyfin gave that device (<see cref="GuestRoutes.TokenHash"/>).</summary>
    public string? Token { get; set; }
    /// <summary>The code of the party link that let this guest in; null for a link of its own.</summary>
    public string? Party { get; set; }

    public GuestInvite Copy() => (GuestInvite)MemberwiseClone();
}

/// <summary>A host's party link (see <see cref="GuestInvites.PartyOf"/>).</summary>
public sealed class GuestParty
{
    public string Code { get; set; } = "";
    /// <summary>The account that hosts: its groups are the ones the party's guests see and join.</summary>
    public string HostId { get; set; } = "";
    public string HostName { get; set; } = "";
    public DateTimeOffset Created { get; set; }

    public GuestParty Copy() => (GuestParty)MemberwiseClone();
}
