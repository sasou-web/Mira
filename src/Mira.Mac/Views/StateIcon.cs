using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>
/// The glyph of a state button of a title's page (<see cref="IconPaths.States"/>: Favori, Vu, Suivre, Garder sur ce Mac),
/// as on Windows: its outline off, a solid white shape on, a ring of how far a copy is while it comes. The button around
/// it stays the same rounded square in every state: a change only pops this drawing, never the line of buttons, so a
/// click on the page moves nothing (checked by the self-check).
/// </summary>
public sealed class StateIcon : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<StateIcon>();
    /// <summary>The glyph's scale while it pops, 1 at rest.</summary>
    public static readonly StyledProperty<double> PopProperty = AvaloniaProperty.Register<StateIcon, double>(nameof(Pop), 1);
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double Pop { get => GetValue(PopProperty); set => SetValue(PopProperty, value); }
    static StateIcon() => AffectsRender<StateIcon>(ForegroundProperty, PopProperty);
    /// <summary>Lecture's dark ink, for the marks cut in a solid glyph.</summary>
    private static readonly IBrush Dark = new SolidColorBrush(Color.Parse("#0B0B0C"));
    private static readonly Dictionary<string, (Geometry Off, Geometry Solid, Geometry? Lines, Geometry? Marks, Geometry? Ring)> Shapes =
        IconPaths.States.ToDictionary(x => x.Key, x => (Geometry.Parse(x.Value.Off), Geometry.Parse(x.Value.Solid), Optional(x.Value.Lines), Optional(x.Value.Marks),
            x.Value.Off.StartsWith(IconPaths.Ring, StringComparison.Ordinal) ? Geometry.Parse(IconPaths.Ring) : null));
    private static Geometry? Optional(string path) => path.Length == 0 ? null : Geometry.Parse(path);
    private CancellationTokenSource? _popping;

    public StateIcon(string kind) { Kind = kind; Width = Height = 20; }
    public string Kind { get; private set; }
    public bool On { get; private set; }
    /// <summary>How far a copy is, from 0 to 1, while it comes; null otherwise.</summary>
    public double? Progress { get; private set; }

    /// <summary>
    /// The state to draw. A change of on or off pops the glyph once it is on screen (not with reduced motion); the same
    /// state drawn again, or a copy a little further, does not.
    /// </summary>
    public void Show(string kind, bool on, double? progress = null)
    {
        var flipped = on != On || kind != Kind;
        Kind = kind; On = on; Progress = progress is { } value ? Math.Clamp(value, 0, 1) : null;
        InvalidateVisual();
        if (flipped && IsEffectivelyVisible && TopLevel.GetTopLevel(this) is not null && !MacMotion.Reduced) _ = PopAsync(on);
    }
    /// <summary>On, the shape lands from smaller with a little overshoot, as something set; off, it settles without one.</summary>
    private async Task PopAsync(bool on)
    {
        _popping?.Cancel(); var popping = _popping = new CancellationTokenSource();
        var spring = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(on ? 340 : 220), Easing = on ? new BackEaseOut() : new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(PopProperty, on ? .62 : .8) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(PopProperty, 1d) } }
            }
        };
        try { await spring.RunAsync(this, popping.Token); }
        catch (OperationCanceledException) { }
    }

    public override void Render(DrawingContext context)
    {
        if (!Shapes.TryGetValue(Kind, out var shape)) return;
        var size = Bounds.Size;
        var scale = Math.Min(size.Width, size.Height) / 24 * Pop;
        var ink = Foreground ?? Brushes.White;
        using (context.PushTransform(Matrix.CreateTranslation(-12, -12) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(size.Width / 2, size.Height / 2)))
        {
            if (On)
            {
                // A thin pen of the same ink softens the solid's corners, as the outlines' round joins do.
                context.DrawGeometry(ink, Pen(ink, 1), shape.Solid);
                if (shape.Lines is { } lines) context.DrawGeometry(null, Pen(ink, 1.85), lines);
                if (shape.Marks is { } marks) context.DrawGeometry(null, Pen(Dark, 2.1), marks);
            }
            else if (Progress is { } done && shape.Ring is { } ring && shape.Marks is { } marks)
            {
                // A copy under way: the circle faint, how far it is drawn over it from the top, clockwise; the arrow inside.
                context.DrawGeometry(null, Pen(Faded(ink, .3), 1.85), ring);
                if (done > 0) context.DrawGeometry(null, Pen(ink, 1.85), Arc(done));
                context.DrawGeometry(null, Pen(ink, 1.85), marks);
            }
            else
            {
                if (Kind is "favorite" or "follow") context.DrawGeometry(Faded(ink, .15), null, shape.Off);
                context.DrawGeometry(null, Pen(ink, 1.85), shape.Off);
            }
        }
    }
    /// <summary>The part of the ring (radius 9 round the middle of the grid) from the top, clockwise, as far as <paramref name="done"/>.</summary>
    private static Geometry Arc(double done)
    {
        if (done >= .999) return Geometry.Parse(IconPaths.Ring);
        var angle = done * 2 * Math.PI;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(12, 3), false);
            path.ArcTo(new Point(12 + 9 * Math.Sin(angle), 12 - 9 * Math.Cos(angle)), new Size(9, 9), 0, done > .5, SweepDirection.Clockwise);
            path.EndFigure(false);
        }
        return geometry;
    }
    private static IBrush Faded(IBrush brush, double alpha) => brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, solid.Opacity * alpha) : brush;
    private static Pen Pen(IBrush brush, double width) => new(brush, width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
}
