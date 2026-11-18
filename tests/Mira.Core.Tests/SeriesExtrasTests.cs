using System.IO;
using System.Text.Json;
using Mira.Core;
using static TestSupport;

/// <summary>« La pastille »: a series' bonus videos by kind (SeriesExtras), the cases shared with the iPhone (tests/fixtures/series-extras.json).</summary>
internal static class SeriesExtrasTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Bonus d'une série : cas communs avec l'iPhone (tests/fixtures/series-extras.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "series-extras.json")));
            var x = fixture.RootElement;
            string? S(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetString();
            int? I(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt32();
            foreach (var row in x.GetProperty("read").EnumerateArray())
            {
                var read = SeriesExtras.Read(new MediaItem { Id = "x", Name = row[0].GetString()!, ExtraType = S(row[1]) }, row[2].GetString());
                Assert(read == new SeriesExtras.Reading(S(row[3]), I(row[4]), S(row[5]), I(row[6])), $"{row[0]} → {read}");
            }
            foreach (var row in x.GetProperty("title").EnumerateArray())
            {
                var title = SeriesExtras.Title(new MediaItem { Id = "x", Name = row[0].GetString()!, ExtraType = S(row[1]) }, row[2].GetString());
                Assert(title == row[3].GetString(), $"{row[0]} → {title}");
            }
            var extras = x.GetProperty("extras").EnumerateArray().Select(t => t.Deserialize<MediaItem>(Json.Options)!).ToList();
            var was = Lang.Code;
            try
            {
                foreach (var (language, at) in new[] { (Lang.French, 0), (Lang.English, 1) })
                {
                    Lang.Use(language);
                    foreach (var row in x.GetProperty("meta").EnumerateArray())
                    {
                        long? ticks = row[2].ValueKind == JsonValueKind.Null ? null : row[2].GetInt64();
                        var meta = SeriesExtras.Meta(new MediaItem { Id = "x", Name = row[0].GetString()!, ExtraType = "Unknown", RunTimeTicks = ticks }, row[1].GetString());
                        Assert(meta == row[3 + at].GetString(), $"{language} {row[0]} → {meta}");
                    }
                    var groups = SeriesExtras.Groups(extras, "Monogatari Series");
                    var expected = x.GetProperty("groups").EnumerateArray().ToList();
                    Assert(groups.Count == expected.Count, $"{language} : {string.Join(", ", groups.Select(g => g.Key))}");
                    for (var i = 0; i < Math.Min(groups.Count, expected.Count); i++)
                    {
                        var g = groups[i]; var e = expected[i];
                        var ids = string.Join(",", g.Items.Select(t => t.Id));
                        Assert(g.Key == e[0].GetString() && g.Name == e[1 + at].GetString() && ids == string.Join(",", e[3].EnumerateArray().Select(t => t.GetString())), $"{language} {g.Key} {g.Name} : {ids}");
                    }
                    var heading = SeriesExtras.Heading(extras);
                    Assert(heading == x.GetProperty("heading")[at].GetString(), $"{language} : {heading}");
                }
            }
            finally { Lang.Use(was); }
            foreach (var row in x.GetProperty("pills").EnumerateArray())
            {
                var pages = Enumerable.Range(1, row[0].GetInt32()).Select(n => new EpisodePage(n, 0, 0, $"Saison {n}")).ToList();
                Assert(SeriesExtras.Pills(pages, row[1].GetInt32()) == row[2].GetBoolean(), $"Pastilles {row}");
            }
            Assert(SeriesExtras.Shown(48, 4, false) == 4 && SeriesExtras.Shown(3, 4, false) == 3 && SeriesExtras.Shown(48, 4, true) == 48, "Une ligne, puis tout.");
            return Task.CompletedTask;
        });
    }
}
