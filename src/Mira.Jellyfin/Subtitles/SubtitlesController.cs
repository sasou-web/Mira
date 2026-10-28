using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
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
    private readonly ISubtitleManager _subtitles;

    public SubtitlesController(ILibraryManager library, IHttpClientFactory clients, IMediaSourceManager mediaSources, ISubtitleManager subtitles, Microsoft.Extensions.Logging.ILogger<MiraSubtitleProvider> logger)
    {
        _library = library; _subtitles = subtitles;
        _provider = new MiraSubtitleProvider(library, mediaSources, clients, logger);
    }

    private bool French => RelayText.French(Request);

    /// <summary>The settings, the journal, how many titles wait, and until when the loop waits for a quota.</summary>
    [HttpGet("")]
    public IActionResult Get() => Answer(new JsonObject
    {
        ["settings"] = Plugin.Subtitles.Public(),
        ["journal"] = JournalJson(Plugin.SubtitleJournal.Recent(), French),
        ["waiting"] = Plugin.SubtitleQueue.Count,
        ["pausedUntil"] = SubtitleAutomation.PausedUntil?.ToString("o"),
    });

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

    /// <summary>
    /// « Chercher aussi pour ce qui est déjà là »: every film and episode of the library waits once for each language of
    /// the settings (those that already have them are let go at once, without asking anything). Answers how many.
    /// </summary>
    [HttpPost("library")]
    public IActionResult QueueLibrary()
    {
        var settings = Plugin.Subtitles.Current;
        if (!settings.Enabled || settings.Languages.Count == 0) return Answer(new JsonObject { ["added"] = 0, ["waiting"] = Plugin.SubtitleQueue.Count });
        var items = _library.GetItemsResult(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode], IsVirtualItem = false, Recursive = true }).Items;
        var titles = items.OfType<Video>().Where(v => v.Path is { Length: > 0 }).SelectMany(v => settings.Languages.Select(language => (v.Id.ToString("N"), language)));
        var added = Plugin.SubtitleQueue.AddBacklog(titles);
        return Answer(new JsonObject { ["added"] = added, ["waiting"] = Plugin.SubtitleQueue.Count });
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
    /// language of the request. The ids are those of Jellyfin's own list and download (Providers/Subtitles/Subtitles/{id});
    /// none when « Mira » is turned off for the title's library.
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
        // Jellyfin's own id for a result (« <provider>_<Mira's id> »), as its list gives it and its download takes it.
        var prefix = _subtitles.GetSupportedProviders(video).FirstOrDefault(p => p.Name == MiraSubtitleProvider.ProviderName)?.Id;
        var results = new JsonArray(ranked.Select(x => (JsonNode)new JsonObject
        {
            ["id"] = prefix is null ? null : prefix + "_" + MiraSubtitleProvider.Id(x.Candidate),
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

    /// <summary>The journal, why said in the language of the request (a source's own words as they came).</summary>
    public static JsonArray JournalJson(IEnumerable<SubtitleJournalEntry> entries, bool french) => new(entries.Select(e => (JsonNode)new JsonObject
    {
        ["when"] = e.When.ToString("o"), ["itemId"] = e.ItemId, ["name"] = e.Name, ["language"] = e.Language, ["kind"] = e.Kind,
        ["source"] = e.Source, ["release"] = e.Release, ["score"] = e.Score, ["detail"] = Why(e, french),
        ["alignedOn"] = e.AlignedOn, ["shift"] = e.Shift, ["speed"] = e.Speed,
    }).ToArray());

    /// <summary>Why an entry of the journal is what it is, in French or English.</summary>
    public static string? Why(SubtitleJournalEntry e, bool french) =>
        e.Reasons is { Count: > 0 } reasons ? string.Join(" · ", reasons.Select(r => SubtitleMatch.Text(r, french)))
        : e.Check is { } check ? SubtitleMatch.Text(check, french)
        : e.Code switch
        {
            "no-reference" => french ? "rien pour le recaler : ni piste de sous-titres ni audio lisible" : "nothing to align it on: no subtitle track nor readable audio",
            "does-not-fit" => french ? "ne colle pas à la vidéo, même recalé" : "doesn’t fit the video, even aligned",
            _ => e.Detail,
        };

    private ContentResult Answer(JsonNode node) => Content(node.ToJsonString(), "application/json");
}
