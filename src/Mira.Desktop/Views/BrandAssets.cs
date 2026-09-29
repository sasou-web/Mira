using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mira.Desktop.Views;

/// <summary>Exports the same vector mark used by the sidebar as Windows application assets.</summary>
internal static class BrandAssets
{
    public static void Export(string directory)
    {
        Directory.CreateDirectory(directory);
        var frames = new[] { 16, 24, 32, 48, 64, 128, 256 }.Select(size => (Size: size,
            Png: Render(new Icon { Kind = "brand", Width = size, Height = size, Foreground = Brushes.WhiteSmoke }, size, size))).ToArray();
        File.WriteAllBytes(Path.Combine(directory, "mira.png"), frames[^1].Png);
        using (var writer = new BinaryWriter(File.Create(Path.Combine(directory, "mira.ico"))))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)frames.Length);
            var offset = 6 + 16 * frames.Length;
            foreach (var frame in frames)
            {
                writer.Write((byte)(frame.Size == 256 ? 0 : frame.Size)); writer.Write((byte)(frame.Size == 256 ? 0 : frame.Size));
                writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(frame.Png.Length); writer.Write(offset); offset += frame.Png.Length;
            }
            foreach (var frame in frames) writer.Write(frame.Png);
        }
        var root = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(12, 12, 15)) };
        var brand = new StackPanel { Margin = new Thickness(40), Orientation = Orientation.Horizontal };
        brand.Children.Add(new Icon { Kind = "brand", Width = 80, Height = 80, Foreground = Brushes.WhiteSmoke });
        brand.Children.Add(new TextBlock { Text = "mira", FontFamily = (FontFamily)Application.Current.FindResource("AppFont"), FontSize = 48, FontWeight = FontWeights.ExtraBold, Foreground = Brushes.WhiteSmoke, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0) });
        root.Children.Add(brand);
        var grid = new UniformGrid { Columns = 6, Margin = new Thickness(20, 0, 20, 20) };
        foreach (var kind in new[] { "home", "search", "film", "series", "library", "settings", "heart", "bookmark", "play", "pause", "skip", "sliders", "volume", "mute", "mini", "fullscreen", "rewind", "forward", "check", "info", "refresh", "subtitles", "server", "user" })
        {
            var tile = new StackPanel { Margin = new Thickness(12) };
            tile.Children.Add(new Icon { Kind = kind, Foreground = Brushes.WhiteSmoke, Width = 28, Height = 28 });
            tile.Children.Add(new TextBlock { Text = kind, Foreground = Brushes.Gray, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0), FontSize = 11 });
            grid.Children.Add(tile);
        }
        root.Children.Add(grid);
        File.WriteAllBytes(Path.Combine(directory, "mira-icon-family.png"), Render(root, 720, 520));
    }
    private static byte[] Render(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var bytes = new MemoryStream(); encoder.Save(bytes); return bytes.ToArray();
    }
}
