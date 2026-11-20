using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mira.Jellyfin;

/// <summary>
/// « Le foyer »: one password for the family, then each person picks their profile, a Jellyfin account the
/// administrator offered. Jellyfin has no such thing (accounts only, each with its own password, and no PIN since
/// 10.9): the extension keeps it. What it keeps, in one file beside the plugins' settings: the home's password (PBKDF2,
/// never the password), the accounts offered as profiles and their PIN (PBKDF2 too), and the devices let in, each by
/// the hash of its home token. Tries are counted here, in memory: the password per address and in all, a profile's PIN
/// per profile (<see cref="PinTries"/> then <see cref="PinWait"/>). Free of ASP.NET, on a clock of its own, for the
/// tests (<see cref="HomeController"/> serves it, the plugin's own in Plugin.Home).
/// </summary>
public sealed class HomeFamily
{
    /// <summary>The shortest home password: it is shared with a whole family and opens every profile without a PIN.</summary>
    public const int MinPassword = 8, MaxPassword = 128;
    /// <summary>Profiles and devices at once.</summary>
    public const int MaxProfiles = 20, MaxDevices = 50;
    /// <summary>Tries of the home password from one address, then from all, within <see cref="TryWindow"/>.</summary>
    public const int TriesPerAddress = 10, TriesInAll = 30;
    public static readonly TimeSpan TryWindow = TimeSpan.FromMinutes(10);
    /// <summary>Wrong PINs of a profile in a row, then the wait before the next try.</summary>
    public const int PinTries = 5;
    public static readonly TimeSpan PinWait = TimeSpan.FromMinutes(5);
    /// <summary>The wait for an administrator's profile: its PIN opens the server, so guessing it is made far slower.</summary>
    public static readonly TimeSpan AdminPinWait = TimeSpan.FromHours(1);
    /// <summary>The prefix of the Jellyfin device id of a profile opened by the home: one per device of the home.</summary>
    public const string DevicePrefix = "mira-home-";
    // OWASP's count for PBKDF2-HMAC-SHA256 (2023): about a third of a second on a small PC, once per try.
    private const int Iterations = 600_000;
    // A device seen again is written down at most this often.
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
    private readonly int _iterations;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _tries = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _allTries = new();
    private readonly Dictionary<string, (int Wrong, DateTimeOffset Until)> _pins = new(StringComparer.OrdinalIgnoreCase);
    private Store _store = new();

    public HomeFamily(string? file, Func<DateTimeOffset> now, int iterations = Iterations)
    {
        _file = file;
        _now = now;
        _iterations = iterations;
        Load();
    }

    public enum Joining { Joined, NoHome, Wrong, TooMany, Full }

    public enum Pin { Open, Asked, Wrong, Wait }

    /// <summary>Whether the home is on: a password was set.</summary>
    public bool On
    {
        get { lock (_gate) { return _store.Password is not null; } }
    }

    /// <summary>When the password was last set.</summary>
    public DateTimeOffset? Changed
    {
        get { lock (_gate) { return _store.Password?.Changed; } }
    }

    /// <summary>Whether a password may be the home's: long enough, not too long, not only spaces.</summary>
    public static bool GoodPassword(string? password) =>
        password is { Length: >= MinPassword and <= MaxPassword } && !string.IsNullOrWhiteSpace(password);

    /// <summary>Whether a text is a PIN: four digits.</summary>
    public static bool IsPin(string? pin) => pin is { Length: 4 } && pin.All(char.IsAsciiDigit);

    /// <summary>
    /// The home's password set (or changed): every device leaves the home, and their ids are returned so that their
    /// profiles' sessions end on Jellyfin too. Null turns the home off (devices out, profiles and PINs kept).
    /// </summary>
    public IReadOnlyList<HomeDevice>? SetPassword(string? password)
    {
        if (password is not null && !GoodPassword(password))
        {
            return null;
        }

        var secret = password is null ? null : Secret.Make(password, _iterations, _now());
        lock (_gate)
        {
            var gone = _store.Devices.Select(x => x.Copy()).ToList();
            _store.Password = secret;
            _store.Devices.Clear();
            Save();
            return gone;
        }
    }

