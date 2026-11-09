using System.IO;
using System.Text.Json;
using Mira.Core;
using static TestSupport;

/// <summary>Réglages → « Exporter un rapport » (DiagnosticReport, RecentPlaybacks).</summary>
internal static class DiagnosticsTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Rapport de diagnostic : rien de privé ne sort, réseau nommé par sa sorte (tests/fixtures/diagnostics.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "diagnostics.json")));
            foreach (var row in fixture.RootElement.GetProperty("redact").EnumerateArray())
            {
                var found = DiagnosticReport.Redact(row[0].GetString()!);
                Assert(found == row[1].GetString(), $"{row[0]} → {found}");
            }
            foreach (var row in fixture.RootElement.GetProperty("network").EnumerateArray())
            {
                var found = DiagnosticReport.NetworkOf(row[0].GetString());
                Assert(found == (row[1].ValueKind == JsonValueKind.Null ? null : row[1].GetString()), $"{row[0]} → {found}");
            }
            return Task.CompletedTask;
        });

        await Test("Rapport de diagnostic : versions, faits de l'app, dernières lectures sans nom ni chemin, fin du journal", () =>
        {
            RecentPlaybacks.Clear();
            var source = new MediaSource
            {
                Id = "s", Container = "mkv", Path = @"C:\Users\ogsas\Videos\MEDIAS\FILMS\Arcane S01E01.mkv",
                MediaStreams = [new() { Type = "Video", Codec = "hevc", Height = 2160, VideoRangeType = "DOVIWithHDR10" }, new() { Type = "Audio", Codec = "eac3", Channels = 6 }],
            };
            var item = new MediaItem { Id = "i", Name = "Arcane", Type = "Episode", SeriesName = "Arcane" };
            for (var i = 0; i < RecentPlaybacks.Kept + 2; i++)
                RecentPlaybacks.Add(PlaybackTrace.Of(item, source, "http://100.88.12.34:8096", new DateTimeOffset(2026, 11, 6, 20, i, 0, TimeSpan.Zero), i == 0 ? "first" : "file as it is"));
            RecentPlaybacks.Add(PlaybackTrace.Of(item, null, "http://192.168.1.20:8096", new DateTimeOffset(2026, 11, 6, 21, 0, 0, TimeSpan.Zero), "not opened", "HttpRequestException"));
            Assert(RecentPlaybacks.All.Count == RecentPlaybacks.Kept && RecentPlaybacks.All.All(x => x.How != "first"), "Ten kept, the oldest dropped");

            var profile = Path.Combine(run.Root, "diagnostics");
            Directory.CreateDirectory(profile);
            File.WriteAllLines(Path.Combine(profile, "errors.log.old"), Enumerable.Range(0, 150).Select(i => $"old {i}"));
            File.WriteAllLines(Path.Combine(profile, "errors.log"), Enumerable.Range(0, 100).Select(i => $"new {i}"));
            var errors = DiagnosticReport.ReadErrors(profile);
            Assert(errors.Count == DiagnosticReport.ErrorLines && errors[0] == "old 50" && errors[^1] == "new 99", $"Newest lines: {errors[0]} … {errors[^1]}");
            Assert(DiagnosticReport.ReadErrors(Path.Combine(profile, "none")).Count == 0, "No log");

            var report = DiagnosticReport.Build("Mira for Windows", "http://100.88.12.34:8096", "12.1.0",
                [("Quality", "auto"), ("Device", "Mira on BUREAU-PC of ogsas"), ("Built-in Tailscale", null), ("Note", "see https://example.org/x?api_key=1")],
                RecentPlaybacks.All, errors, new DateTimeOffset(2026, 11, 6, 21, 5, 0, TimeSpan.Zero), ["ogsas", "bureau-pc", null, "x"]);
            foreach (var part in new[] { "Mira for Windows", "Jellyfin: 12.1.0", "Reached by: Tailscale", "Quality: auto", "Built-in Tailscale: -", "Device: Mira on [name] of [name]", "Note: see [address]",
                "Episode · mkv · hevc 2160p DOVIWithHDR10 · eac3 6ch · file as it is · Tailscale", "Episode · not opened · home network · failed: HttpRequestException", "new 99" })
                Assert(report.Contains(part, StringComparison.Ordinal), "Missing: " + part + "\n" + report);
            foreach (var part in new[] { "Arcane", "100.88", "192.168", "ogsas", "BUREAU", @"\Users\o", "api_key=1" })
                Assert(!report.Contains(part, StringComparison.Ordinal), "Leaked: " + part);
            Assert(DiagnosticReport.FileName(new DateTimeOffset(2026, 11, 6, 21, 5, 0, TimeSpan.Zero)) == "mira-report-20261106-2105.txt", "File name");
            RecentPlaybacks.Clear();
            return Task.CompletedTask;
        });
    }
}
