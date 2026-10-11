using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// Player fixture additions for 0.5.2: the bottom bar (title, chapter and time, chapter segments, button rows),
// the volume slider beside the speaker and the menus opened above their buttons.
public partial class MainWindow
{
    private async Task RunPlayerBarChecksAsync(string output, List<string> checks, Action<bool, string> require)
    {
        var mpv = _mpv!;
        // Keeps the controls on screen for the whole sequence: idling would fade them out between steps.
        void Hold() { ShowPlayerControls(); _lastPlayerInteraction = DateTimeOffset.UtcNow.AddSeconds(30); }
        Point At(FrameworkElement element, double x = 0, double y = 0) => element.TranslatePoint(new Point(x, y), PlayerSurface);
        Hold(); UpdateLayout(); await Task.Delay(260);

        var titleTop = At(PlayingTitle).Y; var barTop = At(SeekBar).Y; var rowTop = At(PauseButton).Y;
        require(PlayingTitle.Text.Length > 0 && PlayingSubtitle.Text.Length > 0 && titleTop < barTop && barTop < rowTop && At(PositionText).Y < barTop,
            "the title, its second line and the time sit above the timeline, the buttons below it");
        var textLeft = At(PlayingTitle).X; var timeRight = At(DurationText, DurationText.ActualWidth).X;
        var playGlyph = FindVisual<Icon>(PauseButton)!; var lastGlyph = FindVisual<Icon>(FullscreenButton)!;
        require(Math.Abs(At(playGlyph).X - textLeft) < 2 && Math.Abs(At(lastGlyph, lastGlyph.ActualWidth).X - timeRight) < 2,
            $"the first and last buttons line up with the title and the time ({At(playGlyph).X - textLeft:0.0} / {At(lastGlyph, lastGlyph.ActualWidth).X - timeRight:0.0} px)");
        require(Regex.IsMatch(PositionText.Text, @"^\d+:\d{2}$") && DurationText.Text == TimeLabel(_duration), $"the time reads « {PositionText.Text} / {DurationText.Text} »");

        if (SeekBar.Chapters.Count > 0)
        {
            var inner = SeekBar.Chapters.Count(c => c.Start > .5 && c.Start < _duration - .5);
            var segments = SeekBar.Segments();
            require(segments.Count == inner + 1 && segments.Zip(segments.Skip(1)).All(p => Math.Abs(p.Second.From - p.First.To - Timeline.Gap) < .01),
                $"chapters split the timeline into {segments.Count} segments separated by {Timeline.Gap} px");
            foreach (var chapter in SeekBar.Chapters.Where(c => !string.IsNullOrWhiteSpace(c.Title)).Take(3))
            {
                var target = Math.Min(chapter.Start + 1, _duration - 1);
                TryCommand("seek", target.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute+exact"); HoldPosition(target);
                await Task.Delay(380); Hold();
                require(ChapterText.IsVisible && ChapterText.Text == chapter.Title!.Trim(), $"the chapter being played is named above the time: « {ChapterText.Text} » at {target:0} s");
            }
            // The capture shows the bar in the second chapter, as in the reference.
            var second = SeekBar.Chapters.Where(c => c.Start > .5).Select(c => c.Start).DefaultIfEmpty(0).First();
            TryCommand("seek", (second + 2).ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute+exact"); HoldPosition(second + 2);
            await Task.Delay(380); Hold();
        }
        else checks.Add("INFO test clip has no chapters: one segment, no chapter name");
        mpv.Set("pause", "yes"); await Task.Delay(150); mpv.Poll(); Hold(); await Task.Delay(120);
        CaptureOverVideo(output, "player-bar", PlayerSurface);

        VolumeGroup_MouseEnter(this, null!); await Task.Delay(320);
        require(Math.Abs(VolumeReveal.ActualWidth - VolumeRevealWidth) < .5 && VolumeSlider.IsVisible, "pointing at the speaker slides the volume slider out");
        CaptureOverVideo(output, "player-volume", PlayerSurface);
        VolumeGroup_MouseLeave(this, null!); await Task.Delay(1300); Hold();
        require(VolumeReveal.ActualWidth < .5, "the volume slider tucks back once the pointer has left");
        ChangeVolume(5); await Task.Delay(320);
        require(VolumeReveal.ActualWidth > VolumeRevealWidth - .5, "a keyboard volume change shows the level for a moment");
        ChangeVolume(-5); await Task.Delay(1300); Hold();

        // A subtitle file gives the menu a real track to switch from and to.
        var subtitles = Path.Combine(output, "sous-titres.srt");
        File.WriteAllText(subtitles, "1\n00:00:00,000 --> 00:01:00,000\nSous-titre de test\n");
        TryCommand("sub-add", subtitles, "select", "Essai", "fre"); await Task.Delay(250);
        Tracks_Click(this, new()); UpdateLayout(); await Task.Delay(180);
        var rows = SubtitleTrackList.Children.OfType<RadioButton>().ToList();
        require(PlayerOptionsPopup.IsOpen && TracksMenu.IsVisible && !MoreMenu.IsVisible && rows.Count == 2 && rows.Count(IsChosen) == 1 && rows[1].IsChecked == true
            && System.Windows.Automation.AutomationProperties.GetName(rows[1]).Contains("Français"),
            $"the subtitles button opens audio and subtitles, the loaded track checked (« {System.Windows.Automation.AutomationProperties.GetName(rows[1])} »)");
        var dpi = VisualTreeHelper.GetDpi(PlayerShell);
        var panel = (FrameworkElement)PlayerOptionsPopup.Child;
        var panelPixels = new Size(panel.ActualWidth * dpi.DpiScaleX, panel.ActualHeight * dpi.DpiScaleY);
        var shellPixels = new Size(PlayerShell.ActualWidth * dpi.DpiScaleX, PlayerShell.ActualHeight * dpi.DpiScaleY);
        var place = MenuPlacement(panelPixels, shellPixels);
        var anchorRight = At(PlayerTracksButton, PlayerTracksButton.ActualWidth).X * dpi.DpiScaleX; var anchorTop = At(PlayerTracksButton).Y * dpi.DpiScaleY;
        require(Math.Abs(place.X + panelPixels.Width - anchorRight) < 1 && Math.Abs(place.Y + panelPixels.Height - anchorTop) < 1 && place.X >= 0,
            "the menu opens right above its button, aligned on its right edge, at the current DPI");
        CaptureElement(output, "player-menu-tracks", panel);
        rows[0].IsChecked = true; await Task.Delay(150);
        require((mpv.Get("sid") ?? "no") is "no" or "false" && IsChosen(rows[0]) && !IsChosen(rows[1]) && PlayerOptionsPopup.IsOpen, "choosing « Désactivés » reaches mpv, unchecks the track and keeps the menu open");
        rows[1].IsChecked = true; await Task.Delay(150);
        require((mpv.Get("sid") ?? "no") is not ("no" or "false") && IsChosen(rows[1]), "choosing the track again turns the subtitles back on");
        Tracks_Click(this, new());
        require(!PlayerOptionsPopup.IsOpen, "the same button closes its menu");

        More_Click(this, new()); UpdateLayout(); await Task.Delay(180);
        var chips = SpeedList.Children.OfType<RadioButton>().ToList();
        require(PlayerOptionsPopup.IsOpen && MoreMenu.IsVisible && !TracksMenu.IsVisible && chips.Count == Speeds.Length && chips.Count(IsChosen) == 1 && PlaybackInfo.Text.Contains("Vidéo"),
            "⋮ opens the speeds, the subtitle timing and the playback information");
        var faster = chips.First(c => c.Tag is 1.5);
        faster.IsChecked = true; await Task.Delay(100);
        require(Math.Abs(mpv.Number("speed") - 1.5) < .001 && IsChosen(faster) && chips.Count(IsChosen) == 1, "a speed chip reaches mpv and becomes the chosen one");
        CaptureElement(output, "player-menu-more", panel);
        ChooseSpeed(1); PlayerOptionsPopup.IsOpen = false;
    }

    /// <summary>The overlay over the current video frame: mpv writes the frame, WPF draws the controls on it.</summary>
    private void CaptureOverVideo(string directory, string name, FrameworkElement element)
    {
        var frame = Path.Combine(directory, name + "-frame.png");
        try { _mpv?.Command("screenshot-to-file", frame, "video"); } catch (IOException) { }
        for (var i = 0; i < 20 && !File.Exists(frame); i++) Thread.Sleep(50);
        BitmapSource? background = null;
        if (File.Exists(frame))
        {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(frame); image.EndInit(); image.Freeze();
            background = image; File.Delete(frame);
        }
        CaptureElement(directory, name, element, background);
    }
}
