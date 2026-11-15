using System.IO;
using System.Windows;
using System.Windows.Media;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// The presentation video's films (--tour &lt;folder&gt;, never merged): the window over the whole screen, each scene played
    /// by the pointer and the keyboard while ffmpeg films it (<see cref="TourDirector"/>).
    /// </summary>
    private async Task RunTourAsync()
    {
        var output = Path.GetFullPath(_args[Array.IndexOf(_args, "--tour") + 1]); Directory.CreateDirectory(output);
        var result = Path.Combine(output, "tour-result.txt");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(output, "dilation.txt"), TimeDilation.Report);
            if (!TimeDilation.Loaded) throw new InvalidOperationException("No time dilation: MIRA_DILATE_DLL?");
            Motion.FollowsWindows = false; Motion.Reduced = false;
            _fallbackRefresh.Stop();
            WhatsNewOverlay.Visibility = WelcomeOverlay.Visibility = GuideOverlay.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Normal; Left = 0; Top = 0;
            Width = SystemParameters.PrimaryScreenWidth; Height = SystemParameters.PrimaryScreenHeight;
            Topmost = true; Activate();
            await Task.Delay(1500);
            await ArtworkAsync();
            var tour = new TourDirector(output);
            await TourExperimentAsync(tour);
            await File.WriteAllTextAsync(result, "PASS");
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(result, $"FAIL: {ex}");
            Environment.ExitCode = 1;
        }
        finally { TimeDilation.Set(1); Close(); }
    }

    /// <summary>Every card of the page on the screen, top to bottom, left to right.</summary>
    private List<MediaCard> CardsOnScreen()
    {
        var cards = new List<MediaCard>();
        void Walk(DependencyObject node)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is MediaCard card) { if (card.IsVisible && card.ActualWidth > 0) cards.Add(card); } else Walk(child);
            }
        }
        Walk(this);
        var screen = new Rect(0, 0, ActualWidth, ActualHeight);
        return [.. cards.Select(x => (Card: x, At: x.TranslatePoint(new Point(), this)))
            .Where(x => screen.Contains(new Rect(x.At, x.Card.RenderSize)))
            .OrderBy(x => Math.Round(x.At.Y / 40)).ThenBy(x => x.At.X).Select(x => x.Card)];
    }

    /// <summary>The first run: is the slow clock filmed smoothly, and does the screen's capture see mpv's picture?</summary>
    private async Task TourExperimentAsync(TourDirector tour)
    {
        foreach (var factor in new[] { 6d, 1d })
        {
            await tour.BeginAsync($"home-x{factor}", factor);
            tour.Place(ActualWidth * .7, ActualHeight * .3);
            var cards = CardsOnScreen();
            foreach (var card in cards.Take(4)) { await tour.MoveAsync(card, .5, .45, 520); await TourDirector.Wait(450); }
            await tour.WheelAsync(-4, 140); await TourDirector.Wait(900);
            await tour.WheelAsync(4, 140); await TourDirector.Wait(700);
            await tour.EndAsync();
        }

        await tour.BeginAsync("movies");
        tour.Place(ActualWidth * .5, ActualHeight * .5);
        await tour.MoveAsync(MoviesNav, .5, .5, 700); await TourDirector.Wait(200); await tour.ClickAsync(); await TourDirector.Wait(1200);
        await ArtworkAsync();
        var poster = CardsOnScreen().Skip(2).FirstOrDefault();
        if (poster is not null) { await tour.MoveAsync(poster, .5, .4, 700); await TourDirector.Wait(500); await tour.ClickAsync(); await TourDirector.Wait(1800); }
        await tour.WheelAsync(-3, 160); await TourDirector.Wait(900);
        await tour.KeyAsync(0x1B); await TourDirector.Wait(900);
        await tour.EndAsync();

        await tour.BeginAsync("settings");
        tour.Place(ActualWidth * .4, ActualHeight * .6);
        await tour.MoveAsync(SettingsNav, .5, .5, 800); await TourDirector.Wait(200); await tour.ClickAsync(); await TourDirector.Wait(1200);
        await tour.KeyAsync(0x1B); await TourDirector.Wait(800);
        await tour.EndAsync();

        if (_testMedia is null) return;
        _settings.Volume = 0; _settings.AutoNext = false;
        foreach (var grab in new[] { "gdigrab", "ddagrab" })
        {
            await PlayAsync(DemoLibrary.Items()[0]);
            for (var i = 0; i < 300 && (!_loaded || _videoWaits); i++) await Task.Delay(100);
            await tour.BeginAsync($"player-{grab}", 6, grab);
            tour.Place(ActualWidth * .5, ActualHeight * .5);
            await tour.MoveToAsync(ActualWidth * .55, ActualHeight * .6, 500); await TourDirector.Wait(600);
            await tour.MoveAsync(PlayerTracksButton, .5, .5, 700); await TourDirector.Wait(200); await tour.ClickAsync(); await TourDirector.Wait(1500);
            await tour.KeyAsync(0x1B); await TourDirector.Wait(1200);
            await tour.EndAsync();
            await StopPlaybackAsync(); await Task.Delay(1500);
        }
    }
}