    /// <summary>
    /// A device joining with the home's password: a new home token (returned once, kept only by its hash). The same
    /// device joining again (<paramref name="device"/>, its app's own id) takes the place of its earlier entry, whose
    /// profile's session is returned to end.
    /// </summary>
    public (Joining Result, string? Token, HomeDevice? Device, HomeDevice? Replaced) Join(string? password, string device, string name, string from)
    {
        Secret? secret;
        lock (_gate)
        {
            if (_store.Password is null)
            {
                return (Joining.NoHome, null, null, null);
            }

            if (!Try(from, _now()))
            {
                return (Joining.TooMany, null, null, null);
            }

            secret = _store.Password;
        }

        // Outside the lock: a third of a second.
        if (password is null || password.Length > MaxPassword || !secret.Matches(password))
        {
            return (Joining.Wrong, null, null, null);
        }

        lock (_gate)
        {
            if (!ReferenceEquals(secret, _store.Password))
            {
                return (Joining.Wrong, null, null, null);
            }

            var replaced = _store.Devices.FirstOrDefault(x => x.Device == device);
            if (replaced is null && _store.Devices.Count >= MaxDevices)
            {
                return (Joining.Full, null, null, null);
            }

            if (replaced is not null)
            {
                _store.Devices.Remove(replaced);
            }

            var token = GuestRoutes.NewPassword().Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var now = _now();
            var entry = new HomeDevice
            {
                Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                Token = GuestRoutes.TokenHash(token),
                Device = device,
                Name = name,
                Joined = now,
                Seen = now,
                // Joined through the tailnet (a home link's key): the node to delete from it when the device leaves.
                Tailnet = HomeTailnet.TailnetAddress(from),
            };
            _store.Devices.Add(entry);
            Save();
            return (Joining.Joined, token, entry.Copy(), replaced?.Copy());
        }
    }

