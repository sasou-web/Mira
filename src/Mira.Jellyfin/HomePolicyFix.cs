using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mira.Jellyfin;

/// <summary>
/// The tailnet's policy put right for « Le foyer » by Mira itself, so the administrator never pastes a line: the
/// home's tag added to tagOwners, « * » (and autogroup:tagged) in a rule's sources narrowed to people, the people a
/// device was shared with and the tailnet's other tags, and the one rule that lets the home's devices reach this
/// Jellyfin. Edited as text, in the HuJSON Tailscale keeps: comments, order and spacing stay as the administrator wrote
/// them. Careful: a plan is given only when the policy it makes passes <see cref="HomeTailnet.CheckPolicy"/>; a rule
/// Mira cannot narrow safely (the tag named with every port, a range of the tailnet, SSH) is left to the administrator.
/// </summary>
public static class HomePolicyFix
{
    /// <summary>
    /// One change: « tag » (tagOwners), « grant » (the rule to Jellyfin), « narrow » (a rule's sources, as they become),
    /// « unshare » (a rule the people this PC is shared with leave: its sources as they become, none when it goes),
    /// « shared » (their one rule to Jellyfin), « funnel » (Funnel allowed on this PC), « key » (this PC's key no longer
    /// expires: not in the policy, see <see cref="HomeController"/>).
    /// </summary>
    public sealed record Change(string Kind, string? Section = null, int Number = 0, IReadOnlyList<string>? Sources = null);

    /// <summary>The policy as it becomes, and what changed in it.</summary>
    public sealed record Plan(string Text, IReadOnlyList<Change> Changes)
    {
        /// <summary>The lines of the policy that are new or changed, as « See the exact lines » shows them.</summary>
        public IReadOnlyList<string> Lines { get; init; } = [];
    }

    /// <summary>
    /// What to change in <paramref name="text"/> (the policy as HuJSON) for the home's devices to reach this PC's
    /// Jellyfin and nothing else; null when it already does, or when Mira cannot get there safely. With
    /// <paramref name="harden"/> (« Make the changes »), also: the people this PC is shared with limited to its Jellyfin
    /// (and Mira through Funnel's port, <see cref="Shared"/>), and Funnel allowed on this PC only (<see cref="Funnel"/>);
    /// a plan is then given for those alone, when the home's devices already reach only Jellyfin.
    /// </summary>
    public static Plan? Make(string text, string server, int port, string tag = HomeTailnet.Tag, bool harden = false, bool funnel = true)
    {
        if (Parse(text) is not JsonObject policy)
        {
            return null;
        }

        var homeOk = HomeTailnet.CheckPolicy(policy, server, port, tag).Verdict == HomeTailnet.Verdict.Ok;
        if (homeOk && !harden)
        {
            return null;
        }

        var changes = new List<Change>();
        var original = text;
        var everyoneNarrowed = false;
        try
        {
            if (!homeOk)
            {
                if (Home(text, server, port, tag, harden, changes, out everyoneNarrowed) is not { } home)
                {
                    return null;
                }

                text = home;
            }

            if (harden)
            {
                text = Shared(text, server, port, changes, everyoneNarrowed);
                if (funnel)
                {
                    text = Funnel(text, server, changes);
                }
            }
        }
        catch (FormatException)
        {
            return null;
        }

        return Parse(text) is JsonObject made && HomeTailnet.CheckPolicy(made, server, port, tag).Verdict == HomeTailnet.Verdict.Ok && changes.Count > 0
            ? new Plan(text, changes) { Lines = Added(original, text) }
            : null;
    }

