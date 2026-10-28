using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mira.Core.Subtitles;

namespace Mira.Jellyfin;

/// <summary>
/// The Réglages of « Sous-titres automatiques », for Jellyfin's administrators only: the settings (secrets never given
/// back), their change, a try of each source, the journal, and a search scored by Mira for one title (what the apps
/// show in « Chercher des sous-titres », with the reasons in the language of the request).
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Mira/subtitles")]
public sealed class SubtitlesController : ControllerBase
{
    private readonly ILibraryManager _library;
    private readonly MiraSubtitleProvider _provider;

    public SubtitlesController(ILibraryManager library, IHttpClientFactory clients, IMediaSourceManager mediaSources, Microsoft.Extensions.Logging.ILogger<MiraSubtitleProvider> logger)
    {
        _library = library;
        _provider = new MiraSubtitleProvider(library, mediaSources, clients, logger);
    }

    private bool French => RelayText.French(Request);

    /// <summary>The settings and the journal.</summary>
    [HttpGet("")]
    public IActionResult Get() => Answer(new JsonObject { ["settings"] = Plugin.Subtitles.Public(), ["journal"] = JournalJson(Plugin.SubtitleJournal.Recent()) });

    /// <summary>Changes the settings (the fields given); answers with them.</summary>
    [HttpPut("settings")]
    [RequestSizeLimit(8_192)]
    public async Task<IActionResult> Put(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        JsonObject? changes;
        try { changes = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { changes = null; }
        if (changes is null) return BadRequest();
        Plugin.Subtitles.Update(changes);
        return Answer(Plugin.Subtitles.Public());
    }

    /// <summary>Tries each source set: a search for a well-known film, and the account when one is set. Says what each answered.</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test(CancellationToken cancellationToken)
    {
        var settings = Plugin.Subtitles.Current;
        var results = new JsonArray();
        var query = new SubtitleQuery(false, null, null, "tt0133093", "603", null, null, null, null, "The Matrix", 1999);
        foreach (var source in _provider.Sources(settings))
        {
            var entry = new JsonObject { ["source"] = source.Name };
            try
            {
                if (source is OpenSubtitlesApi os) await os.LoginAsync(cancellationToken).ConfigureAwait(false);
                var found = await source.SearchAsync(query, "en", cancellationToken).ConfigureAwait(false);
                entry["ok"] = true; entry["found"] = found.Count;
                if (source is OpenSubtitlesApi { Quota: { } quota }) { entry["remaining"] = quota.Remaining; entry["reset"] = quota.Reset?.ToString("o"); }
            }
            catch (SubtitleSourceException ex) { entry["ok"] = false; entry["error"] = ex.Message; entry["refused"] = ex.Refused; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { entry["ok"] = false; entry["error"] = ex.Message; }
            results.Add(entry);
        }
        return Answer(new JsonObject { ["sources"] = results });
    }

    /// <summary>
    /// The candidates of the sources for one title in one language (ISO 639-2), scored, best first, reasons in the
    /// language of the request. The ids are those Jellyfin's own download takes (Providers/Subtitles/Subtitles/{id}),
    /// once prefixed as Jellyfin does (<paramref name="prefix"/> is left to the app, which reads them from Jellyfin's list).
    /// </summary>
    [HttpGet("search/{itemId}/{language}")]
    public async Task<IActionResult> Search(string itemId, string language, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(itemId, out var id) || _library.GetItemById(id) is not Video video) return NotFound();
        if (SubtitleLanguages.Two(language) is not { } two) return Answer(new JsonObject { ["results"] = new JsonArray(), ["unknownLanguage"] = true });
        var settings = Plugin.Subtitles.Current;
        var sources = _provider.Sources(settings);
        var ranked = sources.Count == 0 ? [] : await _provider.RankAsync(video, sources, two, settings.Preferences, false, cancellationToken).ConfigureAwait(false);
        var french = French;
        var results = new JsonArray(ranked.Select(x => (JsonNode)new JsonObject
        {
            ["id"] = MiraSubtitleProvider.Id(x.Candidate),
            ["source"] = x.Candidate.Source,
            ["name"] = x.Candidate.ReleaseName,
            ["score"] = x.Score,
            ["verdict"] = x.Verdict.ToString(),
            ["reasons"] = new JsonArray(x.Reasons.Select(r => (JsonNode)SubtitleMatch.Text(r, french)).ToArray()),
            ["hashMatch"] = x.Candidate.HashMatch,
            ["hearingImpaired"] = x.Candidate.HearingImpaired,
            ["downloads"] = x.Candidate.Downloads,
        }).ToArray());
        return Answer(new JsonObject { ["results"] = results, ["sources"] = new JsonArray(sources.Select(s => (JsonNode)s.Name).ToArray()) });
    }

    private static JsonArray JournalJson(IEnumerable<SubtitleJournalEntry> entries) => new(entries.Select(e => (JsonNode)new JsonObject
    {
        ["when"] = e.When.ToString("o"), ["itemId"] = e.ItemId, ["name"] = e.Name, ["language"] = e.Language, ["kind"] = e.Kind,
        ["source"] = e.Source, ["release"] = e.Release, ["score"] = e.Score, ["detail"] = e.Detail,
    }).ToArray());

    private ContentResult Answer(JsonNode node) => Content(node.ToJsonString(), "application/json");
}
