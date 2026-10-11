using System.Diagnostics;
using System.Globalization;
using Mira.Core;

namespace Mira.Desktop;

public partial class MainWindow
{
    // Exercises the real UI entry points with the synthetic clip, never a personal library item.
    private async Task RunCompletionChecksAsync(Action<bool, string> require)
    {
        async Task Until(Func<bool> predicate)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate()) { if (watch.Elapsed.TotalSeconds > 8) throw new TimeoutException("Completion check timed out."); await Task.Delay(30); }
        }
        MediaItem Episode(string id) => new() { Id = id, Name = id, SeriesName = "Validation", SeriesId = "qa-series", Type = "Episode", RunTimeTicks = TimeSpan.FromSeconds(18).Ticks };
        async Task At(MediaItem item, double position)
        {
            await PlayAsync(item); await Until(() => _loaded);
            _mpv!.Set("pause", "yes"); _mpv.Command("seek", position.ToString(CultureInfo.InvariantCulture), "absolute+exact");
            HoldPosition(position); await Task.Delay(180); _mpv.Poll();
        }
        var originalAuto = _settings.AutoNext; _settings.AutoNext = false;
        var partial = Episode("qa-partial"); await At(partial, 3);
        _segments = []; _activeSkip = null; _skipToNext = false; _nextItem = Episode("qa-after-partial");
        Next_Click(this, new()); await Until(() => _playingItem?.Id == "qa-after-partial" && _loaded);
        require(!partial.UserData.Played && partial.Progress > 0, "manual next in the middle keeps the episode unwatched and resumable");

        var credits = Episode("qa-credits"); await At(credits, 13);
        _segments = [new SkipSegment(SkipKind.Outro, 12, 18)]; _activeSkip = null; _nextItem = Episode("qa-after-credits");
        UpdateSkip(); require(_skipToNext, "outro shortcut identifies the next-episode action below 90 percent");
        Skip_Click(this, new()); await Until(() => _playingItem?.Id == "qa-after-credits" && _loaded);
        require(credits.UserData.Played && credits.Progress == 0, "next-episode pill marks the previous episode watched below 90 percent");

        var automatic = Episode("qa-countdown"); await At(automatic, 13); _settings.AutoNext = true;
        _segments = [new SkipSegment(SkipKind.Outro, 12, 18)]; _activeSkip = null; _nextItem = Episode("qa-after-countdown");
        UpdateSkip(); _countdownEnd = _position; UpdateSkip();
        await Until(() => _playingItem?.Id == "qa-after-countdown" && _loaded);
        require(automatic.UserData.Played && automatic.Progress == 0, "automatic countdown marks the previous episode watched and clears its progress");

        _settings.AutoNext = false; var ended = Episode("qa-eof"); await At(ended, 4);
        PlaybackEnded(true, _playGeneration); await Until(() => !_playing && _mpv is null);
        require(ended.UserData.Played && ended.Progress == 0, "end-of-file completes playback even when mpv retains an earlier time position");

        // Use the real stop/mini paths and rendered row, with synthetic entries in the demo profile.
        var films = new[] { Episode("qa-recent-a") with { Type = "Movie", SeriesId = null }, Episode("qa-recent-b") with { Type = "Movie", SeriesId = null } };
        _demoItems!.AddRange(films); _view = "home";
        await At(films[0], 3); await StopPlaybackAsync(); ReturnToLibrary();
        await At(films[1], 4); await StopPlaybackAsync(); ReturnToLibrary();
        require(ResumeCards.Children.OfType<Views.MediaCard>().First().Item.Id == films[1].Id, "returning after playback puts the last watched film first in the rendered resume row");
        ResumeScroll.ScrollToHorizontalOffset(200); UpdateLayout();
        await At(films[0], 5); SetMiniPlayer(true);
        await Until(() => ResumeCards.Children.OfType<Views.MediaCard>().First().Item.Id == films[0].Id);
        await Task.Delay(80);
        require(ResumeScroll.HorizontalOffset < 1 && films[0].Progress > 0, "mini-player updates recency and returns the resume row to its left edge without stopping playback");
        await StopPlaybackAsync(); ReturnToLibrary();
        _demoItems.RemoveAll(x => films.Any(f => f.Id == x.Id));
        _settings.AutoNext = originalAuto;
    }
}
