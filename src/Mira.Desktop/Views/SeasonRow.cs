using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mira.Core;

namespace Mira.Desktop.Views;

/// <summary>
/// The season pills of a series' page, a row that wraps, with one white pill drawn under them (maquette A « La gelée »,
/// ogsas 2026-11-18): another season chosen, it goes there as a jelly (<see cref="JellyPill"/>, the springs of
/// <see cref="ChoiceTrack"/>), to where that season will stand once opened, while in a compact row (« Les numéros »,
/// <see cref="EpisodeTiles.Compact"/>) the old season closes to its number and the new one opens to its name
/// (<see cref="PillRow"/>), the others following. The ink of each pill follows how much of it the white pill covers; its
/// edge goes under it. Another line: the white pill fades in there. Without motion (<see cref="Motion.Reduced"/>), at once.
/// </summary>
/// <remarks>
/// The pills are buttons whose content is their label or, compact, a <see cref="Face"/> of their whole name and their
/// number (both centred, cut by the pill as it opens: asking no width, so that a button never wants more than its cell,
/// which WPF would have put at its left and cut, Dell 2026-11-18). Their background and edge stay transparent: the row
/// draws their rings, then the white pill over them, so that no grey arc shows on it on the way.
/// </remarks>
public sealed class SeasonRow : Panel
{
    /// <summary>A season watched to its end or one the library lacks: its number quieter.</summary>
    public static readonly DependencyProperty QuietProperty = DependencyProperty.RegisterAttached("Quiet", typeof(bool), typeof(SeasonRow), new PropertyMetadata(false));
    public static bool GetQuiet(DependencyObject pill) => (bool)pill.GetValue(QuietProperty);
    public static void SetQuiet(DependencyObject pill, bool value) => pill.SetValue(QuietProperty, value);

    private const double LineGap = 8;
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private readonly JellyPill _pill = new();
    private readonly DrawingGroup _drawing = new();
    private readonly List<PillRow.Opener> _open = [];
    private Rect[] _cells = [];
    private double _height, _pillTop, _fadeSince = double.NegativeInfinity;
    private int _chosen = -1;
    private bool _compact, _snap = true, _framed;

    public SeasonRow()
    {
        IsVisibleChanged += (_, _) => { if (IsVisible && !_framed) { _snap = true; InvalidateArrange(); } };
        Unloaded += (_, _) => Stop();
    }

    /// <summary>Pills cut to their number but the one shown (more than <see cref="EpisodeTiles.MaxPills"/> pages).</summary>
    public bool Compact { get => _compact; set { _compact = value; _snap = true; InvalidateMeasure(); } }
    private double Gap => _compact ? 7 : 8;

    /// <summary>For the checks: where the white pill goes and where it stands now.</summary>
    public double PillTarget => _pill.TargetLeft;
    public double PillAt => _pill.At(Now).Left;

    /// <summary>Pill <paramref name="index"/> shown: the white pill goes there as a jelly, or at once.</summary>
    public void Show(int index, bool animate)
    {
        if (index == _chosen && !_snap) return;
        _chosen = index;
        var now = Now;
        var go = animate && !_snap && !Motion.Reduced && IsLoaded && IsVisible && ActualWidth > 0 && index >= 0 && index < InternalChildren.Count;
        for (var i = 0; i < _open.Count; i++)
        {
            var open = !_compact || i == index ? 1 : 0;
            if (go) _open[i].Go(open, now); else _open[i].Put(open);
        }
        if (!go) { _snap = true; Stop(); InvalidateMeasure(); InvalidateArrange(); return; }
        var target = Target(ActualWidth);
        if (Math.Abs(target.Top - _pillTop) < .5) _pill.Go(target.Left, target.Right, now);
        else { _pill.Put(target.Left, target.Right); _pillTop = target.Top; _fadeSince = now; }
        Start();
        InvalidateMeasure();
    }

    protected override void OnVisualChildrenChanged(DependencyObject? visualAdded, DependencyObject? visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        while (_open.Count < InternalChildren.Count) _open.Add(new PillRow.Opener());
        if (_open.Count > InternalChildren.Count) _open.RemoveRange(InternalChildren.Count, _open.Count - InternalChildren.Count);
        _chosen = -1; _snap = true;
    }

    /// <summary>Each pill's width, round and whole: a compact pill's from its number and its name, else its own.</summary>
    private (double Round, double Whole) Widths(UIElement child)
    {
        if (!_compact || child is not Button { Content: Face { Children: [FrameworkElement whole, FrameworkElement cut] } face }) return (child.DesiredSize.Width, child.DesiredSize.Width);
        var chrome = Math.Max(0, child.DesiredSize.Width - face.DesiredSize.Width);
        return (Math.Max(PillRow.Round, cut.DesiredSize.Width + chrome + 16), whole.DesiredSize.Width + chrome + 32);
    }
    private List<double> Widths(Func<int, double> open)
    {
        var widths = new List<double>(InternalChildren.Count);
        for (var i = 0; i < InternalChildren.Count; i++) { var (round, whole) = Widths(InternalChildren[i]); widths.Add(PillRow.Width(round, whole, open(i))); }
        return widths;
    }
    /// <summary>Where the pill shown will stand once every pill is opened or closed.</summary>
    private Rect Target(double room)
    {
        if (_chosen < 0 || _chosen >= InternalChildren.Count) return Rect.Empty;
        var widths = Widths(i => _open[i].Target);
        var places = PillRow.Layout(widths, _height, Gap, LineGap, room);
        return new Rect(places[_chosen].X, places[_chosen].Y, widths[_chosen], _height);
    }

