using System.Windows;
using System.Windows.Controls;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Desktop.Services;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// « Coller un lien magnet » (maquette B · Écrin) in the public gallery: the window under the Downloads page's button,
    /// in each of its states (link being read, a season with what the library has, its episodes chosen, 500 episodes in
    /// slices, a film, nobody sharing), at the default size and in a small window; it stays inside Mira's window, its
    /// header and its Download always seen. Fictional links and files only; nothing is read nor asked (28-magnet-*).
    /// </summary>
    private async Task<List<string>> CheckLinkWindowAsync(string output, IReadOnlyList<(int Width, int Height, string Name)> sizes)
    {
        const long GiB = 1L << 30, MiB = 1L << 20;
        List<string> drawn = [];
        var series = (_demoItems ?? DemoLibrary.Items()).First(x => x.Type == "Series");
        var dotted = string.Join('.', series.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Replace("’", "").Replace("'", "");
        var shows = L("Séries", "Shows");
        string Magnet(char c, string name) => $"magnet:?xt=urn:btih:{new string(c, 40)}&dn={Uri.EscapeDataString(name)}";
        var season = $"{dotted}.S02.1080p.WEB-DL.x264-NOTAG";
        List<LinkFile> seasonFiles = [.. Enumerable.Range(1, 10).Select(n => new LinkFile(n - 1, $"{season}/{dotted}.S02E{n:00}.1080p.WEB-DL.x264-NOTAG.mkv", 14 * GiB / 10 + n * 37 * MiB)),
            new(10, $"{season}/Sample/{dotted}.S02.sample.mkv", 60 * MiB)];
        const string saga = "[NoTag] Le Long Voyage - 001-500 [1080p]";
        List<LinkFile> sagaFiles = [.. Enumerable.Range(1, 500).Select(n => new LinkFile(n - 1, $"{saga}/[NoTag] Le Long Voyage - {n:000} [1080p].mkv", 380 * MiB + n % 7 * 11 * MiB))];
        const string film = "Le.Dernier.Phare.2019.2160p.UHD.BluRay.x265.10bit.HDR-NOTAG";
        List<LinkFile> filmFiles = [new(0, $"{film}/{film}.mkv", 22 * GiB), new(1, $"{film}/{film}.fr.srt", 80_000), new(2, $"{film}/Sample/sample.mkv", 90 * MiB)];

        async Task Shot(string name, (int Width, int Height, string Name) size, Func<LinkWindow> open)
        {
            Width = size.Width; Height = size.Height;
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); UpdateLayout();
            var link = open();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); UpdateLayout();
            FitMissingMenu(link.Anchor, link.Body); NudgeMissingMenu(link.Popup);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); UpdateLayout();
            // Where Mira asks WPF to put it, from its button (MenuFit): WPF itself keeps a popup on the screen, and the gallery's
            // window may be off it.
            var root = (FrameworkElement)Content; var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(root);
            var anchor = link.Anchor.TransformToAncestor(root).Transform(new Point(0, 0));
            var offset = MissingMenuPlace(link.Anchor, new Size(link.Body.ActualWidth * dpi.DpiScaleX, link.Body.ActualHeight * dpi.DpiScaleY));
            var place = new Rect(anchor.X + offset.X / dpi.DpiScaleX, anchor.Y + offset.Y / dpi.DpiScaleY, link.Body.ActualWidth, link.Body.ActualHeight);
            await CaptureAsync(output, $"{name}-{size.Name}", popup: (link.Body, place.TopLeft));
            // Inside Mira's window, under its title bar: nothing of it to look for outside.
            if (place.Top < TitleHeight - 1 || place.Left < -1 || place.Right > root.ActualWidth + 1 || place.Bottom > root.ActualHeight + 1)
                throw new InvalidOperationException($"The link's window {name} at {size.Name} leaves Mira's window: {place} in {root.ActualWidth:0} × {root.ActualHeight:0}.");
            // The Download button seen whole, whatever the list's length.
            if (link.Go is { } go && go.IsVisible)
            {
                var button = go.TransformToAncestor(link.Body).TransformBounds(new Rect(go.RenderSize));
                if (button.Bottom > link.Body.ActualHeight + 1 || button.Top < 0) throw new InvalidOperationException($"The link's window {name} at {size.Name}: Download cut at {button}.");
            }
            drawn.Add($"{name}-{size.Name}: window {place.Width:0} × {place.Height:0} at {place.X:0},{place.Y:0}, list {(link.Scroll is { } s ? $"{s.ViewportHeight:0}/{s.ExtentHeight:0}" : "-")} px");
            CloseLinkWindow();
        }

        LinkWindow Season(bool choose)
        {
            var link = OpenLinkWindow(Magnet('1', season), sample: true)!;
            link.Name = season; link.Contents = MagnetLinks.Read(season, seasonFiles); link.Place = new LinkPlace(shows, series.Name, true);
            link.InLibrary = true; link.Item = series; _serverPosters[series.Id] = series;
            link.Owned = [(2, 1), (2, 2), (2, 3)]; link.Stage = "ready"; Propose(link);
            if (choose) PickLinkOption(link, link.Contents, LinkOption.Choose);
            DrawLinkWindow(link); return link;
        }
        foreach (var size in new[] { sizes[0], (Width: 1152, Height: 676, Name: "small") })
        {
            await Shot("28-magnet-reading", size, () => { var link = OpenLinkWindow(Magnet('1', season), sample: true)!; link.Stage = "reading"; DrawLinkWindow(link); return link; });
            await Shot("28b-magnet-season", size, () => Season(choose: false));
            await Shot("28c-magnet-choose", size, () => Season(choose: true));
            await Shot("28d-magnet-500", size, () =>
            {
                var link = OpenLinkWindow(Magnet('2', saga), sample: true)!;
                link.Name = saga; link.Contents = MagnetLinks.Read(saga, sagaFiles); link.Place = new LinkPlace(L("Animes", "Anime"), "Le Long Voyage", false);
                link.InLibrary = false; link.Stage = "ready"; Propose(link); PickLinkOption(link, link.Contents, LinkOption.Choose);
                DrawLinkWindow(link); return link;
            });
        }
        await Shot("28e-magnet-film", sizes[0], () =>
        {
            var link = OpenLinkWindow(Magnet('3', film), sample: true)!;
            link.Name = film; link.Contents = MagnetLinks.Read(film, filmFiles); link.Place = new LinkPlace(L("Films", "Movies"), "Le Dernier Phare (2019)", false);
            link.InLibrary = false; link.Stage = "ready"; Propose(link); DrawLinkWindow(link); return link;
        });
        await Shot("28f-magnet-silent", sizes[0], () => { var link = OpenLinkWindow(Magnet('1', season), sample: true)!; link.Stage = "silent"; DrawLinkWindow(link); return link; });
        return drawn;
    }
}
