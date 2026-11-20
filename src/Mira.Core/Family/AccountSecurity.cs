using System.Text.Json;

namespace Mira.Core.Family;

/// <summary>What an account of the server is to Mira's family, besides its password.</summary>
public enum AccountWay
{
    /// <summary>It signs in with its password only.</summary>
    Password,
    /// <summary>A profile of the home: it gets in with the home's password, then picks itself.</summary>
    Home,
    /// <summary>A friend invited by a link: their devices are let in by the link.</summary>
    Friend,
}

/// <summary>An account of the server as « Security » shows it.</summary>
public sealed record SecurityAccount(string Id, string Name, string? ImageTag, bool Admin, bool Disabled, bool NoPassword, bool You, AccountWay Way, int? Length)
{
    /// <summary>Its own password too short for an administrator: known for the account signed in here only (<see cref="Connection.PasswordLength"/>).</summary>
    public bool Short => You && !NoPassword && Admin && Length is { } length && length < AccountSecurity.MinLength;
    /// <summary>Something to fix: no password, or its own short one.</summary>
    public bool Weak => !Disabled && (NoPassword || Short);
}

/// <summary>Where a device signed in comes from.</summary>
public enum DeviceWay
{
    Other,
    /// <summary>A device of the home (« mira-home-… »): signed out, it leaves the home.</summary>
    Home,
    /// <summary>A friend's device let in by a link (« mira-friend-… »): signed out, its line to this server is cut.</summary>
    Friend,
}

/// <summary>A device Jellyfin knows (its Devices), as « Security » shows it.</summary>
public sealed record SecurityDevice(string Id, string Name, string App, string? Version, string? UserId, string? UserName, DateTimeOffset? LastActive, bool This, bool Active, bool Watching, DeviceWay Way)
{
    /// <summary>The device's own id in the home or among a friend's devices (the extension's), without Jellyfin's prefix.</summary>
    public string WayId => Way switch
    {
        DeviceWay.Home => Id[AccountSecurity.HomePrefix.Length..],
        DeviceWay.Friend => Id[AccountSecurity.FriendPrefix.Length..],
        _ => Id,
    };
    /// <summary>Not used for three months or more: maybe a device given away, sold or lost.</summary>
    public bool Stale(DateTimeOffset now) => !This && !Active && LastActive is { } last && now - last >= AccountSecurity.StaleAfter;
}

/// <summary>What « Security » read: the accounts, the devices; <see cref="Known"/> false when the extension could not say which accounts have no password.</summary>
public sealed record SecurityReport(IReadOnlyList<SecurityAccount> Accounts, IReadOnlyList<SecurityDevice> Devices, bool Known, DateTimeOffset Checked);

/// <summary>Something to fix, one step of « Fix all… »: an account (no password, or your short one) or a device not used for months.</summary>
public sealed record SecurityIssue(string Key, SecurityAccount? Account, SecurityDevice? Device);

/// <summary>
/// « Security » (ogsas, 2026-11-20, maquette A « La page Security » with the steps of C behind « Fix all… »): which
/// accounts of the server open without a password, the administrator's own password when it is short, and every device
/// signed in, one « Sign out » each. Read from Jellyfin (Users, Devices, Sessions) and from Mira's extension (which
/// accounts have no password: Jellyfin's « HasPassword » says yes for an account made without one, checked on 12.1).
/// </summary>
public static class AccountSecurity
{
    /// <summary>Under this, an administrator's password is said short.</summary>
    public const int MinLength = 10;
    public const string HomePrefix = "mira-home-", FriendPrefix = "mira-friend-";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(90);
    /// <summary>« Skip » or « Yes, keep it »: not asked again, nor counted on « Essentials », for 30 days.</summary>
    public static readonly TimeSpan QuietFor = TimeSpan.FromDays(30);

    public static string AccountKey(string id) => "account:" + id;
    public static string DeviceKey(string id) => "device:" + id;

    /// <summary>A long password nobody has to remember (« Let Mira lock it »): 32 signs, about 190 bits.</summary>
    public static string LockPassword() =>
        System.Security.Cryptography.RandomNumberGenerator.GetString("abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789", 32);

