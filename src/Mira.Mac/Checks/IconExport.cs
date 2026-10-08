using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Mira.Mac.Views;

namespace Mira.Mac.Checks;

/// <summary>
/// Mira --export-icon &lt;file.png&gt;: the app icon at 1024 px, the Mira mark on Apple's icon grid (an 824 px tile
/// centred on the canvas), from which the release build makes Mira.icns.
/// </summary>
public static class IconExport
{
    public static void Run(IClassicDesktopStyleApplicationLifetime desktop, string file)
    {
        var mark = new Icon("brand", 824) { Foreground = new SolidColorBrush(Color.Parse("#F5F5F7")) };
        var canvas = new Grid { Width = 1024, Height = 1024, Background = Brushes.Transparent, Children = { mark } };
        canvas.Measure(new Size(1024, 1024)); canvas.Arrange(new Rect(0, 0, 1024, 1024));
        using var bitmap = new RenderTargetBitmap(new PixelSize(1024, 1024));
        bitmap.Render(canvas);
        bitmap.Save(file);
        desktop.Shutdown(0);
    }
}
