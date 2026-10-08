using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Mira.Core;
using Mira.Mac.Playback;

namespace Mira.Mac.Checks;

/// <summary>
/// Mira --video-probe &lt;video&gt; [--software]: one window, one video, then the share of colourful pixels on screen after
/// four seconds. A quick, repeatable look at the renderer alone, without Jellyfin or the rest of the interface.
/// </summary>
public static class VideoProbe
{
    public static void Start(IClassicDesktopStyleApplicationLifetime desktop, string media, bool software)
    {
        IVideoSurface surface = software ? new SoftwareVideoView() : new VideoView();
        var window = new Window { Title = "Mira", Width = 960, Height = 540, Content = surface.View, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        desktop.MainWindow = window;
        window.Opened += async (_, _) => await RunAsync(desktop, window, surface, media);
    }
    private static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, Window window, IVideoSurface surface, string media)
    {
        await Task.Delay(500);
        var player = new MpvPlayer(new PlayerSettings { Volume = 100 }, silent: true);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool ready;
        try { ready = await surface.AttachAsync(player).WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (TimeoutException) { ready = false; }
        Console.WriteLine($"probe attach {ready} after {clock.ElapsedMilliseconds} ms");
        player.Load(media, 0);
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => player.Poll();
        timer.Start();
        await Task.Delay(4000);
        var file = Path.Combine(Path.GetTempPath(), $"mira-probe-{Environment.ProcessId}.png");
        var colour = SelfCheck.ScreenColour(file);
        Console.WriteLine($"probe ready={ready} renderer={(surface as VideoView)?.Renderer} problem={(surface as VideoView)?.Problem} frames={surface.Frames} colour={colour:P0} pos={player.Get("time-pos")} drop={player.Get("frame-drop-count")} delayed={player.Get("vo-delayed-frame-count")} vparams={player.Get("video-params/w")}x{player.Get("video-params/h")} osd={player.Get("osd-width")}x{player.Get("osd-height")} vo={player.Get("current-vo")}");
        timer.Stop();
        window.Content = null; surface.Release(); player.Dispose();
        desktop.Shutdown(colour > 0.2 ? 0 : 1);
    }
}
