using System.IO;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Services;

namespace Mira.Desktop;

/// <summary>« Nouvel épisode » notifications of Windows, for the series this account follows (<see cref="NewEpisodes"/>).</summary>
public partial class MainWindow
{
    // Jellyfin's socket says when the library changes, in bursts during a scan: one look once it settles. The clock
    // catches what the socket missed.
    private readonly DispatcherTimer _episodeSoon = new() { Interval = TimeSpan.FromSeconds(90) };
    private readonly DispatcherTimer _episodeClock = new() { Interval = TimeSpan.FromMinutes(30) };
    private bool _episodesLooking;
    private string NoticeImages => Path.Combine(_profile.DirectoryPath, "notices");
    private void InitializeEpisodeNotices()
    {
        _episodeSoon.Tick += async (_, _) => { _episodeSoon.Stop(); await LookForNewEpisodesAsync(); };
        _episodeClock.Tick += async (_, _) => await LookForNewEpisodesAsync();
    }
    private void StartEpisodeNotices() { if (!_demo) { _episodeClock.Start(); EpisodesMayHaveArrived(); } }
    private void StopEpisodeNotices() { _episodeSoon.Stop(); _episodeClock.Stop(); }
    private void EpisodesMayHaveArrived()
    {
        if (_demo || _client is null || !_settings.EpisodeNotices) return;
        _episodeSoon.Stop(); _episodeSoon.Start();
    }
    private async Task LookForNewEpisodesAsync()
    {
        if (_demo || _closing || _client is not { } client || _episodesLooking || !_settings.EpisodeNotices) return;
        _episodesLooking = true;
        var account = client.Connection.Server + " " + client.Connection.UserId;
        try
        {
            var (episodes, series) = await client.RecentEpisodesAsync(_connectionLifetime.Token);
            if (!ReferenceEquals(client, _client) || _closing || !_settings.EpisodeNotices) return;
            // The first look for this account only sets the mark: what was already there is not news.
            var seen = _settings.EpisodesSeenFor == account ? _settings.EpisodesSeenUntil : null;
            if (_settings.EpisodesSeenFor == account && seen is { } mark)
            {
                var (notices, more) = NewEpisodes.Pick(episodes, series, mark);
                foreach (var notice in notices) await NotifyAsync(client, notice);
                if (more is not null) Toasts.Show("Nouveaux épisodes", more, null, () => Dispatcher.BeginInvoke(RestoreFromWindows));
            }
            // A library without an episode to watch yet: the next one is news.
            var newest = NewEpisodes.Newest(episodes, seen) ?? DateTimeOffset.MinValue;
            if (_settings.EpisodesSeenFor != account || newest != seen) { _settings.EpisodesSeenFor = account; _settings.EpisodesSeenUntil = newest; TrySaveSettings(); }
            ForgetOldNoticeImages();
        }
        // A server that does not answer now is asked again at the next change or on the clock.
        catch (Exception ex) when (IsExpected(ex) || ex is OperationCanceledException) { }
        finally { _episodesLooking = false; }
    }
    private async Task NotifyAsync(JellyfinClient client, NewEpisodes.Notice notice)
    {
        string? image = null;
        if (notice.Episode.ImageTags.ContainsKey("Primary"))
        {
            try
            {
                var bytes = await client.ImageAsync(notice.Episode.Id, "Primary", 728, _connectionLifetime.Token);
                Directory.CreateDirectory(NoticeImages);
                var path = Path.Combine(NoticeImages, notice.Episode.Id + ".jpg");
                await File.WriteAllBytesAsync(path, bytes);
                image = path;
            }
            catch (Exception ex) when (IsExpected(ex) || ex is OperationCanceledException or IOException or UnauthorizedAccessException) { }
        }
        Toasts.Show(notice.Title, notice.Text, image, () => Dispatcher.InvokeAsync(() => OpenNoticeAsync(client, notice.SeriesId)));
    }
    /// <summary>A notification clicked: Mira comes forward on the series, unless something plays.</summary>
    private async Task OpenNoticeAsync(JellyfinClient client, string seriesId)
    {
        if (_closing) return;
        RestoreFromWindows();
        if (_playing || !ReferenceEquals(client, _client)) return;
        try { await ShowDetailsAsync(await client.ItemAsync(seriesId)); }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); }
    }
    /// <summary>Windows shows a notification's picture from its file, as long as it stays in the notification centre (3 days).</summary>
    private void ForgetOldNoticeImages()
    {
        try
        {
            if (!Directory.Exists(NoticeImages)) return;
            foreach (var file in Directory.EnumerateFiles(NoticeImages, "*.jpg"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(4)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
