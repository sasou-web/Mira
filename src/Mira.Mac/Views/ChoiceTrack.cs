using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>
/// A segmented switch, as on Windows (Views/ChoiceTrack.cs of Mira.Desktop): its parts side by side, each as wide as its
/// label needs and the others equal (<see cref="SegmentedSwitch.Widths"/>), or in a grid of <c>columns</c> (the languages
/// of a subtitle search), a solid pill behind the chosen one. A new choice sends the pill there as a jelly
/// (<see cref="JellyPill"/> of Core, the same springs as Windows; across rows, a second one for its top and bottom),
/// drawn at every frame of the screen, while the ink of each part follows how much of it the pill covers (ogsas,
/// 2026-11-14). Without motion (<see cref="MacMotion.Reduced"/>), at once.
/// </summary>
/// <remarks>
/// The player's menus are built anew after each choice (<c>PlayerPage.RefreshMenus</c>): a track given a
/// <c>key</c> hands its moving pill over to the next track of that key, which goes on with it as if nothing was rebuilt.
/// The parts keep no background, edge or weight of their own (<c>track</c> of App.axaml): the pill is their only body.
/// </remarks>
public class ChoiceTrack : Control
{
    private static readonly Dictionary<string, (JellyPill X, JellyPill Y, int Selected)> Moving = [];
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private readonly List<Control> _parts = [];
    private readonly int _columns;
    private readonly string? _key;
    private JellyPill _x = new(), _y = new();
    private Rect[] _cells = [];
    /// <summary>Laid out from scratch: the pill goes under the chosen part at once (unless it was handed over moving).</summary>
    private bool _snap = true;
    private bool _framed;
    private bool _adopted;

    /// <summary>The solid pill, lighter than any track it moves in.</summary>
    public static readonly IBrush Pill = Ui.Brush("#363636");
    public double PillRadius { get; init; } = 8;
    /// <summary>How many parts.</summary>
    public int Count => _parts.Count;
    /// <summary>The part lit, from 0 (-1: none).</summary>
    public int Selected { get; private set; } = -1;

    public ChoiceTrack(int columns = 0, string? key = null)
    {
        _columns = columns; _key = key;
        if (key is not null && Moving.Remove(key, out var handed) && handed.X.Moving(Now))
        {
            _x = handed.X; _y = handed.Y; Selected = handed.Selected; _adopted = true; _snap = false;
        }
        DetachedFromVisualTree += (_, _) => _framed = false;
    }

    /// <summary>A part of the track; <paramref name="chosen"/> lights it at once.</summary>
    public void Add(Control part, bool chosen = false)
    {
        part.Classes.Add("track");
        part.PointerEntered += (_, _) => Paint();
        part.PointerExited += (_, _) => Paint();
        _parts.Add(part); LogicalChildren.Add(part); VisualChildren.Add(part);
        InvalidateMeasure();
        if (chosen && !_adopted) Selected = _parts.Count - 1;
        _snap |= !_adopted;
        Paint();
    }

    /// <summary><paramref name="index"/> lit: the pill goes there as a jelly, or at once (no motion, not laid out yet).</summary>
    public void Select(int index, bool animate = true)
    {
        if (index < 0 || index >= _parts.Count) return;
        Selected = index;
        if (index >= _cells.Length) { _snap = true; InvalidateArrange(); return; }
        var cell = _cells[index];
        if (animate && !MacMotion.Reduced && !_snap && IsEffectivelyVisible && Bounds.Width > 0)
        {
            var now = Now;
            _x.Go(cell.Left, cell.Right, now); _y.Go(cell.Top, cell.Bottom, now);
            if (_key is not null) Moving[_key] = (_x, _y, index);
            Start();
        }
        else Put(cell);
        Paint(); InvalidateVisual();
    }

    /// <summary>For the self-check: where the pill should end up (its left edge), and where it stands now, mid-move too.</summary>
    public double PillTarget => _x.TargetLeft;
    public double PillAt => _x.At(Now).Left;

    private void Put(Rect cell) { _x.Put(cell.Left, cell.Right); _y.Put(cell.Top, cell.Bottom); }

