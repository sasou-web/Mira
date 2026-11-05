using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// A click on Favori or Vu, on a film's page, moves nothing: Lecture, every button of the row, the title, its facts
    /// and its summary keep their place and their size while the button changes and after it, and no notice comes over
    /// the row. Measured every few milliseconds from the click to well after it; the page drawn at three instants of
    /// each change and on and off (<c>24-title-*</c>), for review. The film picked up halfway is the case that moved
    /// most: « Vu » took away « Depuis le début » and the progress line, and Lecture changed its words.
    /// </summary>
    /// <returns>What was measured, one line per click.</returns>
    private async Task<List<string>> CheckTitleRowStillAsync(string output)
    {
        var film = _demoItems!.First(x => x.Type == "Movie" && x.Progress > 0 && !x.UserData.IsFavorite);
        var kept = (film.UserData.Played, film.UserData.IsFavorite, film.UserData.PlaybackPositionTicks);
        Width = 1440; Height = 960;
        await ShowDetailsAsync(film); await CaptureAsync(output, "24-title-row-off");
        var found = new List<string>(); var lines = new List<string>();
        Dictionary<string, Rect> Places()
        {
            var root = (FrameworkElement)Content; var places = new Dictionary<string, Rect>();
            void Add(string name, FrameworkElement element) { if (element.IsVisible && element.ActualWidth > 0) places[name] = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)); }
            Add("Lecture", DetailPlay); Add("« Depuis le début »", DetailRestart); Add("« Regarder ensemble »", TogetherButton); Add("Favori", FavoriteButton);
            Add("Vu", WatchedButton); Add("« Suivre »", FollowButton); Add("« Garder sur ce PC »", OfflineButton); Add("« … »", FilesButton);
            Add("the title", DetailTitle); Add("the facts", DetailMeta); Add("the summary", DetailOverview); Add("the progress line", DetailProgress);
            return places;
        }
        async Task Click(Button button, RoutedEventHandler handler, string name)
        {
            HideNotice(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout();
            var before = Places(); var moves = new Dictionary<string, string>(); var worst = 0d;
            void Compare(string when)
            {
                foreach (var (part, now) in Places())
                {
                    if (!before.TryGetValue(part, out var was)) { moves.TryAdd(part, $"{part} appeared {when}"); continue; }
                    var shift = new[] { now.Left - was.Left, now.Top - was.Top, now.Width - was.Width, now.Height - was.Height }.Max(Math.Abs);
                    if (shift <= .5) continue;
                    worst = Math.Max(worst, shift);
                    moves.TryAdd(part, $"{part} {was.Left:0},{was.Top:0} {was.Width:0}×{was.Height:0} → {now.Left:0},{now.Top:0} {now.Width:0}×{now.Height:0} {when}");
                }
            }
            handler(button, new RoutedEventArgs());
            var clock = Stopwatch.StartNew(); var shot = 0; int[] shots = [30, 130, 320];
            while (clock.ElapsedMilliseconds < 900)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                Compare($"{clock.ElapsedMilliseconds} ms after the click");
                if (shot < shots.Length && clock.ElapsedMilliseconds >= shots[shot]) Snap(output, $"24-title-click-{name}-{++shot}");
                await Task.Delay(12);
            }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout();
            Compare("once settled");
            if (Notice.Visibility == Visibility.Visible && NoticeText.Text.Length > 0) moves.TryAdd("notice", $"a notice came over the row: « {NoticeText.Text} »");
            lines.Add(moves.Count == 0 ? $"{name}: nothing moved" : $"{name}: {moves.Count} part(s) moved, up to {worst:0} px: {string.Join(" | ", moves.Values)}");
            found.AddRange(moves.Values.Select(x => $"{name}: {x}"));
        }
        await Click(FavoriteButton, Favorite_Click, "favorite-on");
        await Click(WatchedButton, Watched_Click, "watched-on");
        await CaptureAsync(output, "24-title-row-on");
        await Click(WatchedButton, Watched_Click, "watched-off");
        await Click(FavoriteButton, Favorite_Click, "favorite-off");
        foreach (var copy in _demoItems!.Concat(_catalog.Items).Concat(_catalog.Resume).Concat(_catalog.NextUp).Where(x => x.Id == film.Id))
            (copy.UserData.Played, copy.UserData.IsFavorite, copy.UserData.PlaybackPositionTicks) = kept;
        await File.WriteAllLinesAsync(Path.Combine(output, "title-row.txt"), lines);
        if (found.Count > 0) throw new InvalidOperationException("A click on Favori or Vu moved the title page: " + string.Join(" ; ", found.Take(8)));
        return lines;
    }

    /// <summary>The words and buttons over a title's picture as drawn at this instant, an animation midway included.</summary>
    private void Snap(string directory, string name)
    {
        var visual = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(visual);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen()) context.DrawRectangle(Background, null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
        bitmap.Render(backdrop); bitmap.Render(visual);
        var area = DetailHeroText.TransformToAncestor(visual).TransformBounds(new Rect(DetailHeroText.RenderSize));
        area.Inflate(24, 24); area.Intersect(new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
        var pixels = new Int32Rect((int)(area.X * dpi.DpiScaleX), (int)(area.Y * dpi.DpiScaleY), (int)(area.Width * dpi.DpiScaleX), (int)(area.Height * dpi.DpiScaleY));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(bitmap, pixels)));
        using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
}