    /// <summary>The home's part: its tag, every open rule narrowed, the rule to Jellyfin; null when Mira can't.</summary>
    private static string? Home(string text, string server, int port, string tag, bool harden, List<Change> changes, out bool everyoneNarrowed)
    {
        everyoneNarrowed = false;

        // 1. The tag, then every open rule that only « * » or autogroup:tagged opens.
        var root = Read(text);
        var edits = new List<(int At, int Remove, string Insert)>();
        var owners = Member(root, "tagOwners");
        // Never Mira's own tags: a wide rule narrowed to them would let the home's or friends' devices out again.
        var tags = (owners as HuObject)?.Members.Select(x => x.Key).Where(x => x.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(x, HomeTailnet.Tag, StringComparison.OrdinalIgnoreCase) && !string.Equals(x, HomeTailnet.FriendTag, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        if (owners is not HuObject ownersObject || !ownersObject.Members.Any(x => string.Equals(x.Key, tag, StringComparison.OrdinalIgnoreCase)))
        {
            var line = HomeTailnet.Setup(server, port, tag).TagOwners + ",";
            edits.Add(owners is HuObject o ? First(text, o, line) : Section(text, root, $"\"tagOwners\": {{{Line(Inner(text, root))}{line}{Line(Indent(text, root))}}},"));
            changes.Add(new Change("tag"));
        }

        // The tag first: until it is there, the policy says nothing of the rules that would let its devices through.
        text = Apply(text, edits);
        edits.Clear();
        root = Read(text);
        if (Parse(text) is not JsonObject tagged)
        {
            return null;
        }

        var (_, open) = HomeTailnet.CheckPolicy(tagged, server, port, tag);
        foreach (var rule in open)
        {
            if (rule.Section is not ("acls" or "grants") || Member(root, rule.Section) is not HuArray rules || rule.Number > rules.Items.Count
                || rules.Items[rule.Number - 1] is not HuObject row || (Member(row, "src") ?? Member(row, "users")) is not HuArray sources)
            {
                return null;
            }

            var kept = sources.Items.OfType<HuString>().Select(x => x.Value).Where(x => x != "*" && !string.Equals(x, "autogroup:tagged", StringComparison.OrdinalIgnoreCase)).ToList();
            var wide = sources.Items.OfType<HuString>().Where(x => x.Value == "*" || string.Equals(x.Value, "autogroup:tagged", StringComparison.OrdinalIgnoreCase)).ToList();
            if (wide.Count == 0 || sources.Items.Count != sources.Items.OfType<HuString>().Count())
            {
                return null;
            }

            var everyone = wide.Any(x => x.Value == "*");
            // « * » held the people this PC is shared with: hardened, they get their own rule to Jellyfin instead (Shared).
            everyoneNarrowed |= everyone && harden;
            var instead = (everyone ? harden ? new[] { "autogroup:member" } : new[] { "autogroup:member", HomeTailnet.Shared } : []).Concat(tags)
                .Where(x => !kept.Contains(x, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (instead.Count == 0 && kept.Count == 0)
            {
                return null;
            }

            // The first wide source becomes the narrower list (or goes, when nothing replaces it); the others go.
            edits.Add(instead.Count > 0 ? (wide[0].Start, wide[0].End - wide[0].Start, string.Join(", ", instead.Select(Quote))) : Drop(text, wide[0]));
            foreach (var other in wide.Skip(1))
            {
                edits.Add(Drop(text, other));
            }

            changes.Add(new Change("narrow", rule.Section, rule.Number, kept.Concat(instead).ToList()));
        }

        text = Apply(text, edits);
        if (Parse(text) is not JsonObject narrowed)
        {
            return null;
        }

        // 2. The rule to Jellyfin, when nothing lets the home's devices reach it.
        if (HomeTailnet.CheckPolicy(narrowed, server, port, tag).Verdict == HomeTailnet.Verdict.NoAccess)
        {
            root = Read(text);
            var (_, grant) = HomeTailnet.Setup(server, port, tag);
            text = Apply(text, [Rule(text, root, grant)]);
            changes.Add(new Change("grant"));
        }

        return text;
    }

    /// <summary>
    /// The people this PC is shared with (autogroup:shared) taken out of every rule that lets them reach more than its
    /// Jellyfin (a rule they alone were in goes), then given the one rule to Jellyfin and Funnel's port, when they had
    /// any access before. Anything Mira can't change safely (« * » still wide, a source it can't read) is left as it is.
    /// </summary>
    private static string Shared(string text, string server, int port, List<Change> changes, bool everyoneNarrowed)
    {
        if (Parse(text) is not JsonObject policy)
        {
            return text;
        }

        var (reaches, open) = HomeTailnet.CheckShared(policy, server, port);
        var root = Read(text);
        var edits = new List<(int At, int Remove, string Insert)>();
        var found = new List<Change>();
        foreach (var rule in open)
        {
            if (Member(root, rule.Section) is not HuArray rules || rule.Number > rules.Items.Count || rules.Items[rule.Number - 1] is not HuObject row
                || (Member(row, "src") ?? Member(row, "users")) is not HuArray sources || sources.Items.Count != sources.Items.OfType<HuString>().Count()
                || sources.Items.OfType<HuString>().Any(x => x.Value.Trim() == "*"))
            {
                // « * » there: the home's part would have narrowed it, unless Mira couldn't. Left to the administrator.
                return text;
            }

            var shared = sources.Items.OfType<HuString>().Where(x => string.Equals(x.Value.Trim(), HomeTailnet.Shared, StringComparison.OrdinalIgnoreCase)).ToList();
            var others = sources.Items.Count - shared.Count;
            if (others == 0)
            {
                edits.Add(Drop(text, row));
                found.Add(new Change("unshare", rule.Section, rule.Number, []));
            }
            else
            {
                edits.AddRange(shared.Select(x => Drop(text, x)));
                found.Add(new Change("unshare", rule.Section, rule.Number, sources.Items.OfType<HuString>().Select(x => x.Value).Where(x => !shared.Any(s => s.Value == x)).ToList()));
            }
        }

        string after;
        try
        {
            after = Apply(text, edits, strict: true);
        }
        catch (FormatException)
        {
            return text;
        }

        if (Parse(after) is not JsonObject made)
        {
            return text;
        }

        // A rule of their own, to Jellyfin only, when they reached anything before (no sharing at all: nothing added).
        if ((found.Count > 0 || everyoneNarrowed) && !HomeTailnet.CheckShared(made, server, port).Reaches)
        {
            after = Apply(after, [Rule(after, Read(after), HomeTailnet.SharedGrant(server, port))]);
            found.Add(new Change("shared"));
        }

        changes.AddRange(found);
        return after;
    }

    /// <summary>
    /// Funnel allowed for this PC alone (a node attribute « funnel » targeting its Tailscale address), so Mira can open
    /// /Mira to the Internet without Tailscale's consent page; nothing when some attribute already gives Funnel. Which
    /// paths Funnel serves is Mira's command (/Mira only), never the policy.
    /// </summary>
    private static string Funnel(string text, string server, List<Change> changes)
    {
        if (HomeTailnet.FunnelAllowed(Parse(text)))
        {
            return text;
        }

        var root = Read(text);
        var line = HomeTailnet.FunnelAttribute(server) + ",";
        text = Apply(text, [Member(root, "nodeAttrs") is HuArray attrs
            ? First(text, attrs, line)
            : Section(text, root, $"\"nodeAttrs\": [{Line(Inner(text, root))}{line}{Line(Indent(text, root))}],")]);
        changes.Add(new Change("funnel"));
        return text;
    }

    /// <summary>A rule put first in grants (the section made when there is none).</summary>
    private static (int, int, string) Rule(string text, HuObject root, string rule) =>
        Member(root, "grants") is HuArray grants
            ? First(text, grants, rule + ",")
            : Section(text, root, $"\"grants\": [{Line(Inner(text, root))}{rule},{Line(Indent(text, root))}],");

    /// <summary>The lines of <paramref name="after"/> that <paramref name="before"/> does not have, trimmed.</summary>
    private static List<string> Added(string before, string after)
    {
        var had = before.Split('\n').Select(x => x.Trim()).ToHashSet(StringComparer.Ordinal);
        return after.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !had.Contains(x)).ToList();
    }

    /// <summary>A policy read as Tailscale reads it (HuJSON: comments, trailing commas).</summary>
    public static JsonNode? Parse(string? text)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- Edits ----

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static string Line(string indent) => "\n" + indent;

    private static string Apply(string text, List<(int At, int Remove, string Insert)> edits, bool strict = false)
    {
        // Strict: two edits that overlap (two neighbours dropped with the same comma) are refused, never half made.
        var sorted = edits.OrderBy(x => x.At).ToList();
        if (strict && sorted.Zip(sorted.Skip(1)).Any(x => x.First.At + x.First.Remove > x.Second.At))
        {
            throw new FormatException("Edits overlap.");
        }

        var result = new StringBuilder(text);
        foreach (var (at, remove, insert) in edits.OrderByDescending(x => x.At))
        {
            result.Remove(at, remove).Insert(at, insert);
        }

        return result.ToString();
    }

    /// <summary>A line put first in an object or an array, at the indent of what is already there.</summary>
    private static (int, int, string) First(string text, HuNode container, string line)
    {
        var items = container switch { HuObject o => o.Members.Select(x => x.KeyStart).ToList(), HuArray a => a.Items.Select(x => x.Start).ToList(), _ => [] };
        if (items.Count > 0 && text.IndexOf('\n', container.Start, items[0] - container.Start) >= 0)
        {
            return (container.Start + 1, 0, Line(LineIndent(text, items[0])) + line);
        }

        var outer = LineIndent(text, container.Start);
        return (container.Start + 1, 0, Line(outer + "\t") + line + (items.Count == 0 ? Line(outer) : " "));
    }

    /// <summary>A new section, put first in the policy.</summary>
    private static (int, int, string) Section(string text, HuObject root, string section) =>
        (root.Start + 1, 0, Line(Indent(text, root)) + section + (root.Members.Count == 0 ? "\n" : ""));

    /// <summary>The indent of the policy's own sections.</summary>
    private static string Indent(string text, HuObject root) =>
        root.Members.Count > 0 && text.IndexOf('\n', root.Start, root.Members[0].KeyStart - root.Start) >= 0 ? LineIndent(text, root.Members[0].KeyStart) : "\t";

    /// <summary>The indent inside a new section: the policy's own, twice when it is spaces.</summary>
    private static string Inner(string text, HuObject root) => Indent(text, root) is var indent && indent.StartsWith(' ') ? indent + indent : indent + "\t";

    private static string LineIndent(string text, int at)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        var end = start;
        while (end < at && text[end] is ' ' or '\t')
        {
            end++;
        }

        return text[start..end];
    }

    /// <summary>A value of an array removed, with the comma after it (or before it, for the last one).</summary>
    private static (int, int, string) Drop(string text, HuNode item)
    {
        var after = item.End;
        while (after < text.Length && char.IsWhiteSpace(text[after]))
        {
            after++;
        }

        if (after < text.Length && text[after] == ',')
        {
            after++;
            while (after < text.Length && text[after] is ' ' or '\t')
            {
                after++;
            }

            return (item.Start, after - item.Start, "");
        }

        var before = item.Start - 1;
        while (before >= 0 && char.IsWhiteSpace(text[before]))
        {
            before--;
        }

        return before >= 0 && text[before] == ',' ? (before, item.End - before, "") : (item.Start, item.End - item.Start, "");
    }

    // ---- HuJSON, with each value's place ----

    private abstract record HuNode(int Start, int End);
    private sealed record HuObject(int Start, int End, List<(string Key, int KeyStart, HuNode Value)> Members) : HuNode(Start, End);
    private sealed record HuArray(int Start, int End, List<HuNode> Items) : HuNode(Start, End);
    private sealed record HuString(int Start, int End, string Value) : HuNode(Start, End);
    private sealed record HuOther(int Start, int End) : HuNode(Start, End);

    private static HuNode? Member(HuNode node, string name) =>
        node is HuObject o ? o.Members.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase)).Value : null;

