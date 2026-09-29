using System.Windows;
using System.Windows.Media;

namespace Mira.Desktop.Views;

/// <summary>Subpixel capsule, without scaling its rounded end or invalidating layout each frame.</summary>
public sealed class CarouselProgress : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(CarouselProgress), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(CarouselProgress), new FrameworkPropertyMetadata(Brushes.WhiteSmoke, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly Brush Track = new SolidColorBrush(Color.FromArgb(115, 175, 175, 184));
    static CarouselProgress() { Track.Freeze(); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public CarouselProgress() { SnapsToDevicePixels = false; UseLayoutRounding = false; IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        var radius = ActualHeight / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(RenderSize), radius, radius);
        var width = ActualWidth * Math.Clamp(Progress, 0, 1);
        if (width > 0) dc.DrawRoundedRectangle(Accent, null, new Rect(0, 0, width, ActualHeight), Math.Min(radius, width / 2), radius);
    }
}
