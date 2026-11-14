using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// The menu of the notification area (« Carte ») in the public gallery: opened as a right click on an icon of a taskbar
    /// at the bottom of the main screen would open it, in its three states (an episode playing, a film paused, nothing
    /// playing with Mira put away), each inside the work area above the taskbar, every button named and ringed under the
    /// keyboard, its buttons sending their commands. Captured over a dark desktop (26-tray-*). The fictional catalogue only.
    /// </summary>
    private async Task<string> CheckTrayMenuAsync(string output)
    {
        void Fail(string what) => throw new InvalidOperationException("Notification area menu: " + what);
        var items = _demoItems ?? DemoLibrary.Items();
        var film = items.First(x => x.Type == "Movie");
        var episode = new MediaItem
        {
            Id = "demo-episode", Type = "Episode", Name = L("Le signal", "The Signal"), SeriesName = items.First(x => x.Type == "Series").Name,
            ParentIndexNumber = 1, IndexNumber = 3, RunTimeTicks = TimeSpan.FromMinutes(40).Ticks
        };
        var chosen = new List<DesktopCommand>();
        var menu = new TrayMenuWindow(chosen.Add);
        var drawn = new List<string>();
        try
        {
            var screen = Screens.All().FirstOrDefault() ?? throw new InvalidOperationException("Notification area menu: no screen.");
            var work = screen.WorkArea;
            // On the taskbar, under the work area, near its right end: where the notification area is.
            var point = (X: work.Right - 150 * screen.Scale, Y: work.Bottom + 12 * screen.Scale);
            var states = new (string Name, TrayCard? Card, bool Shown, int Buttons)[]
            {
                ("26-tray-episode", new(episode.DisplayTitle, TrayMenuPlan.Line(episode, paused: false), 822, 2412, true, false, true, DemoLibrary.Artwork(2, landscape: true)), true, 8),
                ("26b-tray-film-paused", new(film.DisplayTitle, TrayMenuPlan.Line(film, paused: true), 2890, 7020, true, true, false, DemoLibrary.Artwork(0, landscape: true)), true, 7),
                ("26c-tray-idle", null, false, 2)
            };
            foreach (var (name, card, shown, count) in states)
            {
                menu.Open(point, screen, card, shown);
                await Task.Delay(300);
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var box = menu.Shown;
                if (box.Width <= 0 || box.Left < work.Left || box.Top < work.Top || box.Right > work.Right || box.Bottom > work.Bottom)
                    Fail($"{name} at {box} leaves the work area {work}.");
                if (work.Bottom - box.Bottom > 24 * screen.Scale) Fail($"{name} at {box} is not just above the taskbar ({work.Bottom}).");
                var root = (FrameworkElement)menu.Content;
                var buttons = Descendants<Button>(root).Where(x => x.IsVisible).ToList();
                if (buttons.Count != count) Fail($"{name} shows {buttons.Count} buttons, not {count}: " + string.Join(", ", buttons.Select(AutomationProperties.GetName)));
                var unnamed = Accessible.Unnamed(root);
                if (unnamed.Count > 0) Fail($"{name}: controls without a name: " + string.Join(", ", unnamed.Take(6).Select(Accessible.Describe)));
                var unringed = Accessible.WithoutFocusRing(root, (Style)FindResource("KeyboardFocus"));
                if (unringed.Count > 0) Fail($"{name}: controls without a focus ring: " + string.Join(", ", unringed.Take(6).Select(Accessible.Describe)));
                CaptureTrayMenu(root, Path.Combine(output, name + ".png"));
                drawn.Add($"{name}: {box.Width:0} × {box.Height:0} px at {box.Left:0},{box.Top:0} over a work area ending at {work.Bottom:0} (scale {screen.Scale:0.##}), {buttons.Count} buttons");
            }
            // A playback button keeps the menu open and sends its command; a line of the app closes it first.
            menu.Open(point, screen, states[0].Card, true);
            Press(menu, L("Mettre en pause", "Pause"));
            Press(menu, L("Ouvrir Mira", "Open Mira"));
            if (!chosen.SequenceEqual([DesktopCommand.TogglePause, DesktopCommand.Show])) Fail("commands sent: " + string.Join(", ", chosen));
            if (menu.IsOpen) Fail("« Open Mira » leaves the menu open.");
        }
        finally { menu.Destroy(); }
        return string.Join("; ", drawn);

        static void Press(TrayMenuWindow menu, string name)
        {
            var button = Descendants<Button>((DependencyObject)menu.Content).FirstOrDefault(x => x.IsVisible && AutomationProperties.GetName(x) == name)
                ?? throw new InvalidOperationException($"Notification area menu: no button « {name} ».");
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
    }

    /// <summary>The menu and its shadow at ×2 over a desktop's dark wallpaper (the capture's own backdrop, not Mira's).</summary>
    private static void CaptureTrayMenu(FrameworkElement root, string path)
    {
        const double scale = 2;
        var (width, height) = (root.ActualWidth + root.Margin.Left + root.Margin.Right, root.ActualHeight + root.Margin.Top + root.Margin.Bottom);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var desk = new DrawingVisual();
        using (var context = desk.RenderOpen())
            context.DrawRectangle(new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#27324A"), (Color)ColorConverter.ConvertFromString("#0A0C11"), 90), null, new Rect(0, 0, width, height));
        bitmap.Render(desk); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
