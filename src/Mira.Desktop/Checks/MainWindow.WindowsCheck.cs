using System.Diagnostics;
using System.IO;
using System.Windows;
using Mira.Desktop.Services;
using Windows.Media.Control;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>Real Windows media broker + real mpv, scoped to an isolated synthetic-media profile.</summary>
    private async Task RunWindowsCheckAsync()
    {
        var output = Path.GetFullPath(_args[Array.IndexOf(_args, "--windows-check") + 1]); Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add("PASS " + label); }
        async Task Until(Func<bool> predicate)
        { var timer = Stopwatch.StartNew(); while (!predicate()) { if (timer.Elapsed.TotalSeconds > 10) throw new TimeoutException("Windows/player state did not arrive."); await Task.Delay(60); } }
        try
        {
            if (!_demo || _testMedia is null || !_args.Contains("--data")) throw new InvalidOperationException("Windows checks require --demo, --test-media and an isolated --data profile.");
            Require(_windows is { MediaAvailable: true, TrayVisible: true }, "Windows media broker and notification-area icon initialize");
            Require(TaskbarItemInfo.ThumbButtonInfos.Count == 4 && TaskbarItemInfo.ThumbButtonInfos.All(x => x.Visibility == Visibility.Collapsed), "taskbar buttons remain absent outside playback");
            Require(File.Exists(WindowsIdentity.IconPath) && Icon is not null, "Windows logo is present both as a multi-size ICO and a window resource");
            _settings.Volume = 0; _settings.AutoNext = false; VolumeSlider.Value = 0;
            await PlayAsync(DemoLibrary.Items()[0]); await Until(() => _loaded); _mpv!.Set("loop-file", "inf");
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            await Until(() => manager.GetSessions().Any(x => x.SourceAppUserModelId == App.ShellAppId));
            var session = manager.GetSessions().First(x => x.SourceAppUserModelId == App.ShellAppId);
            Require(TaskbarItemInfo.ThumbButtonInfos.Take(3).All(x => x.IsEnabled) && !TaskbarItemInfo.ThumbButtonInfos[3].IsEnabled, "taskbar enables seek and pause but disables next when no following episode exists");
            Require(await session.TryPauseAsync(), "Windows accepts pause"); await Until(() => _mpv.Flag("pause"));
            await session.TryPauseAsync(); await Task.Delay(200);
            Require(_mpv.Flag("pause"), "repeated Windows pause is idempotent");
            var metadata = await session.TryGetMediaPropertiesAsync();
            Require(metadata.Title == _playingItem!.DisplayTitle && metadata.Subtitle == _playingItem.Subtitle, "Windows receives the title and episode metadata");
            for (var i = 0; i < 30 && metadata.Thumbnail is null; i++) { await Task.Delay(100); metadata = await session.TryGetMediaPropertiesAsync(); }
            Require(metadata.Thumbnail is not null, "Windows receives artwork through a private in-memory stream");
            using (var thumbnail = await metadata.Thumbnail!.OpenReadAsync()) Require(thumbnail.Size > 0, "Windows can read the published artwork");
            await Until(() => session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);
            Require(TaskbarItemInfo.ThumbButtonInfos[1].Description.Contains("Reprendre"), "taskbar pause label follows the system state");
            Require(await session.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(6).Ticks), "Windows accepts a timeline seek");
            await Until(() => Math.Abs(_position - 6) < .6);
            await Until(() => Math.Abs(session.GetTimelineProperties().Position.TotalSeconds - 6) < .7);
            Require(Math.Abs(_mpv.Number("time-pos") - 6) < .6, "Windows seek reaches mpv and returns the corrected timeline");
            await session.TryPlayAsync(); await Until(() => !_mpv.Flag("pause")); await session.TryPlayAsync(); await Task.Delay(180);
            Require(!_mpv.Flag("pause"), "repeated Windows play is idempotent");
            WindowsCommand(DesktopCommand.Hide, 0); await Task.Delay(250);
            Require(!IsVisible && _playing && _windows!.TrayVisible, "notification-area reduction keeps playback running");
            await session.TryPauseAsync(); await Until(() => _mpv.Flag("pause"));
            Require(!IsVisible, "media keys can pause the hidden window without stealing focus");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--data"); start.ArgumentList.Add(_profile.DirectoryPath);
            using (var second = Process.Start(start)!) await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            await Until(() => IsVisible && WindowState != WindowState.Minimized);
            Require(_playing && _mpv.Flag("pause"), "a second launch restores the same window without resetting playback");
            WindowsCommand(DesktopCommand.Back, 0); await Until(() => _mpv.Number("time-pos") < .5);
            WindowsCommand(DesktopCommand.Forward, 0); await Until(() => Math.Abs(_mpv.Number("time-pos") - 10) < .6);
            Require(Math.Abs(_mpv.Number("time-pos") - 10) < .6, "shared taskbar/tray commands seek by ten seconds and clamp at zero");
            var next = DemoLibrary.Items()[1]; _nextItem = next; UpdateWindowsPlayback();
            await Until(() => session.GetPlaybackInfo().Controls.IsNextEnabled);
            Require(TaskbarItemInfo.ThumbButtonInfos[3].IsEnabled, "next becomes available only when the episode has been resolved");
            Require(await session.TrySkipNextAsync(), "Windows accepts next episode");
            await Until(() => _playingItem?.Id == next.Id && _loaded);
            await session.TryPauseAsync(); await Until(() => _mpv!.Flag("pause"));
            Require(_playingItem.Id == next.Id && Title.Contains(next.DisplayTitle), "next uses Mira playback and updates the window identity");
            await session.TryStopAsync(); await Until(() => !_playing && _mpv is null);
            Require(TaskbarItemInfo.ThumbButtonInfos.All(x => x.Visibility == Visibility.Collapsed), "stopping removes all taskbar playback buttons");
            await Until(() => !manager.GetSessions().Any(x => x.SourceAppUserModelId == App.ShellAppId) || session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed);
            Require(true, "stopping withdraws the active Windows media session");
            Require(_windows!.MediaError is null, "Windows operations complete without COM errors");
            _windows.Dispose();
            Require(!_windows.TrayVisible && TaskbarItemInfo is null, "closing cleans up the tray and taskbar integration");
            _windows = null;
            await File.WriteAllLinesAsync(Path.Combine(output, "result.txt"), checks);
        }
        catch (Exception ex)
        {
            checks.Add("FAIL " + ex.GetType().Name + ": " + ex.Message);
            await File.WriteAllLinesAsync(Path.Combine(output, "result.txt"), checks);
        }
        finally { Close(); }
    }
}
