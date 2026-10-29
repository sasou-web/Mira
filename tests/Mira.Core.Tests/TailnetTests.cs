using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Mira.Core;
using Mira.Core.Tailscale;
using static TestSupport;

/// <summary>
/// Mira's own Tailscale node, seen from the apps: the reasons the Go node gives (src/Mira.Tailscale) are those the apps
/// explain, and a failure of the node is a network failure like any other.
/// </summary>
internal static class TailnetTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Tailscale intégré : chaque raison d’échec du nœud Go est connue des apps", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            var go = File.ReadAllText(Path.Combine(repository, "src", "Mira.Tailscale", "tailnet", "dial.go"));
            var codes = Regex.Matches(go, @"^\s*Code\w+\s*=\s*""([a-z-]+)""", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
            Assert(codes.Count >= 14, "dial.go: " + codes.Count + " codes read");
            var missing = codes.Except(TailnetCodes.All).ToList();
            Assert(missing.Count == 0, "Unknown to the apps: " + string.Join(", ", missing));
            // The apps' own: the helper process, its version, the state's key.
            var appsOnly = TailnetCodes.All.Except(codes).Order(StringComparer.Ordinal).ToList();
            Assert(appsOnly.SequenceEqual([TailnetCodes.Helper, TailnetCodes.Key, TailnetCodes.Version]), "Not in Go: " + string.Join(", ", appsOnly));
            Assert(TailnetCodes.All.Distinct().Count() == TailnetCodes.All.Count, "A code twice");
            return Task.CompletedTask;
        });
        await Test("Tailscale intégré : un échec du nœud est une erreur réseau attendue", () =>
        {
            var failure = new TailnetException(TailnetCodes.PeerOffline);
            Assert(failure is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError, StatusCode: null }, "Not a connection error");
            Assert(ExpectedErrors.Are(failure) && failure.Code == "peer-offline" && failure.Message == "peer-offline", "Not expected, or code lost");
            var inner = new IOException("broken pipe");
            var said = new TailnetException(TailnetCodes.Helper, "The helper ended", inner);
            Assert(said.Message == "The helper ended" && said.InnerException == inner && said.Code == TailnetCodes.Helper, "Message or cause lost");
            return Task.CompletedTask;
        });
    }
}