    /// <summary>The device a home token belongs to, seen now; null for a token of no device of the home.</summary>
    public HomeDevice? ByToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128)
        {
            return null;
        }

        var hash = GuestRoutes.TokenHash(token);
        lock (_gate)
        {
            if (_store.Password is null)
            {
                return null;
            }

            var device = _store.Devices.FirstOrDefault(x => Same(x.Token, hash));
            if (device is null)
            {
                return null;
            }

            var now = _now();
            if (now - device.Seen >= Touch)
            {
                device.Seen = now;
                Save();
            }

            return device.Copy();
        }
    }

    /// <summary>The devices of the home, latest first.</summary>
    public IReadOnlyList<HomeDevice> Devices()
    {
        lock (_gate) { return _store.Devices.OrderByDescending(x => x.Seen).Select(x => x.Copy()).ToList(); }
    }

    /// <summary>A device out of the home: its entry, to end its profile's session; null when it was not there.</summary>
    public HomeDevice? Remove(string id)
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

    /// <summary>The profile a device now has open (null: none), written down once Jellyfin opened its session.</summary>
    public bool Opened(string id, string? profile)
    {
        lock (_gate)
        {
            var device = _store.Devices.FirstOrDefault(x => x.Id == id);
            if (device is null)
            {
                return false;
            }

            device.Profile = profile;
            device.Seen = _now();
            Save();
            return true;
        }
    }

    /// <summary>The accounts offered as profiles, by their id (32 hexadecimal digits, lower case).</summary>
    public IReadOnlyList<HomeProfile> Profiles()
    {
        lock (_gate) { return _store.Profiles.Select(x => x.Copy()).ToList(); }
    }

    public HomeProfile? Profile(string? user)
    {
        if (Plain(user) is not { } id)
        {
            return null;
        }

        lock (_gate) { return _store.Profiles.FirstOrDefault(x => x.User == id)?.Copy(); }
    }

    /// <summary>
    /// An account offered as a profile, or no longer: taken out, the devices that had it open are returned (their
    /// session ends) and they keep only the home. Never more than <see cref="MaxProfiles"/>.
    /// </summary>
    public (bool Done, IReadOnlyList<HomeDevice> Closed) Offer(string user, bool offered)
    {
        if (Plain(user) is not { } id)
        {
            return (false, []);
        }

        lock (_gate)
        {
            var profile = _store.Profiles.FirstOrDefault(x => x.User == id);
            if (offered)
            {
                if (profile is null)
                {
                    if (_store.Profiles.Count >= MaxProfiles)
                    {
                        return (false, []);
                    }

                    _store.Profiles.Add(new HomeProfile { User = id });
                    Save();
                }

                return (true, []);
            }

            if (profile is not null)
            {
                _store.Profiles.Remove(profile);
                _pins.Remove(id);
            }

            var closed = _store.Devices.Where(x => x.Profile == id).ToList();
            var copies = closed.Select(x => x.Copy()).ToList();
            foreach (var device in closed)
            {
                device.Profile = null;
            }

            Save();
            return (true, copies);
        }
    }

    /// <summary>
    /// « Can download »: an account that may use TorLink without administering Jellyfin (an administrator always
    /// may). Kept with the home, apart from its profiles: offered or not, the home on or off.
    /// </summary>
    public bool CanDownload(string? user)
    {
        if (Plain(user) is not { } id)
        {
            return false;
        }

        lock (_gate) { return _store.Downloaders.Contains(id); }
    }

    /// <summary>The accounts that may download, by id.</summary>
    public IReadOnlyList<string> Downloaders()
    {
        lock (_gate) { return _store.Downloaders.ToList(); }
    }

    /// <summary>An account let download, or no longer; false for an id that is not one.</summary>
    public bool SetDownload(string user, bool allowed)
    {
        if (Plain(user) is not { } id)
        {
            return false;
        }

        lock (_gate)
        {
            var had = _store.Downloaders.Contains(id);
            if (had == allowed)
            {
                return true;
            }

            if (allowed)
            {
                _store.Downloaders.Add(id);
            }
            else
            {
                _store.Downloaders.Remove(id);
            }

            Save();
            return true;
        }
    }

    /// <summary>Whether an offered profile has a PIN.</summary>
    public bool HasPin(string user) => Plain(user) is { } id && Profile(id)?.Pin is not null;

    /// <summary>A profile's PIN set (four digits) or taken off (null); false for a profile not offered or no PIN.</summary>
    public bool SetPin(string user, string? pin)
    {
        if (Plain(user) is not { } id || (pin is not null && !IsPin(pin)))
        {
            return false;
        }

        var secret = pin is null ? null : Secret.Make(pin, _iterations, _now());
        lock (_gate)
        {
            var profile = _store.Profiles.FirstOrDefault(x => x.User == id);
            if (profile is null)
            {
                return false;
            }

            profile.Pin = secret;
            _pins.Remove(id);
            Save();
            return true;
        }
    }

    /// <summary>
    /// Whether a profile may be opened with <paramref name="pin"/>: open without a PIN, asked when it has one and none
    /// came, wrong (with the tries left), or to wait after <see cref="PinTries"/> wrong ones in a row
    /// (<see cref="AdminPinWait"/> for an administrator's profile).
    /// </summary>
    public (Pin Result, int Left, TimeSpan Wait) CheckPin(string user, string? pin, bool admin = false)
    {
        var lockout = admin ? AdminPinWait : PinWait;
        if (Plain(user) is not { } id)
        {
            return (Pin.Wrong, 0, TimeSpan.Zero);
        }

        Secret? secret;
        lock (_gate)
        {
            secret = _store.Profiles.FirstOrDefault(x => x.User == id)?.Pin;
            if (secret is null)
            {
                return (Pin.Open, PinTries, TimeSpan.Zero);
            }

            var now = _now();
            if (_pins.TryGetValue(id, out var state) && state.Until > now)
            {
                return (Pin.Wait, 0, state.Until - now);
            }

            if (pin is null)
            {
                return (Pin.Asked, PinTries - (_pins.TryGetValue(id, out var asked) ? asked.Wrong : 0), TimeSpan.Zero);
            }
        }

        var right = IsPin(pin) && secret.Matches(pin);
        lock (_gate)
        {
            if (right)
            {
                _pins.Remove(id);
                return (Pin.Open, PinTries, TimeSpan.Zero);
            }

            var now = _now();
            var wrong = (_pins.TryGetValue(id, out var state) && state.Until <= now ? state.Wrong : 0) + 1;
            if (wrong >= PinTries)
            {
                _pins[id] = (0, now + lockout);
                return (Pin.Wait, 0, lockout);
            }

            _pins[id] = (wrong, DateTimeOffset.MinValue);
            return (Pin.Wrong, PinTries - wrong, TimeSpan.Zero);
        }
    }

    /// <summary>The Jellyfin device id of the profile a device of the home opens: always the same for that device.</summary>
    public static string JellyfinDevice(HomeDevice device) => DevicePrefix + device.Id;

    /// <summary>An account's id as Jellyfin writes it in its JSON (32 hexadecimal digits, lower case), from either form.</summary>
    public static string? Plain(string? id)
    {
        var plain = id?.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        return plain is { Length: 32 } && plain.All(char.IsAsciiHexDigit) ? plain : null;
    }

    private bool Try(string from, DateTimeOffset now)
    {
        var since = now - TryWindow;
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
            if (_store.Password is { } password && !password.Valid)
            {
                _store.Password = null;
            }

            _store.Profiles.RemoveAll(x => Plain(x.User) is null);
            foreach (var profile in _store.Profiles.Where(x => x.Pin is { Valid: false }))
            {
                profile.Pin = null;
            }

            _store.Devices.RemoveAll(x => string.IsNullOrEmpty(x.Id) || string.IsNullOrEmpty(x.Token));
            _store.Downloaders = _store.Downloaders.Select(Plain).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable: put aside, and the home is off until its administrator sets a password again. Never a home
            // left open by a damaged file.
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
            // Kept in memory until the next change: a restart of Jellyfin would forget it.
        }
    }

    internal sealed class Store
    {
        public Secret? Password { get; set; }
        public List<HomeProfile> Profiles { get; set; } = [];
        public List<HomeDevice> Devices { get; set; } = [];
        public List<string> Downloaders { get; set; } = [];
    }
}