    /// <summary>
    /// The report, from Jellyfin's answers: its accounts (GET Users), its devices (GET Devices), its sessions (GET Sessions),
    /// the extension's accounts without a password (null when it has none to say), the home's profiles and friends' accounts.
    /// Accounts of a party's guests are left out (<paramref name="guests"/>).
    /// </summary>
    public static SecurityReport Read(JsonElement users, JsonElement devices, JsonElement sessions, IReadOnlyCollection<string>? open, Connection you,
        IReadOnlyCollection<string> home, IReadOnlyCollection<string> friends, IReadOnlyCollection<string> guests, DateTimeOffset now)
    {
        var accounts = new List<SecurityAccount>();
        if (users.ValueKind == JsonValueKind.Array)
            foreach (var user in users.EnumerateArray())
            {
                if (Text(user, "Id") is not { } raw) continue;
                var id = Plain(raw);
                if (guests.Contains(id)) continue;
                var policy = user.TryGetProperty("Policy", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
                var self = Plain(you.UserId) == id;
                var way = home.Contains(id) ? AccountWay.Home : friends.Contains(id) ? AccountWay.Friend : AccountWay.Password;
                // Without the extension's word: Jellyfin's own « HasPassword » (false only for an account it reset).
                var none = open is not null ? open.Contains(id) : user.TryGetProperty("HasPassword", out var has) && has.ValueKind == JsonValueKind.False;
                accounts.Add(new SecurityAccount(id, Text(user, "Name") ?? "", Text(user, "PrimaryImageTag"), Flag(policy, "IsAdministrator"), Flag(policy, "IsDisabled"),
                    none, self, way, self ? you.PasswordLength : null));
            }

        var active = new HashSet<string>(StringComparer.Ordinal); var watching = new HashSet<string>(StringComparer.Ordinal);
        if (sessions.ValueKind == JsonValueKind.Array)
            foreach (var session in sessions.EnumerateArray())
            {
                if (Text(session, "DeviceId") is not { } device) continue;
                if (session.TryGetProperty("LastActivityDate", out var at) && at.TryGetDateTimeOffset(out var last) && now - last < TimeSpan.FromMinutes(10)) active.Add(device);
                if (session.TryGetProperty("NowPlayingItem", out var item) && item.ValueKind == JsonValueKind.Object) { watching.Add(device); active.Add(device); }
            }

        var list = new List<SecurityDevice>();
        var items = devices.ValueKind == JsonValueKind.Object && devices.TryGetProperty("Items", out var found) ? found : devices;
        if (items.ValueKind == JsonValueKind.Array)
            foreach (var device in items.EnumerateArray())
            {
                if (Text(device, "Id") is not { } id) continue;
                DateTimeOffset? last = device.TryGetProperty("DateLastActivity", out var at) && at.TryGetDateTimeOffset(out var when) ? when : null;
                var way = id.StartsWith(HomePrefix, StringComparison.Ordinal) && id.Length > HomePrefix.Length ? DeviceWay.Home
                    : id.StartsWith(FriendPrefix, StringComparison.Ordinal) && id.Length > FriendPrefix.Length ? DeviceWay.Friend : DeviceWay.Other;
                var user = Text(device, "LastUserId") is { } u ? Plain(u) : null;
                if (user is not null && guests.Contains(user)) continue;
                var self = id == you.DeviceId;
                list.Add(new SecurityDevice(id, Text(device, "Name") ?? id, Text(device, "AppName") ?? "", Text(device, "AppVersion"), user, Text(device, "LastUserName"), last,
                    self, self || active.Contains(id), watching.Contains(id), way));
            }

        // The most urgent first: no password, then your short one; then the safe ones, you first, then by name.
        accounts = [.. accounts.OrderBy(x => x.Disabled).ThenBy(x => x.NoPassword ? 0 : x.Short ? 1 : 2).ThenByDescending(x => x.You).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)];
        // This device first, then the ones in use, then the most recently seen.
        list = [.. list.OrderByDescending(x => x.This).ThenByDescending(x => x.Watching).ThenByDescending(x => x.Active).ThenByDescending(x => x.LastActive ?? DateTimeOffset.MinValue)];
        return new SecurityReport(accounts, list, open is not null, now);
    }

    /// <summary>
    /// What to fix, in the order « Fix all… » asks: accounts without a password, your short one, devices not used for
    /// months. <paramref name="quiet"/>: those skipped or kept within 30 days are left out (« Essentials », the list's dot).
    /// </summary>
    public static IReadOnlyList<SecurityIssue> Issues(SecurityReport report, Func<string, bool>? quiet = null)
    {
        var issues = new List<SecurityIssue>();
        foreach (var account in report.Accounts.Where(x => x.Weak).OrderBy(x => x.Short))
            if (quiet?.Invoke(AccountKey(account.Id)) != true) issues.Add(new(AccountKey(account.Id), account, null));
        foreach (var device in report.Devices.Where(x => x.Stale(report.Checked)))
            if (quiet?.Invoke(DeviceKey(device.Id)) != true) issues.Add(new(DeviceKey(device.Id), null, device));
        return issues;
    }

    /// <summary>Whether a skipped or kept item is still quiet; old marks dropped from <paramref name="marks"/>.</summary>
    public static bool Quiet(IDictionary<string, DateTimeOffset> marks, string key, DateTimeOffset now) =>
        marks.TryGetValue(key, out var at) && now - at < QuietFor && at <= now + TimeSpan.FromDays(1);

    public static void Forget(IDictionary<string, DateTimeOffset> marks, DateTimeOffset now)
    {
        foreach (var key in marks.Where(x => now - x.Value >= QuietFor).Select(x => x.Key).ToList()) marks.Remove(key);
    }

    /// <summary>An account's id as Jellyfin writes it in its JSON (32 hexadecimal digits, lower case), from either form.</summary>
    public static string Plain(string id) => id.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
    private static bool Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
