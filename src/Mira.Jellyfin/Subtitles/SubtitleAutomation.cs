using System.IO;
using System.Net.Http;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mira.Core.Subtitles;

namespace Mira.Jellyfin;

/// <summary>
/// « Sous-titres automatiques » by itself: each film or episode added to the library waits in <see cref="SubtitleQueue"/>
/// for each language of the Réglages, and is tried by a loop of its own, once a minute, never while anyone is watching
/// something on Jellyfin. A try asks the sources through « Mira » (<see cref="MiraSubtitleProvider"/>), takes only what
/// is sure or good for the file (<see cref="SubtitleMatch.Automatic"/>), and has Jellyfin write it where the library
/// keeps its subtitles. With « Recaler » on (the default), a subtitle not made for this very file is aligned first on
/// when people speak in the video (<see cref="SubtitleReference"/>, <see cref="SubtitleAlign"/>): moved and sped up or
/// down when it needs it, and one that was only worth taking once aligned is taken only if it fits. Nothing good yet: tried again later; a source's quota
/// spent: everything waits for its reset. Independent of Jellyfin's own « Télécharger les sous-titres » of each
/// library, which also goes through « Mira » when it is on.
/// </summary>
public sealed class SubtitleAutomation : IHostedService, IDisposable
{
    /// <summary>Titles tried per minute at most.</summary>
    private const int PerTick = 3;
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    /// <summary>
    /// A title Jellyfin has not read yet (no length: its streams, embedded subtitles among them, are unknown), or an
    /// episode it has not numbered yet.
    /// </summary>
    private static readonly TimeSpan NotReadYet = TimeSpan.FromMinutes(10);
    /// <summary>No source set: nothing to ask, the titles wait.</summary>
    private static readonly TimeSpan NoSource = TimeSpan.FromHours(6);

    private readonly ILibraryManager _library;
    private readonly ISessionManager _sessions;
    private readonly ISubtitleManager _subtitles;
    private readonly IMediaEncoder _encoder;
    private readonly MiraSubtitleProvider _provider;
    private readonly ILogger<SubtitleAutomation> _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private DateTimeOffset _pausedUntil;

    public SubtitleAutomation(ILibraryManager library, ISessionManager sessions, ISubtitleManager subtitles, IMediaSourceManager mediaSources, IMediaEncoder encoder,
        IHttpClientFactory clients, ILogger<SubtitleAutomation> logger, ILogger<MiraSubtitleProvider> providerLogger)
    {
        _library = library; _sessions = sessions; _subtitles = subtitles; _encoder = encoder; _logger = logger;
        _provider = new MiraSubtitleProvider(library, mediaSources, clients, providerLogger);
    }

    private static SubtitleQueue Queue => Plugin.SubtitleQueue;

    /// <summary>When the loop waits for a source's quota to come back (null: it does not).</summary>
    public static DateTimeOffset? PausedUntil { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += OnItemAdded;
        _library.ItemRemoved += OnItemRemoved;
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnItemAdded;
        _library.ItemRemoved -= OnItemRemoved;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
    }

    public void Dispose() => _stop.Dispose();