    protected override Size MeasureOverride(Size available)
    {
        var height = 0d;
        foreach (UIElement child in InternalChildren) { child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); height = Math.Max(height, child.DesiredSize.Height); }
        _height = _compact ? PillRow.Round : height;
        if (InternalChildren.Count == 0) return new Size();
        var now = Now;
        var widths = Widths(i => _open[i].At(now));
        var places = PillRow.Layout(widths, _height, Gap, LineGap, double.IsInfinity(available.Width) ? double.MaxValue : available.Width);
        var right = places.Select((p, i) => p.X + widths[i]).Max();
        return new Size(right, places[^1].Y + _height);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var now = Now;
        var widths = Widths(i => _open[i].At(now));
        var places = PillRow.Layout(widths, _height, Gap, LineGap, size.Width);
        var cells = new Rect[InternalChildren.Count];
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i] = new Rect(places[i].X, places[i].Y, widths[i], _height);
            InternalChildren[i].Arrange(cells[i]);
        }
        _cells = cells;
        var target = Target(size.Width);
        if (!target.IsEmpty)
        {
            var still = Math.Abs(target.Left - _pill.TargetLeft) < .01 && Math.Abs(target.Right - _pill.TargetRight) < .01 && Math.Abs(target.Top - _pillTop) < .5;
            // Laid out anew (new pills, a new width): the white pill under the one shown, at once; mid-move, on to its new place.
            if (_snap || !_framed) { if (!still || _snap) { _pill.Put(target.Left, target.Right); _pillTop = target.Top; _fadeSince = double.NegativeInfinity; } }
            else if (!still) { if (Math.Abs(target.Top - _pillTop) < .5) _pill.Go(target.Left, target.Right, now); else { _pill.Put(target.Left, target.Right); _pillTop = target.Top; } }
            _snap = false;
        }
        Draw();
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
    private bool Moving(double now) => _pill.Moving(now) || now - _fadeSince < PillRow.Fade || _open.Any(x => x.Moving(now));
    private void Frame(object? sender, EventArgs e)
    {
        var now = Now;
        if (_open.Any(x => x.Moving(now))) InvalidateMeasure();
        Draw();
        if (!Moving(now)) { Stop(); InvalidateMeasure(); }
    }

    /// <summary>The white pill where it is now, then each pill's ink, edge and text.</summary>
    private void Draw()
    {
        var now = Now;
        var (left, right, share) = _pill.At(now);
        if (_chosen >= 0 && _chosen < _cells.Length && Math.Abs(_cells[_chosen].Top - _pillTop) < .5) (left, right) = PillRow.Drawn(left, right, _cells[_chosen].Left, _cells[_chosen].Right);
        var fade = Math.Clamp((now - _fadeSince) / PillRow.Fade, 0, 1);
        var line = Ink("Line");
        using (var dc = _drawing.Open())
        {
            // Each pill's ring, under the white pill.
            var ring = new Pen(new SolidColorBrush(line), 1);
            foreach (var cell in _cells) { var edge = cell; edge.Inflate(-.5, -.5); dc.DrawGeometry(null, ring, Squircle.Geometry(edge, edge.Height / 2)); }
            if (_chosen >= 0 && _chosen < _cells.Length && _height > 0)
            {
                var height = _height * share;
                var area = new Rect(left, _pillTop + (_height - height) / 2, Math.Max(0, right - left), height);
                if (fade < 1) dc.PushOpacity(fade);
                dc.DrawGeometry(Palette.Get("Text"), null, Squircle.Geometry(area, height / 2));
                if (fade < 1) dc.Pop();
            }
        }
        var lit = Ink("OnAccent");
        for (var i = 0; i < InternalChildren.Count && i < _cells.Length; i++)
        {
            if (InternalChildren[i] is not Button pill) continue;
            var cell = _cells[i];
            var cover = _chosen < 0 ? 0 : Math.Abs(cell.Top - _pillTop) < .5 ? SegmentedSwitch.Lit(left, right, cell.Left, cell.Right) * fade : 0;
            Tint(pill, Control.ForegroundProperty, Mix(Ink(GetQuiet(pill) ? "Muted" : "TextSecondary"), lit, cover));
            if (pill.Content is Face { Children: [UIElement whole, UIElement cut] } && i < _open.Count)
            {
                var open = _open[i].At(now);
                whole.Opacity = PillRow.Name(open); cut.Opacity = PillRow.Number(open);
            }
        }
    }

    private static void Tint(Control pill, DependencyProperty property, Color color)
    {
        if (pill.ReadLocalValue(property) is SolidColorBrush { IsFrozen: false } brush) { if (brush.Color != color) brush.Color = color; }
        else pill.SetValue(property, new SolidColorBrush(color));
    }
    /// <summary>
    /// A compact pill's face: its whole name and its number, each at its own width and centred, cut to the pill. It asks
    /// for no width (the row sizes the pill), only its height.
    /// </summary>
    public sealed class Face : Panel
    {
        public Face() => ClipToBounds = true;
        protected override Size MeasureOverride(Size available)
        {
            var height = 0d;
            foreach (UIElement child in InternalChildren) { child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); height = Math.Max(height, child.DesiredSize.Height); }
            return new Size(0, height);
        }
        protected override Size ArrangeOverride(Size size)
        {
            foreach (UIElement child in InternalChildren) child.Arrange(new Rect((size.Width - child.DesiredSize.Width) / 2, (size.Height - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height));
            return size;
        }
    }

    private static Color Ink(string key) => Palette.Get(key) is SolidColorBrush solid ? solid.Color : Colors.Gray;
    private static Color Mix(Color from, Color to, double share) => Color.FromArgb(
        (byte)Math.Round(from.A + (to.A - from.A) * share), (byte)Math.Round(from.R + (to.R - from.R) * share),
        (byte)Math.Round(from.G + (to.G - from.G) * share), (byte)Math.Round(from.B + (to.B - from.B) * share));
}
