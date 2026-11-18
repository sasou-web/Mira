using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>
/// The season pills of a series' page, as on Windows (Views/SeasonRow.cs of Mira.Desktop, maquette A « La gelée », ogsas
/// 2026-11-18): a row that wraps, drawing each pill's grey body and, over them, one white pill sent as a jelly
/// (<see cref="JellyPill"/>) to where the season chosen will stand, while in a compact row the old season closes to its
/// number and the new one opens to its name (<see cref="PillRow"/>), the others following. Each pill's ink follows how
/// much of it the white pill covers. Another line: the white pill fades in there. Without motion (<see cref="MacMotion.Reduced"/>), at once.
/// </summary>
/// <remarks>The pills are <c>chip season</c> buttons (App.axaml): transparent, the row draws their body.</remarks>
public sealed class SeasonRow : Control
{
    /// <summary>The pills (a control, not a panel: a panel's own drawing is sealed, and the row draws under its pills).</summary>
    public Controls Children { get; } = [];
    private const double LineGap = 8;
    private static readonly IBrush Body = Ui.Raised, White = Ui.Brush("#F5F5F7");
    private static readonly Color Lit = Color.Parse("#0B0B0B");
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private readonly JellyPill _pill = new();
    private readonly List<PillRow.Opener> _open = [];
    private readonly HashSet<Control> _quiet = [];
    private Rect[] _cells = [];
    private double _height, _pillTop, _fadeSince = double.NegativeInfinity;
    private int _chosen = -1;
    private bool _compact, _snap = true, _framed;

    public SeasonRow()
    {
        DetachedFromVisualTree += (_, _) => _framed = false;
        Children.CollectionChanged += ChildrenChanged;
    }

    /// <summary>Pills cut to their number but the one shown (more than <see cref="EpisodeTiles.MaxPills"/> pages).</summary>
    public bool Compact { get => _compact; set { _compact = value; _snap = true; InvalidateMeasure(); } }
    private double Gap => _compact ? 7 : 8;

    /// <summary>For the self-check: where the white pill goes and where it stands now.</summary>
    public double PillTarget => _pill.TargetLeft;
    public double PillAt => _pill.At(Now).Left;

    /// <summary>A season watched to its end or one the library lacks: its number quieter.</summary>
    public void SetQuiet(Control pill, bool quiet) { if (quiet) _quiet.Add(pill); else _quiet.Remove(pill); }

    /// <summary>Pill <paramref name="index"/> shown: the white pill goes there as a jelly, or at once.</summary>
    public void Show(int index, bool animate)
    {
        if (index == _chosen && !_snap) return;
        _chosen = index;
        var now = Now;
        var go = animate && !_snap && IsEffectivelyVisible && Bounds.Width > 0 && index >= 0 && index < Children.Count && !MacMotion.Reduced;
        for (var i = 0; i < _open.Count; i++)
        {
            var open = !_compact || i == index ? 1 : 0;
            if (go) _open[i].Go(open, now); else _open[i].Put(open);
        }
        if (!go) { _snap = true; _framed = false; InvalidateMeasure(); InvalidateArrange(); return; }
        var target = Target(Bounds.Width);
        if (Math.Abs(target.Top - _pillTop) < .5) _pill.Go(target.Left, target.Right, now);
        else { _pill.Put(target.Left, target.Right); _pillTop = target.Top; _fadeSince = now; }
        Start();
        InvalidateMeasure();
    }