/// <summary>A password or a PIN as kept: PBKDF2-HMAC-SHA256 of it, with its salt and count.</summary>
public sealed class Secret
{
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
    public int Iterations { get; set; }
    public DateTimeOffset Changed { get; set; }

    [JsonIgnore]
    public bool Valid => Iterations >= 1 && Bytes(Salt) is { Length: 16 } && Bytes(Hash) is { Length: 32 };

    public static Secret Make(string text, int iterations, DateTimeOffset now)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new Secret
        {
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(Derive(text, salt, iterations)),
            Iterations = iterations,
            Changed = now,
        };
    }

    public bool Matches(string text) =>
        Valid && CryptographicOperations.FixedTimeEquals(Derive(text, Bytes(Salt)!, Iterations), Bytes(Hash)!);

    private static byte[] Derive(string text, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(text), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static byte[]? Bytes(string text)
    {
        try { return Convert.FromBase64String(text); }
        catch (FormatException) { return null; }
    }
}

/// <summary>An account offered as a profile of the home (see <see cref="HomeFamily"/>).</summary>
public sealed class HomeProfile
{
    /// <summary>The Jellyfin account's id, 32 hexadecimal digits in lower case.</summary>
    public string User { get; set; } = "";
    public Secret? Pin { get; set; }

    public HomeProfile Copy() => (HomeProfile)MemberwiseClone();
}

/// <summary>A device let into the home (see <see cref="HomeFamily"/>).</summary>
public sealed class HomeDevice
{
    /// <summary>The home's own id for it: its profiles' Jellyfin device id is made of it (<see cref="HomeFamily.JellyfinDevice"/>).</summary>
    public string Id { get; set; } = "";
    /// <summary>The hash of its home token (<see cref="GuestRoutes.TokenHash"/>).</summary>
    public string Token { get; set; } = "";
    /// <summary>The app's own id for the device, so that joining again takes its earlier place.</summary>
    public string Device { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset Joined { get; set; }
    public DateTimeOffset Seen { get; set; }
    /// <summary>The account it has open, if any.</summary>
    public string? Profile { get; set; }
    /// <summary>The device's Tailscale address when it joined through the tailnet (a home link), else null.</summary>
    public string? Tailnet { get; set; }

    public HomeDevice Copy() => (HomeDevice)MemberwiseClone();
}
