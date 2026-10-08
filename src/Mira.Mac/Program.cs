using Avalonia;

namespace Mira.Mac;

public static class Program
{
    [STAThread]
    public static int Main(string[] args) { System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener()); return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        // mpv draws the video with OpenGL into the window's own surface; software rendering stays as the fallback.
        .With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software] })
        // Linux is only where Mira's checks run (virtual display, software OpenGL that Avalonia refuses by default).
        .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Glx, X11RenderingMode.Software], GlxRendererBlacklist = [] })
        .LogToTrace(Avalonia.Logging.LogEventLevel.Warning);
}
