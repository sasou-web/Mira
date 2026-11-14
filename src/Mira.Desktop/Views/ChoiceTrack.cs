using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Mira.Core;

namespace Mira.Desktop.Views;

/// <summary>
/// A segmented switch: its parts side by side, each as wide as its label needs and the others equal
/// (<see cref="SegmentedSwitch.Widths"/>: equal columns cut « Auto » of Quality, 0.9.9), a solid pill behind the chosen
/// one. A new choice sends the pill there as a jelly (<see cref="JellyPill"/> of Core: the edge in front first, stretched
/// on the way, a soft rebound, then still), drawn at every frame of the screen, while the ink of each part follows how
/// much of it the pill covers (ogsas, 2026-11-14). Without motion (<see cref="Motion.Reduced"/>), at once.
/// </summary>
/// <remarks>
/// The parts are radio buttons (the player's Quality, Sleep timer, the languages of a subtitle search: chosen by a
/// click, the pill follows) or any control whose choice the code sets (<see cref="Segmented"/>, Réglages | Aide). This
/// track paints their ink, never their background: the pill is the only body under a part, and the pointer only lights
/// a part's ink, at once. The pill is drawn by the track itself, under its parts, into one drawing redone at each frame:
/// nothing is laid out again while it moves.
/// </remarks>
public class ChoiceTrack : Panel
{
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(nameof(Selected), typeof(int), typeof(ChoiceTrack), new FrameworkPropertyMetadata(-1, (d, _) => { if (!((ChoiceTrack)d)._going) ((ChoiceTrack)d).Chosen(animate: false); }));
    /// <summary>The part lit, from 0; set by the code, at once (a part checked by a click goes there as a jelly).</summary>
    public int Selected { get => (int)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public static readonly DependencyProperty PillRadiusProperty = DependencyProperty.Register(nameof(PillRadius), typeof(double), typeof(ChoiceTrack), new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double PillRadius { get => (double)GetValue(PillRadiusProperty); set => SetValue(PillRadiusProperty, value); }
    public static readonly DependencyProperty PillProperty = DependencyProperty.Register(nameof(Pill), typeof(Brush), typeof(ChoiceTrack), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush? Pill { get => (Brush?)GetValue(PillProperty); set => SetValue(PillProperty, value); }

    private readonly JellyPill _pill = new();
    private readonly DrawingGroup _drawing = new();
    private (double Left, double Right)[] _cells = [];
    private double _height;
    private bool _framed;
    /// <summary>The choice set by <see cref="Choose"/> with motion: the property's own change must not put the pill there at once.</summary>
    private bool _going;
    /// <summary>Laid out again from scratch (new parts): the pill goes under the chosen one at once.</summary>
    private bool _snap = true;
    /// <summary>Where the pill should start from once this track has a width (shown for the first time mid-turn), in parts.</summary>
    private double? _from;
    private static readonly double Ticks = Stopwatch.Frequency;
    private static double Now => Stopwatch.GetTimestamp() / Ticks;

    public ChoiceTrack()
    {
        SetResourceReference(PillProperty, "TrackPill");
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not UIElement part || InternalChildren.IndexOf(part) is not (>= 0 and var index)) return;
            Choose(index, animate: IsLoaded && IsVisible && !_snap);
        }));
        // Shown again (a menu opened anew, high contrast turned on meanwhile): the pill in place and the inks as the palette has them now.
        IsVisibleChanged += (_, _) => { if (IsVisible && _from is null && !_framed) { Stop(); _snap = true; InvalidateArrange(); } };
        Unloaded += (_, _) => Stop();
    }

    /// <summary>Where the pill stands now, in parts (0 the first, 1.5 halfway between the second and the third), mid-move too.</summary>
    public double Position
    {
        get
        {
            if (_cells.Length < 2) return 0;
            var (left, right, _) = _pill.At(Now);
            var middle = (left + right) / 2;
            for (var i = 0; i < _cells.Length - 1; i++)
            {
                var here = Middle(i); var next = Middle(i + 1);
                if (middle <= next || i == _cells.Length - 2) return Math.Clamp(i + (middle - here) / (next - here), 0, _cells.Length - 1);
            }
            return 0;
        }
    }

    /// <summary><paramref name="index"/> lit: the pill goes there as a jelly, or at once.</summary>
    public void Choose(int index, bool animate)
    {
        if (!animate) { SetCurrentValue(SelectedProperty, index); return; }
        _going = true;
        try { SetCurrentValue(SelectedProperty, index); } finally { _going = false; }
        Chosen(animate: true);
    }

    /// <summary>
    /// This track shown in place of another one (Réglages turned over to Aide: one switch in each window): its pill
    /// starts where the other's stood, <paramref name="from"/> in parts, and goes to <see cref="Selected"/>.
    /// </summary>
    public void SlideFrom(double from)
    {
        if (_cells.Length == 0 || ActualWidth <= 0) { _from = from; return; }
        StartFrom(from);
    }
    private void StartFrom(double from)
    {
        _from = null;
        if (Motion.Reduced || _cells.Length < 2) { Snap(); return; }
        var low = (int)Math.Floor(Math.Clamp(from, 0, _cells.Length - 1)); var high = Math.Min(low + 1, _cells.Length - 1); var share = from - low;
        _pill.Put(Lerp(_cells[low].Left, _cells[high].Left, share), Lerp(_cells[low].Right, _cells[high].Right, share));
        _snap = false;
        Chosen(animate: true);
    }

    private void Chosen(bool animate)
    {
        if (_cells.Length == 0 || Selected < 0 || Selected >= _cells.Length) { Draw(); return; }
        var (left, right) = _cells[Selected];
        if (animate && !Motion.Reduced && !_snap)
        {
            _pill.Go(left, right, Now);
            Start();
        }
        else { Stop(); _pill.Put(left, right); }
        Draw();
    }
    private void Snap() { Stop(); if (Selected >= 0 && Selected < _cells.Length) _pill.Put(_cells[Selected].Left, _cells[Selected].Right); _snap = false; Draw(); }

    protected override void OnVisualChildrenChanged(DependencyObject? visualAdded, DependencyObject? visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (visualAdded is Control added)
        {
            added.MouseEnter += Hovered; added.MouseLeave += Hovered;
            // A part added already chosen (a menu built anew): the pill under it, at once.
            if (added is ToggleButton { IsChecked: true }) SetCurrentValue(SelectedProperty, InternalChildren.IndexOf(added));
        }
        if (visualRemoved is Control removed) { removed.MouseEnter -= Hovered; removed.MouseLeave -= Hovered; }
        _snap = true;
    }
    private void Hovered(object sender, MouseEventArgs e) => Paint();

    protected override Size MeasureOverride(Size available)
    {
        var height = 0d; var width = 0d;
        foreach (UIElement part in InternalChildren)
        {
            part.Measure(new Size(double.PositiveInfinity, available.Height));
            width += part.DesiredSize.Width; height = Math.Max(height, part.DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var parts = InternalChildren.Cast<UIElement>().ToList();
        var widths = SegmentedSwitch.Widths(parts.Select(p => p.DesiredSize.Width).ToList(), size.Width);
        var cells = new (double, double)[parts.Count];
        var x = 0d;
        for (var i = 0; i < parts.Count; i++)
        {
            parts[i].Arrange(new Rect(x, 0, widths[i], size.Height));
            cells[i] = (x, x + widths[i]); x += widths[i];
        }
        var moved = _height != size.Height || !cells.SequenceEqual(_cells);
        _cells = cells; _height = size.Height;
        if (_from is { } from && size.Width > 0) StartFrom(from);
        else if (_snap || moved) Snap();
        return size;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawDrawing(_drawing);
    }

    private void Start()
    {
        if (_framed) return;
        _framed = true; CompositionTarget.Rendering += Frame;
    }
    private void Stop()
    {
        if (!_framed) return;
        _framed = false; CompositionTarget.Rendering -= Frame;
    }
    private void Frame(object? sender, EventArgs e)
    {
        Draw();
        if (!_pill.Moving(Now)) Stop();
    }

    /// <summary>The pill where it is now, and the ink of each part.</summary>
    private void Draw()
    {
        using (var dc = _drawing.Open())
        {
            if (Pill is { } fill && _cells.Length > 0 && Selected >= 0 && _height > 0)
            {
                var (left, right, share) = _pill.At(Now);
                var height = _height * share;
                var area = new Rect(left, (_height - height) / 2, Math.Max(0, right - left), height);
                dc.DrawGeometry(fill, null, Squircle.Geometry(area, Math.Min(PillRadius, height / 2)));
            }
        }
        Paint();
    }

    private void Paint()
    {
        var lit = Ink("Text"); var muted = Ink("Muted");
        var (left, right, _) = _pill.At(Now);
        for (var i = 0; i < InternalChildren.Count && i < _cells.Length; i++)
        {
            if (InternalChildren[i] is not Control part) continue;
            var share = Selected < 0 ? 0 : SegmentedSwitch.Lit(left, right, _cells[i].Left, _cells[i].Right);
            if (part.IsMouseOver && part.IsEnabled) share = 1;
            var color = Mix(muted, lit, share);
            if (part.Foreground is SolidColorBrush { IsFrozen: false } brush && ReferenceEquals(part.ReadLocalValue(Control.ForegroundProperty), brush)) { if (brush.Color != color) brush.Color = color; }
            else part.Foreground = new SolidColorBrush(color);
        }
    }

    private double Middle(int i) => (_cells[i].Left + _cells[i].Right) / 2;
    private static double Lerp(double from, double to, double share) => from + (to - from) * share;
    private static Color Ink(string key) => Palette.Get(key) is SolidColorBrush solid ? solid.Color : Colors.Gray;
    private static Color Mix(Color from, Color to, double share) => Color.FromArgb(
        (byte)Math.Round(from.A + (to.A - from.A) * share), (byte)Math.Round(from.R + (to.R - from.R) * share),
        (byte)Math.Round(from.G + (to.G - from.G) * share), (byte)Math.Round(from.B + (to.B - from.B) * share));
}
