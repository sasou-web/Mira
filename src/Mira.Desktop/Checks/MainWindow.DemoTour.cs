using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    private readonly List<object> _tourSteps = [];
    /// <summary>
    /// The presentation video's tour (--demo-tour &lt;folder&gt;): the real window, maximized on screen, used with the real
    /// mouse and keyboard (SendInput) while the workflow films the screen; each step's time (Unix ms) in tour.json, to
    /// cut the film. Signed in as --readme-gallery (MIRA_GALLERY_SERVER, _USER, _PASSWORD).
    /// </summary>
    private async Task RunDemoTourAsync()
    {
        var output = Path.GetFullPath(_args[Array.IndexOf(_args, "--demo-tour") + 1]); Directory.CreateDirectory(output);
        try
        {
            var server = Environment.GetEnvironmentVariable("MIRA_GALLERY_SERVER"); var user = Environment.GetEnvironmentVariable("MIRA_GALLERY_USER");
            var password = Environment.GetEnvironmentVariable("MIRA_GALLERY_PASSWORD");
            if (_demo || string.IsNullOrEmpty(server) || string.IsNullOrEmpty(user) || password is null || !_args.Contains("--data"))
                throw new InvalidOperationException("The tour needs MIRA_GALLERY_SERVER, _USER and _PASSWORD, an isolated --data profile and no demo.");
            _fallbackRefresh.Stop(); _searchTimer.Stop();
            Connection connection;
            using (var client = new JellyfinClient(new(server, "", user, "", _profile.DeviceId))) connection = await client.LoginAsync(user, password);
            _profile.SaveConnection(connection);
            await ActivateConnectionAsync(connection);
            WhatsNewOverlay.Visibility = WelcomeOverlay.Visibility = GuideOverlay.Visibility = LoginOverlay.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Maximized; Activate(); await ArtworkAsync();
            await Pointer(new Point(1500, 900), 10);
            await Task.Delay(3000);

            Step("home");
            await Task.Delay(4500);
            await HoverAsync(HeroPlay, 900); await Task.Delay(1400);
            await Pointer(ScreenAt(LibraryScroll, 0.55, 0.75), 700);
            Step("rows");
            await WheelAsync(-3); await ArtworkAsync();
            foreach (var card in VisibleCards().Take(4)) await HoverAsync(card, 520, 380);
            await WheelAsync(-4); await Task.Delay(900);
            foreach (var card in VisibleCards().Skip(2).Take(3)) await HoverAsync(card, 520, 380);
            await WheelAsync(9, 220);

            Step("series");
            await ClickAsync(SeriesNav); await ArtworkAsync();
            var arcane = await CardAsync("Arcane");
            await HoverAsync(arcane, 700, 600); await ClickAsync(arcane); await DetailsAsync();
            Step("arcane");
            await Task.Delay(3200);
            await Pointer(ScreenAt(DetailScroll, 0.5, 0.8), 600);
            await WheelAsync(-5); await Task.Delay(1500);
            foreach (var episode in Descendants<Button>(EpisodesPanel).Where(OnScreen).Take(3)) await HoverAsync(episode, 500, 500);
            await WheelAsync(-4); await Task.Delay(1800);
            await KeyAsync(0x1B);

            Step("movies");
            await ClickAsync(MoviesNav); await ArtworkAsync(); await Task.Delay(800);
            foreach (var card in VisibleCards().Take(5)) await HoverAsync(card, 480, 320);
            await WheelAsync(-4); await ArtworkAsync();
            foreach (var card in VisibleCards().Skip(6).Take(3)) await HoverAsync(card, 480, 320);
            var verse = await CardAsync("Across the Spider-Verse");
            await HoverAsync(verse, 600, 500); await ClickAsync(verse); await DetailsAsync();
            Step("film");
            await Task.Delay(3500);
            await Pointer(ScreenAt(DetailScroll, 0.5, 0.8), 600); await WheelAsync(-4); await ArtworkAsync(); await Task.Delay(1800);
            await KeyAsync(0x1B); await Task.Delay(600);

            var spring = await CardAsync("Spring");
            await HoverAsync(spring, 600, 500); await ClickAsync(spring); await DetailsAsync();
            Step("spring");
            await Task.Delay(2600);
            await HoverAsync(DetailPlay, 700, 500); await ClickAsync(DetailPlay);
            Step("play");
            for (var i = 0; i < 240 && !_loaded; i++) await Task.Delay(250);
            if (!_loaded) throw new InvalidOperationException("The film did not play within 60 s.");
            Step("playing");
            await Task.Delay(1500);
            await Pointer(ScreenAt(SeekBar, 0.3, 0.5), 900); await Task.Delay(400);
            await Pointer(ScreenAt(SeekBar, 0.62, 0.5), 1400); await Task.Delay(500);
            await ClickAsync(); Step("seek");
            await Task.Delay(1500);
            await Pointer(new Point(SystemParameters.PrimaryScreenWidth * 0.5, SystemParameters.PrimaryScreenHeight * 0.45), 600);
            Step("picture");
            await Task.Delay(12000);
            await Pointer(new Point(SystemParameters.PrimaryScreenWidth * 0.52, SystemParameters.PrimaryScreenHeight * 0.5), 400);
            await Task.Delay(800);
            BackFromPlayer_Click(this, new()); Step("mini");
            await ArtworkAsync(); await Task.Delay(3500);

            Step("search");
            await ClickAsync(SearchNav); await Task.Delay(700);
            foreach (var c in "spider") { TypeText(c.ToString()); await Task.Delay(170); }
            await Task.Delay(1500); await ArtworkAsync();
            foreach (var card in VisibleCards().Take(3)) await HoverAsync(card, 520, 450);
            await Task.Delay(2500);
            Step("end");
            await File.WriteAllTextAsync(Path.Combine(output, "tour.json"), JsonSerializer.Serialize(_tourSteps));
            await File.WriteAllTextAsync(Path.Combine(output, "tour-result.txt"), $"PASS: the tour of {server} as {user}.");
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "tour.json"), JsonSerializer.Serialize(_tourSteps));
            await File.WriteAllTextAsync(Path.Combine(output, "tour-result.txt"), $"FAIL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            Environment.ExitCode = 1;
        }
        finally { Close(); }
    }
    private void Step(string name) => _tourSteps.Add(new { step = name, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    private async Task DetailsAsync() { await Task.Delay(500); await ArtworkAsync(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T found) yield return found;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
    private bool OnScreen(FrameworkElement element)
    {
        if (!element.IsVisible || element.ActualWidth < 1) return false;
        var center = ScreenAt(element, 0.5, 0.5);
        return center.X > 80 && center.Y > 60 && center.X < SystemParameters.PrimaryScreenWidth - 20 && center.Y < SystemParameters.PrimaryScreenHeight - 40;
    }
    private List<MediaCard> VisibleCards() => [.. Descendants<MediaCard>(this).Where(OnScreen).OrderBy(x => ScreenAt(x, 0.5, 0.5).Y).ThenBy(x => ScreenAt(x, 0.5, 0.5).X)];
    /// <summary>The card of a title, scrolled to by the wheel as a viewer would.</summary>
    private async Task<MediaCard> CardAsync(string name)
    {
        for (var i = 0; i < 30; i++)
        {
            var card = Descendants<MediaCard>(this).FirstOrDefault(x => x.IsVisible && x.Item.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (card is not null && OnScreen(card) && ScreenAt(card, 0.5, 1).Y < SystemParameters.PrimaryScreenHeight - 40) return card;
            await WheelAsync(card is not null && ScreenAt(card, 0.5, 0.5).Y < 100 ? 2 : -2);
            await Task.Delay(300);
        }
        throw new InvalidOperationException($"No card « {name} » on screen.");
    }
    private static Point ScreenAt(FrameworkElement element, double x, double y) => element.PointToScreen(new Point(element.ActualWidth * x, element.ActualHeight * y));
    private async Task HoverAsync(FrameworkElement element, int moveMs, int holdMs = 0) { await Pointer(ScreenAt(element, 0.5, 0.5), moveMs); await Task.Delay(holdMs); }
    private async Task ClickAsync(FrameworkElement? element = null)
    {
        if (element is not null) await Pointer(ScreenAt(element, 0.5, 0.5), 450);
        await Task.Delay(120); SendMouse(0x0002); await Task.Delay(90); SendMouse(0x0004);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background); await Task.Delay(500);
    }
    private async Task KeyAsync(ushort key) { Activate(); PressKey(key); await Task.Delay(700); }
    /// <summary>Wheel notches, negative = down, as a hand turns it.</summary>
    private async Task WheelAsync(int notches, int everyMs = 300)
    {
        for (var i = 0; i < Math.Abs(notches); i++) { SendMouse(0x0800, Math.Sign(notches) * 120); await Task.Delay(everyMs); }
        await Task.Delay(450);
    }
    /// <summary>The real cursor glides to a point (physical pixels), eased.</summary>
    private static async Task Pointer(Point target, int ms)
    {
        GetCursorPos(out var from); var steps = Math.Max(1, ms / 15);
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps; var e = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            SetCursorPos((int)Math.Round(from.X + (target.X - from.X) * e), (int)Math.Round(from.Y + (target.Y - from.Y) * e));
            await Task.Delay(15);
        }
    }
    private static void SendMouse(uint flags, int data = 0)
    {
        var input = new TourInput { Type = 0, Mouse = new() { Flags = flags, Data = data } };
        SendTourInput(1, [input], Marshal.SizeOf<TourInput>());
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public int Data; public uint Flags; public uint Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct TourInput { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public MouseInput Mouse; }
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)] private static extern uint SendTourInput(uint count, TourInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
}
