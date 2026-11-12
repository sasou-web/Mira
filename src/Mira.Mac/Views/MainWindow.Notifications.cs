using Avalonia.Controls;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// Notifications of the Mac (MacNotifications), as Mira for Windows': « Nouvel épisode » for the series this account
// follows (NewEpisodes, the same rules and the same mark), and someone who joined the group while Mira is in the Dock or
// behind another window. Never for the checks, never for a party's guest (the friend's Jellyfin is not this account's).
public sealed partial class MainWindow
{
    // Jellyfin's socket says when the library changes, in bursts during a scan: one look once it settles. The clock
    // catches what the socket missed.
    private readonly DispatcherTimer _episodeSoon = new() { Interval = TimeSpan.FromSeconds(90) };
    private readonly DispatcherTimer _episodeClock = new() { Interval = TimeSpan.FromMinutes(30) };
    private bool _episodesLooking;
    private string NoticeImages => Path.Combine(Profile.DirectoryPath, "notices");
    /// <summary>The checks run Mira without macOS's notifications, as Windows' validation profiles.</summary>
    private bool ChecksRun => Args.Any(x => x is "--self-check" or "--tailnet-check" or "--gallery" or "--smooth-check");

    private void InitializeNotifications()
    {
        MacNotifications.Quiet = ChecksRun;
        MacNotifications.Failed = ex => ErrorLog.Append(Profile.DirectoryPath, ex);
        _episodeSoon.Tick += async (_, _) => { _episodeSoon.Stop(); await LookForNewEpisodesAsync(); };
        _episodeClock.Tick += async (_, _) => await LookForNewEpisodesAsync();
        Closed += (_, _) => StopEpisodeNotices();
    }
    private void StartEpisodeNotices() { if (!ChecksRun) { _episodeClock.Start(); EpisodesMayHaveArrived(); } }
    private void StopEpisodeNotices() { _episodeSoon.Stop(); _episodeClock.Stop(); }
    /// <summary>The library changed on the server (or the setting was switched on): a look once it settles.</summary>
    public void EpisodesMayHaveArrived()
    {
        if (ChecksRun || Session is null || InParty || !Settings.EpisodeNotices) return;
        _episodeSoon.Stop(); _episodeSoon.Start();
    }

    /// <summary>
    /// The episodes Jellyfin added since the last look, of the series this account follows: a notice for each series
    /// (three at most, a line for the rest). The first look for an account only sets the mark.
    /// </summary>
    public async Task LookForNewEpisodesAsync()
    {
        if (_closing || Session is not { } session || InParty || _episodesLooking || !Settings.EpisodeNotices) return;
        _episodesLooking = true;
        var client = session.Client;
        var account = client.Connection.Server + " " + client.Connection.UserId;
        try
        {
            // Read down to the mark: what came since, however much.
            var since = Settings.EpisodesSeenFor == account ? Settings.EpisodesSeenUntil : null;
            var (episodes, series, whole) = await client.RecentEpisodesAsync(since, session.Lifetime);
            if (!ReferenceEquals(session, Session) || _closing || InParty || !Settings.EpisodeNotices) return;
            var seen = Settings.EpisodesSeenFor == account ? Settings.EpisodesSeenUntil : null;
            if (Settings.EpisodesSeenFor == account && seen is { } mark)
            {
                var (notices, more) = NewEpisodes.Pick(episodes, series, mark, Settings.EpisodesKnown);
                foreach (var notice in notices) await NotifyAsync(session, notice);
                if (more is not null) MacNotifications.Show(L("Nouveaux épisodes", "New episodes"), more, null, BringForward);
            }
            // A library without an episode to watch yet: the next one is news.
            var newest = NewEpisodes.Newest(episodes, seen) ?? DateTimeOffset.MinValue;
            // And what was there: an episode dated before the mark, but not among them, came since.
            var known = whole ? NewEpisodes.Known(episodes, newest) : null;
            if (Settings.EpisodesSeenFor != account || newest != seen || !NewEpisodes.Same(known, Settings.EpisodesKnown))
            {
                Settings.EpisodesSeenFor = account; Settings.EpisodesSeenUntil = newest; Settings.EpisodesKnown = known; Profile.SaveSettings(Settings);
            }
            ForgetOldNoticeImages();
        }
        // A server that does not answer now is asked again at the next change or on the clock.
        catch (Exception ex) when (Errors.Expected(ex) || ex is OperationCanceledException) { }
        finally { _episodesLooking = false; }
    }
    private async Task NotifyAsync(Session session, NewEpisodes.Notice notice)
    {
        string? image = null;
        // « Cacher les spoilers »: the series' art, never the new episode's still (NewEpisodes.Picture).
        if (NewEpisodes.Picture(notice.Episode) is { } picture)
        {
            try
            {
                var bytes = await session.Client.ImageAsync(picture.ItemId, picture.Type, 728, session.Lifetime);
                Directory.CreateDirectory(NoticeImages);
                var path = Path.Combine(NoticeImages, notice.Episode.Id + ".jpg");
                await File.WriteAllBytesAsync(path, bytes);
                image = path;
            }
            catch (Exception ex) when (Errors.Expected(ex) || ex is OperationCanceledException or IOException or UnauthorizedAccessException) { }
        }
        MacNotifications.Show(notice.Title, notice.Text, image, () => _ = OpenNoticeAsync(session, notice.SeriesId));
    }
    /// <summary>A notice clicked: Mira comes forward on the series, unless something plays.</summary>
    private async Task OpenNoticeAsync(Session session, string seriesId)
    {
        if (_closing) return;
        BringForward();
        if (Watching || !ReferenceEquals(session, Session) || InParty) return;
        try { var series = await session.ItemAsync(seriesId); if (ReferenceEquals(session, Session) && !Watching) Open(series); }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
    }
    /// <summary>Back from the Dock, minimized or closed with ⌘W (macOS brings the app forward, not a window put away).</summary>
    private void BringForward() => Reopen();
    /// <summary>macOS moves a notice's picture into its own store; what it did not take goes after four days.</summary>
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

    /// <summary>
    /// Someone came or went: said by the player's pill while a title plays, by a notice otherwise. Mira in the Dock or
    /// behind another window, a notification of the Mac says who came; clicked, Mira comes back where it was. Kept an
    /// hour in the notification centre, as on Windows.
    /// </summary>
    private void GroupMember(string said, bool joined)
    {
        if (joined && Group.Group is { } group && (WindowState == WindowState.Minimized || !IsActive))
            MacNotifications.Show(group.GroupName, said + ".", null, BringForward, "together", TimeSpan.FromHours(1));
        if (Player is { } player) player.ShowMember(said); else Notice(said + ".");
    }
}