    /// <summary>A film or an episode with a file of its own, for the queue.</summary>
    private static bool Wanted(BaseItem item) => item is Movie or Episode && item is Video { IsVirtualItem: false, Path.Length: > 0 };

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (!Wanted(e.Item)) return;
        var settings = Plugin.Subtitles.Current;
        if (!settings.Enabled) return;
        foreach (var language in settings.Languages) Queue.Add(e.Item.Id.ToString("N"), language);
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Video) Queue.Forget(e.Item.Id.ToString("N"));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { await TickAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Mira: automatic subtitles"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Whether anyone is watching something on Jellyfin (paused counts: they are still there).</summary>
    private bool Watching() => _sessions.Sessions.Any(s => s.NowPlayingItem is not null);

    /// <summary>A token cancelled as soon as someone starts watching (looked at every 2 s), or with <paramref name="ct"/>.</summary>
    private CancellationTokenSource WhileNobodyWatches(CancellationToken ct)
    {
        var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = watch.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (Watching()) { watch.Cancel(); return; }
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        }, CancellationToken.None);
        return watch;
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var settings = Plugin.Subtitles.Current;
        if (!settings.Enabled || Queue.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        if (now < _pausedUntil) return;
        PausedUntil = null;
        foreach (var wait in Queue.Due(PerTick))
        {
            if (Watching() || ct.IsCancellationRequested) return;
            if (!await TryAsync(wait, settings, ct).ConfigureAwait(false)) return;
        }
    }

    /// <summary>One title in one language. False when the loop should stop for now (a quota spent).</summary>
    private async Task<bool> TryAsync(SubtitleWait wait, SubtitleSettings settings, CancellationToken ct)
    {
        var two = SubtitleLanguages.Two(wait.Language);
        if (!Guid.TryParse(wait.ItemId, out var id) || _library.GetItemById(id) is not Video video || !Wanted(video) || two is null || !settings.Languages.Contains(wait.Language))
        {
            Queue.Done(wait.ItemId, wait.Language);
            return true;
        }
        if (video.RunTimeTicks is null || video is Episode && MiraSubtitleProvider.Numbers(video).Episode is null) { Queue.Postpone(wait.ItemId, wait.Language, DateTimeOffset.UtcNow + NotReadYet); return true; }
        if (Has(video, two)) { Queue.Done(wait.ItemId, wait.Language); return true; }
        var sources = _provider.Sources(settings);
        if (sources.Count == 0) { Queue.Postpone(wait.ItemId, wait.Language, DateTimeOffset.UtcNow + NoSource); return true; }
        // Through Jellyfin, as « Chercher des sous-titres »: it writes the file where the library keeps subtitles. A
        // library where « Mira » is turned off among the subtitle downloaders is left alone.
        var mira = _subtitles.GetSupportedProviders(video).FirstOrDefault(p => p.Name == MiraSubtitleProvider.ProviderName);
        if (mira is null) { Queue.Done(wait.ItemId, wait.Language); return true; }

        var ranked = await _provider.RankAsync(video, sources, two, settings.Preferences, false, ct).ConfigureAwait(false);
        var ffmpeg = _encoder.EncoderPath;
        var syncing = settings.Sync && !string.IsNullOrEmpty(ffmpeg) && File.Exists(ffmpeg) && video.Path is { Length: > 0 } && File.Exists(video.Path);
        var chosen = SubtitleMatch.Automatic(ranked, syncAvailable: syncing);
        var name = MiraSubtitleProvider.Title(video);

        // Aligning needs when people speak in the video: read once for the title, before any download (a download
        // spends the source's quota), and given up for now when someone starts watching.
        float[]? reference = null; string? referenceKind = null;
        if (syncing && chosen.Any(x => x.Verdict != MatchVerdict.Sure))
        {
            using var watch = WhileNobodyWatches(ct);
            var reader = new SubtitleReference(ffmpeg);
            try { reference = await reader.ReadAsync(video.Path!, _provider.Streams(video.Id), TimeSpan.FromTicks(video.RunTimeTicks.Value), watch.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            if (watch.IsCancellationRequested && !ct.IsCancellationRequested) return false;
            referenceKind = reader.Kind;
        }

        foreach (var score in chosen.Take(3))
        {
            var candidate = score.Candidate;
            try
            {
                if (!syncing || score.Verdict == MatchVerdict.Sure)
                {
                    // Made for this very file (or nothing to align with): as « Chercher des sous-titres » would.
                    await _subtitles.DownloadSubtitles(video, mira.Id + "_" + MiraSubtitleProvider.Id(candidate), ct).ConfigureAwait(false);
                    Plugin.SubtitleJournal.Add(new SubtitleJournalEntry(DateTimeOffset.UtcNow, wait.ItemId, name, wait.Language, "kept", candidate.Source, candidate.ReleaseName, score.Score));
                    Queue.Done(wait.ItemId, wait.Language);
                    return true;
                }
                var fetched = await _provider.GetSubtitles(MiraSubtitleProvider.Id(candidate), ct).ConfigureAwait(false);
                string text;
                using (var reader = new StreamReader(fetched.Stream, Encoding.UTF8)) text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                var cues = SubtitleText.Parse(text);
                var sync = reference is null ? null : SubtitleAlign.Align(reference, cues);
                if (sync is not { Confident: true } && score.Verdict == MatchVerdict.IfSynced)
                {
                    // Only worth taking once found to fit the video: it does not.
                    MiraSubtitleProvider.Forget(video.Id, two);
                    Plugin.SubtitleJournal.Add(new SubtitleJournalEntry(DateTimeOffset.UtcNow, wait.ItemId, name, wait.Language, "rejected", candidate.Source, candidate.ReleaseName, score.Score,
                        reference is null ? "nothing to align with" : "does not fit the video", referenceKind));
                    continue;
                }
                var format = fetched.Format;
                if (sync is { Changes: true })
                {
                    var moved = SubtitleAlign.Apply(cues, sync);
                    if (SubtitleMatch.Check(moved, TimeSpan.FromTicks(video.RunTimeTicks.Value)).Verdict == SubtitleCheckVerdict.Ok) { text = SubtitleText.ToSrt(moved); format = "srt"; }
                    else sync = null;
                }
                await _subtitles.UploadSubtitle(video, new SubtitleResponse
                {
                    Format = format,
                    Language = fetched.Language,
                    IsForced = fetched.IsForced,
                    IsHearingImpaired = fetched.IsHearingImpaired,
                    Stream = new MemoryStream(Encoding.UTF8.GetBytes(text)),
                }).ConfigureAwait(false);
                Plugin.SubtitleJournal.Add(new SubtitleJournalEntry(DateTimeOffset.UtcNow, wait.ItemId, name, wait.Language, "kept", candidate.Source, candidate.ReleaseName, score.Score, null,
                    sync is { Confident: true } ? referenceKind : null, sync is { Changes: true } ? Math.Round(sync.Shift.TotalSeconds, 2) : null, sync is { Changes: true } && sync.Ratio != 1 ? Math.Round(sync.Ratio, 5) : null));
                Queue.Done(wait.ItemId, wait.Language);
                return true;
            }
            catch (SubtitleSourceException ex) when (ex.Until is { } until)
            {
                // The source's downloads of the day are spent: every title waits for them, this one keeps its place.
                Plugin.SubtitleJournal.Add(new SubtitleJournalEntry(DateTimeOffset.UtcNow, wait.ItemId, name, wait.Language, "quota", candidate.Source, null, null, ex.Message));
                _pausedUntil = until; PausedUntil = until;
                Queue.Postpone(wait.ItemId, wait.Language, until);
                return false;
            }
            catch (Exception ex) when (ex is SubtitleSourceException or InvalidOperationException or HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // A file set aside by the check is already in the journal (FetchCheckedAsync); the next candidate is tried.
                _logger.LogWarning("Mira: subtitles for {Name} not kept: {Message}", name, ex.Message);
                if (ex is not InvalidOperationException)
                    Plugin.SubtitleJournal.Add(new SubtitleJournalEntry(DateTimeOffset.UtcNow, wait.ItemId, name, wait.Language, "failed", candidate.Source, candidate.ReleaseName, score.Score, ex.Message));
            }
        }
        var best = ranked.FirstOrDefault();
        var again = Queue.Retry(wait.ItemId, wait.Language);
        if (chosen.Count == 0)
            Plugin.SubtitleJournal.Add(new SubtitleJournalEntry(DateTimeOffset.UtcNow, wait.ItemId, name, wait.Language, again ? "none" : "given-up",
                best?.Candidate.Source, best?.Candidate.ReleaseName, best?.Score, best is null ? null : string.Join(" · ", best.Reasons.Select(r => SubtitleMatch.Text(r)))));
        return true;
    }

    /// <summary>
    /// Whether the title already has subtitles in this language, not only forced ones: in the file itself, beside it,
    /// in Jellyfin's data, or just given by « Mira ».
    /// </summary>
    private bool Has(Video video, string two) =>
        MiraSubtitleProvider.AlreadyHas(video, two)
        || _provider.Streams(video.Id).Any(s => s.Type == MediaStreamType.Subtitle && !s.IsForced && SubtitleLanguages.Two(s.Language) == two);
}
