using System.Globalization;
using System.Text.Json;

namespace Mira.Core;

/// <summary>
/// « Regarder ensemble » (Jellyfin's SyncPlay), what needs no window: the server's clock, where and when a command of
/// the group puts the player, the group's words. The same as Mira web's sync.js: tests/fixtures/syncplay.json.
/// </summary>
public static class Together
{
    /// <summary>A group as Jellyfin describes it; Participants names each account once, whatever its devices.</summary>
    public sealed record Group(string GroupId, string GroupName, List<string> Participants, string State);
    /// <summary>An answer of GetUtcTime: sent and received here, received and sent there, in milliseconds since 1970.</summary>
    public sealed record ClockSample(long Sent, long Received, long ServerIn, long ServerOut);
    /// <summary>What a command asks: Action play (in Wait ms), pause, seek or stop, at Position seconds.</summary>
    public sealed record Plan(string Action, long Wait, double Position);
    /// <summary>The group's title and where it starts.</summary>
    public sealed record Entry(string ItemId, string PlaylistItemId, long StartTicks);

    /// <summary>Jellyfin's date (seven decimals) in milliseconds since 1970; null when it is not one.</summary>
    public static long? ServerTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUnixTimeMilliseconds() : null;
    /// <summary>Jellyfin's clock minus this PC's, from the answer with the shortest round trip; null without one.</summary>
    public static (long Offset, long Trip)? ClockOffset(IEnumerable<ClockSample> samples)
    {
        (long Offset, long Trip)? best = null;
        foreach (var s in samples)
        {
            var trip = (s.Received - s.Sent) - (s.ServerOut - s.ServerIn);
            if (trip < 0) continue;
            if (best is null || trip < best.Value.Trip)
                best = ((long)Math.Round(((s.ServerIn - s.Sent) + (s.ServerOut - s.Received)) / 2.0, MidpointRounding.AwayFromZero), trip);
        }
        return best;
    }
    /// <summary>
    /// What a SyncPlayCommand asks of this player, on its clock (<paramref name="now"/> in ms since 1970): a play that
    /// arrives late starts further on, where the others are. Null for a command Mira does not know.
    /// </summary>
    public static Plan? CommandPlan(JsonElement data, long offset, long now)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        var position = (data.TryGetProperty("PositionTicks", out var p) && p.TryGetInt64(out var ticks) ? ticks : 0) / 1e7;
        switch (Text(data, "Command"))
        {
            case "Unpause":
                if (ServerTime(Text(data, "When")) is not { } when) return new("play", 0, position);
                var wait = when - offset - now;
                return new("play", Math.Max(0, wait), wait < 0 ? position - wait / 1000.0 : position);
            case "Pause": return new("pause", 0, position);
            case "Seek": return new("seek", 0, position);
            case "Stop": return new("stop", 0, position);
            default: return null;
        }
    }
    /// <summary>Under half a second apart, a jump would be seen more than the gap.</summary>
    public static bool NeedsSeek(double current, double target) => Math.Abs(current - target) >= 0.5;
    /// <summary>The title a PlayQueue update puts on; null when it plays nothing.</summary>
    public static Entry? QueueItem(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("Playlist", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var index = data.TryGetProperty("PlayingItemIndex", out var i) && i.TryGetInt32(out var n) ? n : -1;
        if (index < 0 || index >= list.GetArrayLength()) return null;
        var entry = list[index];
        if (Text(entry, "ItemId") is not { } item) return null;
        return new(item, Text(entry, "PlaylistItemId") ?? "", data.TryGetProperty("StartPositionTicks", out var s) && s.TryGetInt64(out var start) ? start : 0);
    }
    public static Group? ParseGroup(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && Text(data, "GroupId") is { } id
            ? new(id, Text(data, "GroupName") ?? "Groupe", data.TryGetProperty("Participants", out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList() : [], Text(data, "State") ?? "")
            : null;
    /// <summary>« Groupe de mira ».</summary>
    public static string GroupName(string user) => "Groupe de " + user;
    /// <summary>Who made a group of Mira, from its name (« Groupe de mira » → mira); null for a name Mira does not give.</summary>
    public static string? GroupHost(string? name) =>
        name is not null && name.StartsWith("Groupe de ", StringComparison.Ordinal) && name.Length > "Groupe de ".Length ? name["Groupe de ".Length..] : null;
    /// <summary>
    /// The group an invitation means among Jellyfin's: the one of its id, else the one its host made again since under
    /// the same name (Jellyfin ends an empty group with its last device; Mira makes it again, <see cref="RejoinPlan"/>),
    /// with the host in it first, then the fullest. Null when none is open.
    /// </summary>
    public static Group? FindGroup(IReadOnlyList<Group> groups, string groupId, string? host)
    {
        if (groups.FirstOrDefault(g => SameGroup(g.GroupId, groupId)) is { } same) return same;
        if (string.IsNullOrEmpty(host)) return null;
        var name = GroupName(host);
        return groups.Where(g => g.GroupName == name).OrderByDescending(g => g.Participants.Contains(host)).ThenByDescending(g => g.Participants.Count).FirstOrDefault();
    }
    /// <summary>How to go back to a group: join the one of <see cref="Join"/>, or make it again under the name <see cref="Create"/>.</summary>
    public sealed record Rejoin(string? Join, string? Create);
    /// <summary>
    /// Back in a group Jellyfin let this device go from (its session ended with its WebSocket: Wi-Fi lost, the PC asleep):
    /// joined where it still is; made again under the same name when this device made it and it ended (empty), so that
    /// the invitations sent still find it (<see cref="FindGroup"/>); null when there is nothing to go back to.
    /// </summary>
    public static Rejoin? RejoinPlan(IReadOnlyList<Group> groups, Group lost, bool made)
    {
        if (FindGroup(groups, lost.GroupId, GroupHost(lost.GroupName)) is { } found) return new(found.GroupId, null);
        return made && lost.GroupName.Length > 0 ? new(null, lost.GroupName) : null;
    }
    /// <summary>What a friend reads when the host has no group open yet: « mira n’a pas encore lancé la soirée ».</summary>
    public static string WaitingText(string host) => host + " n’a pas encore lancé la soirée";
    /// <summary>What it says under it, while Mira waits for the group.</summary>
    public const string WaitingLead = "Garde Mira ouvert : tu entres dans le groupe dès qu’il est ouvert.";
    /// <summary>« 1 personne », « 2 personnes ».</summary>
    public static string People(int count) => count <= 1 ? "1 personne" : $"{count} personnes";
    /// <summary>« Groupe de mira · 2 personnes ».</summary>
    public static string GroupText(Group group) => $"{group.GroupName} · {People(Math.Max(1, group.Participants.Count))}";
    /// <summary>
    /// The group Home offers to join (« Groupe de Marc · 2 personnes », Rejoindre): the first Jellyfin has open, none
    /// while this device is in one (<paramref name="current"/>). As Mira web's Home (views/home.js).
    /// </summary>
    public static Group? HomeGroup(IReadOnlyList<Group> groups, string? current) => current is null && groups.Count > 0 ? groups[0] : null;
    /// <summary>Who is in a group, as a line says it: « mira et Marc », « mira, Marc et Léa », « mira, Marc et 3 autres ».</summary>
    public static string NamesText(IReadOnlyList<string> names) => names.Count switch
    {
        <= 1 => string.Concat(names),
        > 3 => $"{string.Join(", ", names.Take(2))} et {names.Count - 2} autres",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} et {names[^1]}",
    };
    /// <summary>What the group is doing, in a line, and whether it holds the picture (Alert).</summary>
    public sealed record Status(string Text, bool Alert);
    /// <summary>
    /// What the group is doing, for the player's pill and the title page. Jellyfin says only its state (Idle, Waiting,
    /// Paused, Playing), never who caused it: a wait while this device is ready (not <paramref name="loading"/>) is for
    /// the others, named when there is one. <paramref name="me"/>: this account's name, as Participants names each
    /// account once. The same as Mira web's groupStatus.
    /// </summary>
    public static Status GroupStatus(Group group, string? me, bool loading = false)
    {
        var others = group.Participants.Where(x => !string.Equals(x, me, StringComparison.OrdinalIgnoreCase)).ToList();
        return group.State switch
        {
            "Waiting" when loading || others.Count == 0 => new("Chargement pour le groupe…", true),
            "Waiting" => new(others.Count == 1 ? $"On attend {others[0]}…" : "On attend quelqu’un du groupe…", true),
            "Paused" => new("En pause pour le groupe", true),
            _ => new(others.Count > 0 ? $"Ensemble · {NamesText(group.Participants)}" : "Ensemble · personne d’autre pour l’instant", false),
        };
    }
    /// <summary>« Marc a rejoint le groupe », « Marc a quitté le groupe ».</summary>
    public static string MemberText(string name, bool joined) => $"{name} {(joined ? "a rejoint" : "a quitté")} le groupe";
    /// <summary>
    /// Lecture of <paramref name="itemId"/> while the group has another title on, not over (Idle): asked first, rather
    /// than changed for everyone unannounced. The same as Mira web's asksToReplace.
    /// </summary>
    public static bool AsksToReplace(Group? group, Entry? queue, string itemId) =>
        group is not null && queue is { ItemId.Length: > 0 } && queue.ItemId != itemId && group.State != "Idle";
    /// <summary>A title as a question names it: a film by its name, an episode with its series (« Dark · S1 · É3 »).</summary>
    public static string TitleName(MediaItem item)
    {
        if (item.Type != "Episode" || string.IsNullOrEmpty(item.SeriesName)) return item.Name;
        var code = string.Join(" · ", new[] { item.ParentIndexNumber is { } s ? $"S{s}" : "", item.IndexNumber is { } e ? $"É{e}" : "" }.Where(x => x.Length > 0));
        return code.Length > 0 ? $"{item.SeriesName} · {code}" : item.SeriesName;
    }
    /// <summary>« Remplacer « Dune » pour tout le groupe ? », or « Remplacer le titre du groupe ? » when its name is not known.</summary>
    public static string ReplaceQuestion(string? current) => string.IsNullOrEmpty(current) ? "Remplacer le titre du groupe ?" : $"Remplacer « {current} » pour tout le groupe ?";
    /// <summary>« à 42 min », « à 1 h 05 », « à 30 s »: where the group starts a title Lecture resumes.</summary>
    public static string AtText(double seconds)
    {
        var s = (long)Math.Floor(Math.Max(0, seconds)); var m = s / 60;
        return m < 1 ? $"à {s} s" : m < 60 ? $"à {m} min" : $"à {m / 60} h {m % 60:00}";
    }
    /// <summary>
    /// Lecture's label in a group, which starts the title for everyone: « Lancer pour le groupe », « Reprendre pour le
    /// groupe à 42 min » (from this account's progress), « Lancer S01 E03 pour le groupe » (<paramref name="code"/>: the
    /// episode, as the page writes it), « Regarder avec le groupe » on the group's own title. Mira web's groupPlayLabel.
    /// </summary>
    public static string GroupPlayLabel(string code, double resumeAt, bool groupTitle)
    {
        if (groupTitle) return "Regarder avec le groupe";
        return string.Join(" ", new[] { resumeAt > 0 ? "Reprendre" : "Lancer", code, "pour le groupe", resumeAt > 0 && code.Length == 0 ? AtText(resumeAt) : "" }.Where(x => x.Length > 0));
    }
    /// <summary>
    /// The address of Jellyfin to put in an invitation: a Jellyfin on this computer at its Tailscale address when
    /// Tailscale is connected here (a friend reaches it at home and away), else at its home network's address; another
    /// server keeps the one this app reaches it at.
    /// </summary>
    public static string InviteServer(string server, bool onThisComputer, string? tailnet, string? home)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri)) return server.TrimEnd('/');
        var host = onThisComputer ? tailnet ?? (uri.IsLoopback ? home : null) : null;
        return (host is null ? uri : new UriBuilder(uri) { Host = host }.Uri).GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
    }
    /// <summary>
    /// The link that opens Mira web on the group: the friend signs in with an account of this Jellyfin, then is in it. Its
    /// host (who made the group) keeps it valid when the group is made again, or not open yet (<see cref="FindGroup"/>).
    /// </summary>
    public static string InviteLink(string server, string groupId, string? host = null) =>
        server.TrimEnd('/') + "/Mira/#/ensemble/" + Uri.EscapeDataString(groupId) + (string.IsNullOrEmpty(host) ? "" : "?hote=" + Uri.EscapeDataString(host));
    /// <summary>
    /// The same invitation for Mira on a computer (« Ouvrir dans Mira » of Mira web): mira://ensemble/&lt;group&gt;?server=…,
    /// opened by Mira for Windows or Mac, which offers to join the group (<see cref="Answer"/>).
    /// </summary>
    public static string AppLink(string server, string groupId, string? host = null) =>
        "mira://ensemble/" + Uri.EscapeDataString(groupId) + "?server=" + Uri.EscapeDataString(server.TrimEnd('/')) + (string.IsNullOrEmpty(host) ? "" : "&hote=" + Uri.EscapeDataString(host));
    /// <summary>A group of a Jellyfin, at the address the invitation gives for it, and who made it when the link says.</summary>
    public sealed record Invitation(string GroupId, string Server, string? Host = null);
    /// <summary>
    /// An <see cref="AppLink"/>, from a web page: a group id (a GUID) and a Jellyfin at an http(s) address, nothing else
    /// (no other scheme, no user name in the address), and maybe its host (a name, without control characters). Null for
    /// anything else.
    /// </summary>
    public static Invitation? ParseAppLink(string? text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "mira" || !uri.Host.Equals("ensemble", StringComparison.OrdinalIgnoreCase)) return null;
        var group = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        if (!Guid.TryParse(group, out _) || group.Length is not (32 or 36)) return null;
        string? server = null, host = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith("server=", StringComparison.Ordinal)) server = Uri.UnescapeDataString(pair["server=".Length..]);
            else if (pair.StartsWith("hote=", StringComparison.Ordinal)) host = Uri.UnescapeDataString(pair["hote=".Length..]);
        }
        if (!Uri.TryCreate(server, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https") || address.UserInfo.Length > 0 || address.Query.Length > 0 || address.Fragment.Length > 0) return null;
        if (host is not null && (host.Length is 0 or > 128 || host.Any(char.IsControl))) return null;
        return new(group, address.GetLeftPart(UriPartial.Authority) + address.AbsolutePath.TrimEnd('/'), host);
    }
    /// <summary>Jellyfin writes a group's id with or without dashes.</summary>
    public static bool SameGroup(string a, string b) => string.Equals(a.Replace("-", ""), b.Replace("-", ""), StringComparison.OrdinalIgnoreCase);
    public enum InviteKind { Join, Already, OtherServer, Gone, Waiting }
    /// <summary>What Mira says of an invitation opened in it: the group to join (« Rejoindre »), or why not.</summary>
    public sealed record InviteAnswer(InviteKind Kind, string Text, Group? Group = null);
    public const string SignInToJoin = "Connecte-toi avec ton compte Jellyfin : Mira te proposera ensuite de rejoindre le groupe.";
    /// <summary>
    /// An invitation, against the groups of the Jellyfin this app is signed in to (<paramref name="server"/>): a group id
    /// is unique, so finding it there is enough, whatever address each one reaches that Jellyfin at; its host's group
    /// made again since will do too. With a host and no group yet, Mira can wait for it (<see cref="WaitForGroupAsync"/>).
    /// </summary>
    public static InviteAnswer Answer(Invitation invite, IReadOnlyList<Group> groups, string server, string? currentGroup)
    {
        if (FindGroup(groups, invite.GroupId, invite.Host) is { } group)
        {
            if (currentGroup is not null && SameGroup(currentGroup, group.GroupId)) return new(InviteKind.Already, $"Tu es déjà dans « {group.GroupName} ».", group);
            var people = People(Math.Max(1, group.Participants.Count)) + (group.Participants.Count > 0 ? " : " + string.Join(", ", group.Participants) : "");
            return new(InviteKind.Join, $"On t’invite à regarder ensemble dans « {group.GroupName} » ({people}).", group);
        }
        var host = Uri.TryCreate(invite.Server, UriKind.Absolute, out var a) ? a.Host : invite.Server;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var b) || !b.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
            return new(InviteKind.OtherServer, $"Ce groupe est sur un autre serveur Jellyfin ({host}) que celui de Mira : ouvre-le dans ton navigateur.");
        if (invite.Host is { Length: > 0 } who) return new(InviteKind.Waiting, WaitingText(who) + " : Mira peut t’y faire entrer dès que le groupe est ouvert.");
        return new(InviteKind.Gone, "Ce groupe n’existe plus : demande un nouveau lien à la personne qui t’a invité.");
    }
    /// <summary>
    /// The group of an invitation, once its host opens it: Jellyfin's groups read every <paramref name="every"/> until
    /// one is the invitation's (<see cref="FindGroup"/>). <paramref name="groups"/> gives null when Jellyfin did not
    /// answer this time. Ends by an <see cref="OperationCanceledException"/> when <paramref name="ct"/> is canceled.
    /// </summary>
    public static async Task<Group> WaitForGroupAsync(Func<CancellationToken, Task<IReadOnlyList<Group>?>> groups, Invitation invite, TimeSpan every, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await groups(ct) is { } list && FindGroup(list, invite.GroupId, invite.Host) is { } found) return found;
            await Task.Delay(every, ct);
        }
    }
    /// <summary>What the invitation says under its link: who may follow it, and from where (Tailscale, the home network).</summary>
    public static List<string> InviteSteps(string link)
    {
        var host = Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri.Host : "";
        var steps = new List<string>
        {
            "Ton ami ouvre ce lien sur son téléphone ou son ordinateur et se connecte avec son propre compte Jellyfin : il est alors dans le groupe. S’il a Mira sur son ordinateur, « Ouvrir dans Mira » l’y fait rejoindre.",
            "Pas encore de compte ? Crée-le-lui dans Mira pour Windows : Réglages → Jellyfin → Ajouter un ami, avec seulement les bibliothèques que tu choisis (ou dans Jellyfin : tableau de bord → Utilisateurs → +).",
        };
        var reach = Reach(host);
        if (reach == "tailnet") steps.Add("Hors de chez toi, il lui faut Tailscale : partage-lui l’ordinateur de Jellyfin (login.tailscale.com → Machines → … → Share), il accepte avec son compte Tailscale gratuit.");
        else if (reach == "home") steps.Add("Ce lien ne marche que sur ton Wi-Fi. Pour un ami ailleurs, installe Tailscale sur l’ordinateur de Jellyfin (Mira pour Windows : Guide → Mira sur ton téléphone).");
        steps.Add("Quand il est là, le groupe compte une personne de plus : lance le titre, il démarre chez vous deux au même moment.");
        return steps;
    }
    /// <summary>Where a host is reached from: Tailscale, the home network only, or the Internet.</summary>
    private static string Reach(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ip.GetAddressBytes() is [var a, var b, _, _])
            return a == 100 && b is >= 64 and < 128 ? "tailnet" : a == 10 || a == 192 && b == 168 || a == 172 && b is >= 16 and < 32 || a == 127 ? "home" : "internet";
        if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)) return "tailnet";
        return !host.Contains('.') || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ? "home" : "internet";
    }
    private static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } text ? text : null;
}