    protected override Size MeasureOverride(Size available)
    {
        double width = 0, height = 0;
        foreach (var part in _parts)
        {
            part.Measure(new Size(double.PositiveInfinity, available.Height));
            width += part.DesiredSize.Width; height = Math.Max(height, part.DesiredSize.Height);
        }
        if (_columns <= 0 || _parts.Count <= _columns) return new Size(width, height);
        var rows = (_parts.Count + _columns - 1) / _columns;
        var column = _parts.Max(p => p.DesiredSize.Width);
        return new Size(column * _columns, height * rows);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var parts = _parts;
        var cells = new Rect[parts.Count];
        if (_columns <= 0 || parts.Count <= _columns)
        {
            var widths = SegmentedSwitch.Widths(parts.Select(p => p.DesiredSize.Width).ToList(), size.Width);
            var x = 0d;
            for (var i = 0; i < parts.Count; i++) { cells[i] = new Rect(x, 0, widths[i], size.Height); x += widths[i]; }
        }
        else
        {
            var rows = (parts.Count + _columns - 1) / _columns;
            var width = size.Width / _columns; var height = size.Height / rows;
            for (var i = 0; i < parts.Count; i++) cells[i] = new Rect(i % _columns * width, i / _columns * height, width, height);
        }
        for (var i = 0; i < parts.Count; i++) parts[i].Arrange(cells[i]);
        var moved = !cells.SequenceEqual(_cells);
        _cells = cells;
        if (Selected >= 0 && Selected < cells.Length)
        {
            if (_adopted)
            {
                // Handed over moving: on to the same place, at the speed it had.
                _adopted = false;
                var cell = cells[Selected]; var now = Now;
                if (cell.Left != _x.TargetLeft || cell.Right != _x.TargetRight) { _x.Go(cell.Left, cell.Right, now); _y.Go(cell.Top, cell.Bottom, now); }
                Start();
            }
            else if (_snap || moved && !_framed) Put(cells[Selected]);
        }
        _snap = false;
        Paint();
        return size;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Selected < 0 || _cells.Length == 0) return;
        var now = Now;
        var (left, right, share) = _x.At(now);
        var (top, bottom, _) = _y.At(now);
        var height = (bottom - top) * share;
        var middle = (top + bottom) / 2;
        var area = new Rect(left, middle - height / 2, Math.Max(0, right - left), Math.Max(0, height));
        context.DrawRectangle(Pill, null, new RoundedRect(area, Math.Min(PillRadius, area.Height / 2)));
    }

    private void Start()
    {
        if (_framed) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        _framed = true;
        top.RequestAnimationFrame(Frame);
    }
    private void Frame(TimeSpan _)
    {
        if (!_framed) return;
        Paint(); InvalidateVisual();
        if (_x.Moving(Now) || _y.Moving(Now)) { if (TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(Frame); else _framed = false; }
        else { _framed = false; Paint(); InvalidateVisual(); }
    }

    /// <summary>Each part's ink: lit as much as the pill covers it, at once under the pointer.</summary>
    private void Paint()
    {
        var now = Now;
        var (left, right, _) = _x.At(now);
        var (top, bottom, _) = _y.At(now);
        var lit = ((ISolidColorBrush)Ui.Ink).Color; var muted = ((ISolidColorBrush)Ui.Muted).Color;
        for (var i = 0; i < _parts.Count; i++)
        {
            if (_parts[i] is not TemplatedControl part) continue;
            double share = 0;
            if (Selected >= 0 && i < _cells.Length)
            {
                var cell = _cells[i];
                // Across rows: only the row the pill is on (its middle).
                var row = (top + bottom) / 2;
                share = row >= cell.Top - .5 && row <= cell.Bottom + .5 ? SegmentedSwitch.Lit(left, right, cell.Left, cell.Right) : 0;
            }
            else if (i == Selected) share = 1;
            if (part.IsPointerOver && part.IsEffectivelyEnabled) share = 1;
            var color = Mix(muted, lit, share);
            if (part.Foreground is SolidColorBrush brush && part.IsSet(TemplatedControl.ForegroundProperty) && brush != Ui.Ink && brush != Ui.Muted) { if (brush.Color != color) brush.Color = color; }
            else part.Foreground = new SolidColorBrush(color);
        }
    }

    private static Color Mix(Color from, Color to, double share) => Color.FromArgb(
        (byte)Math.Round(from.A + (to.A - from.A) * share), (byte)Math.Round(from.R + (to.R - from.R) * share),
        (byte)Math.Round(from.G + (to.G - from.G) * share), (byte)Math.Round(from.B + (to.B - from.B) * share));
}
