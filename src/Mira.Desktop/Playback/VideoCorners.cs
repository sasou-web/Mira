using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;

namespace Mira.Desktop.Playback;

/// <summary>
/// Smooth rounded corners for the native video. A window region (all Windows offers to round a child window) keeps or
/// drops whole pixels: alone, it left the mini-player's corners in stairs, the hairline over them showing every step
/// (0.9.9). The region now cuts a pixel inside the rounded shape, and Windows' compositor draws over the video's edge, at
/// the shape's exact curve: a black band wide enough to hide the steps, its inner edge smoothed against the picture, its
/// outer edge smoothed against the page under it, and the hairline that outlines the mini-player.
/// </summary>
public sealed class VideoCorners
{
    /// <summary>The band, in device pixels: the region's steps (within a pixel of the curve) and a pixel of smoothing.</summary>
    public const double Band = 3;
    /// <summary>The region, this far inside the shape (device pixels): it never reaches past the curve the band smooths.</summary>
    public const int Inset = 1;
    private readonly WindowLayers _layers;
    private readonly ShapeVisual _visual;
    private readonly CompositionRoundedRectangleGeometry _band, _line;
    private readonly CompositionSpriteShape _lineShape;
    private readonly CompositionColorBrush _lineBrush;
    private (int Left, int Top, int Width, int Height, float Radius, float Line, float Strength) _shown = (0, 0, 0, 0, -1, 0, 0);
    internal VideoCorners(WindowLayers layers)
    {
        _layers = layers; var compositor = layers.Compositor;
        _visual = compositor.CreateShapeVisual();
        _band = compositor.CreateRoundedRectangleGeometry(); _line = compositor.CreateRoundedRectangleGeometry();
        var band = compositor.CreateSpriteShape(_band);
        band.StrokeBrush = compositor.CreateColorBrush(Color.FromArgb(255, 0, 0, 0)); band.StrokeThickness = (float)Band;
        _lineBrush = compositor.CreateColorBrush(Hairline(1));
        _lineShape = compositor.CreateSpriteShape(_line); _lineShape.StrokeBrush = _lineBrush;
        _visual.Shapes.Add(band); _visual.Shapes.Add(_lineShape);
    }
    /// <summary>The hairline of the mini-player (Hairline of Themes/Cinema.xaml: white at 0x30), at this strength.</summary>
    private static Color Hairline(float strength) => Color.FromArgb((byte)Math.Round(0x30 * Math.Clamp(strength, 0, 1)), 255, 255, 255);
    /// <summary>
    /// Over the video's window, in the parent window's client pixels. <paramref name="line"/> is the hairline's width in
    /// device pixels, <paramref name="strength"/> how much of it shows (it fades in with the corners as the player shrinks).
    /// </summary>
    public void Place(int left, int top, int width, int height, double radius, double line, double strength)
    {
        var shown = (left, top, width, height, (float)radius, (float)line, (float)strength);
        if (shown == _shown) return;
        _shown = shown;
        _visual.Offset = new Vector3(left, top, 0); _visual.Size = new Vector2(width, height);
        // A stroke straddles its outline: each is drawn half a stroke inside the box, so its outer edge is the shape's.
        Outline(_band, (float)Band, width, height, (float)radius);
        Outline(_line, (float)line, width, height, (float)radius);
        _lineShape.StrokeThickness = (float)line; _lineBrush.Color = Hairline((float)strength);
        _layers.Show(_visual, top: false);
    }
    private static void Outline(CompositionRoundedRectangleGeometry geometry, float stroke, int width, int height, float radius)
    {
        var half = stroke / 2; var corner = Math.Max(0, radius - half);
        geometry.Offset = new Vector2(half, half);
        geometry.Size = new Vector2(Math.Max(0, width - stroke), Math.Max(0, height - stroke));
        geometry.CornerRadius = new Vector2(corner, corner);
    }
    public void Hide()
    {
        if (_shown.Radius < 0) return;
        _shown = (0, 0, 0, 0, -1, 0, 0);
        _layers.Hide(_visual);
    }
}