    private void ChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        LogicalChildren.Clear(); VisualChildren.Clear();
        foreach (var child in Children) { LogicalChildren.Add(child); VisualChildren.Add(child); }
        InvalidateMeasure();
        while (_open.Count < Children.Count) _open.Add(new PillRow.Opener());
        if (_open.Count > Children.Count) _open.RemoveRange(Children.Count, _open.Count - Children.Count);
        _quiet.IntersectWith(Children);
        _chosen = -1; _snap = true;
    }

    /// <summary>Each pill's width, round and whole: a compact pill's from its number and its name, else its own.</summary>
    private (double Round, double Whole) Widths(Control child)
    {
        if (!_compact || child is not Button { Content: Face { Children: [Control whole, Control cut] } face }) return (child.DesiredSize.Width, child.DesiredSize.Width);
        var chrome = Math.Max(0, child.DesiredSize.Width - face.DesiredSize.Width);
        return (Math.Max(PillRow.Round, cut.DesiredSize.Width + chrome + 16), whole.DesiredSize.Width + chrome + 32);
    }
    private List<double> Widths(Func<int, double> open)
    {
        var widths = new List<double>(Children.Count);
        for (var i = 0; i < Children.Count; i++) { var (round, whole) = Widths(Children[i]); widths.Add(PillRow.Width(round, whole, open(i))); }
        return widths;
    }
    /// <summary>Where the pill shown will stand once every pill is opened or closed.</summary>
    private Rect Target(double room)
    {
        if (_chosen < 0 || _chosen >= Children.Count) return default;
        var widths = Widths(i => _open[i].Target);
        var places = PillRow.Layout(widths, _height, Gap, LineGap, room);
        return new Rect(places[_chosen].X, places[_chosen].Y, widths[_chosen], _height);
    }

    protected override Size MeasureOverride(Size available)
    {
        var height = 0d;
        foreach (var child in Children) { child.Measure(Size.Infinity); height = Math.Max(height, child.DesiredSize.Height); }
        _height = _compact ? PillRow.Round : height;
        if (Children.Count == 0) return default;
        var now = Now;
        var widths = Widths(i => _open[i].At(now));
        var places = PillRow.Layout(widths, _height, Gap, LineGap, double.IsInfinity(available.Width) ? double.MaxValue : available.Width);
        return new Size(places.Select((p, i) => p.X + widths[i]).Max(), places[^1].Y + _height);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var now = Now;
        var widths = Widths(i => _open[i].At(now));
        var places = PillRow.Layout(widths, _height, Gap, LineGap, size.Width);
        var cells = new Rect[Children.Count];
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i] = new Rect(places[i].X, places[i].Y, widths[i], _height);
            Children[i].Arrange(cells[i]);
        }
        _cells = cells;
        if (_chosen >= 0 && _chosen < Children.Count)
        {
            var target = Target(size.Width);
            var still = Math.Abs(target.Left - _pill.TargetLeft) < .01 && Math.Abs(target.Right - _pill.TargetRight) < .01 && Math.Abs(target.Top - _pillTop) < .5;
            // Laid out anew (new pills, a new width): the white pill under the one shown, at once; mid-move, on to its new place.
            if (_snap || !_framed) { if (!still || _snap) { _pill.Put(target.Left, target.Right); _pillTop = target.Top; _fadeSince = double.NegativeInfinity; } }
            else if (!still) { if (Math.Abs(target.Top - _pillTop) < .5) _pill.Go(target.Left, target.Right, now); else { _pill.Put(target.Left, target.Right); _pillTop = target.Top; } }
            _snap = false;
        }
        Paint();
        return size;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        foreach (var cell in _cells) context.DrawRectangle(Body, null, new RoundedRect(cell, cell.Height / 2));
        if (_chosen < 0 || _chosen >= _cells.Length || _height <= 0) return;
        var now = Now;
        var (left, right, share) = _pill.At(now);
        var height = _height * share;
        var area = new Rect(left, _pillTop + (_height - height) / 2, Math.Max(0, right - left), height);
        using (context.PushOpacity(Math.Clamp((now - _fadeSince) / PillRow.Fade, 0, 1)))
            context.DrawRectangle(White, null, new RoundedRect(area, height / 2));
    }

    private void Start()
    {
        if (_framed) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        _framed = true;
        top.RequestAnimationFrame(Frame);
    }
    private bool Moving(double now) => _pill.Moving(now) || now - _fadeSince < PillRow.Fade || _open.Any(x => x.Moving(now));
    private void Frame(TimeSpan _)
    {
        if (!_framed) return;
        var now = Now;
        if (_open.Any(x => x.Moving(now))) InvalidateMeasure();
        Paint(); InvalidateVisual();
        if (Moving(now) && TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(Frame);
        else { _framed = false; InvalidateMeasure(); InvalidateVisual(); }
    }

    /// <summary>Each pill's ink and text: lit as much as the white pill covers it.</summary>
    private void Paint()
    {
        var now = Now;
        var (left, right, _) = _pill.At(now);
        var fade = Math.Clamp((now - _fadeSince) / PillRow.Fade, 0, 1);
        for (var i = 0; i < Children.Count && i < _cells.Length; i++)
        {
            if (Children[i] is not Button { Content: Control face }) continue;
            var cell = _cells[i];
            var cover = _chosen >= 0 && Math.Abs(cell.Top - _pillTop) < .5 ? SegmentedSwitch.Lit(left, right, cell.Left, cell.Right) * fade : 0;
            var rest = ((ISolidColorBrush)(!_compact ? Ui.Ink : _quiet.Contains(Children[i]) ? Ui.Muted : Ui.TextSecondary)).Color;
            var color = Mix(rest, Lit, cover);
            // On the face, not the button: the button's pointer-over style sets its presenter's ink.
            if (face.GetValue(TextElement.ForegroundProperty) is SolidColorBrush brush && face.IsSet(TextElement.ForegroundProperty)) { if (brush.Color != color) brush.Color = color; }
            else face.SetValue(TextElement.ForegroundProperty, new SolidColorBrush(color));
            if (face is Face { Children: [Control whole, Control cut] } && i < _open.Count)
            {
                var open = _open[i].At(now);
                whole.Opacity = PillRow.Name(open); cut.Opacity = PillRow.Number(open);
            }
        }
    }

    private static Color Mix(Color from, Color to, double share) => Color.FromArgb(
        (byte)Math.Round(from.A + (to.A - from.A) * share), (byte)Math.Round(from.R + (to.R - from.R) * share),
        (byte)Math.Round(from.G + (to.G - from.G) * share), (byte)Math.Round(from.B + (to.B - from.B) * share));

    /// <summary>A compact pill's face: its whole name and its number, each at its own width and centred, cut by the pill as it opens.</summary>
    public sealed class Face : Panel
    {
        public Face() => ClipToBounds = true;
        protected override Size MeasureOverride(Size available)
        {
            double width = 0, height = 0;
            foreach (var child in Children) { child.Measure(Size.Infinity); width = Math.Max(width, child.DesiredSize.Width); height = Math.Max(height, child.DesiredSize.Height); }
            return new Size(width, height);
        }
        protected override Size ArrangeOverride(Size size)
        {
            foreach (var child in Children) child.Arrange(new Rect((size.Width - child.DesiredSize.Width) / 2, (size.Height - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height));
            return size;
        }
    }
}