    private static HuObject Read(string text)
    {
        var at = 0;
        var value = Value(text, ref at);
        return value as HuObject ?? throw new FormatException("Not an object.");
    }

    private static void Skip(string text, ref int at)
    {
        while (at < text.Length)
        {
            if (char.IsWhiteSpace(text[at]))
            {
                at++;
            }
            else if (text[at] == '/' && at + 1 < text.Length && text[at + 1] == '/')
            {
                var end = text.IndexOf('\n', at);
                at = end < 0 ? text.Length : end + 1;
            }
            else if (text[at] == '/' && at + 1 < text.Length && text[at + 1] == '*')
            {
                var end = text.IndexOf("*/", at + 2, StringComparison.Ordinal);
                at = end < 0 ? throw new FormatException("Comment not closed.") : end + 2;
            }
            else
            {
                return;
            }
        }
    }

    private static HuNode Value(string text, ref int at)
    {
        Skip(text, ref at);
        if (at >= text.Length)
        {
            throw new FormatException("End of text.");
        }

        var start = at;
        switch (text[at])
        {
            case '{':
            {
                at++;
                var members = new List<(string, int, HuNode)>();
                while (true)
                {
                    Skip(text, ref at);
                    if (at < text.Length && text[at] == '}')
                    {
                        at++;
                        return new HuObject(start, at, members);
                    }

                    var key = Value(text, ref at) as HuString ?? throw new FormatException("A key that is not a string.");
                    Skip(text, ref at);
                    if (at >= text.Length || text[at] != ':')
                    {
                        throw new FormatException("No colon.");
                    }

                    at++;
                    members.Add((key.Value, key.Start, Value(text, ref at)));
                    Comma(text, ref at, '}');
                }
            }
            case '[':
            {
                at++;
                var items = new List<HuNode>();
                while (true)
                {
                    Skip(text, ref at);
                    if (at < text.Length && text[at] == ']')
                    {
                        at++;
                        return new HuArray(start, at, items);
                    }

                    items.Add(Value(text, ref at));
                    Comma(text, ref at, ']');
                }
            }
            case '"':
            {
                at++;
                while (at < text.Length && text[at] != '"')
                {
                    at += text[at] == '\\' ? 2 : 1;
                }

                if (at >= text.Length)
                {
                    throw new FormatException("String not closed.");
                }

                at++;
                try
                {
                    return new HuString(start, at, JsonSerializer.Deserialize<string>(text[start..at]) ?? "");
                }
                catch (JsonException)
                {
                    throw new FormatException("Bad string.");
                }
            }
            default:
            {
                while (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] is not (',' or ']' or '}' or '/'))
                {
                    at++;
                }

                return at > start ? new HuOther(start, at) : throw new FormatException("Unexpected character.");
            }
        }
    }

    private static void Comma(string text, ref int at, char close)
    {
        Skip(text, ref at);
        if (at < text.Length && text[at] == ',')
        {
            at++;
        }
        else if (at >= text.Length || text[at] != close)
        {
            throw new FormatException("No comma.");
        }
    }
}
