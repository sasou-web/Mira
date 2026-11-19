using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// The season pills on one line that scrolls, as on Windows (Views/SeasonStrip.cs of Mira.Desktop, maquette A « Le
/// fondu », ogsas 2026-11-19): their whole names, the line stopping at <see cref="Layoutable.MaxWidth"/> (the end of the
/// page's buttons, under « ⋮ ») and its last name fading into the dark over <see cref="Fade"/>; scrolled, a fade at the
/// left too (<see cref="EdgeFade"/>). An arrow only while the pointer is over the line, on the fade, a click gliding a
/// page of names; the trackpad moves it sideways. The season shown is brought into view. A short series fits: nothing
/// fades, no arrow.
/// </summary>
public sealed class SeasonStrip : Grid
{
    /// <summary>Width of the fade at an edge where more names wait.</summary>
    public const double Fade = 110;
    private const double Arrow = 32;

    private readonly ScrollViewer _scroll;
    private readonly Button _back, _next;
    private readonly SeasonRow _row;
    private readonly ScrollMotion _motion = new();
    private bool _running;
    private TimeSpan? _last;
    private int _revealed = -1;

    public SeasonStrip(SeasonRow row)
    {
        _row = row;
        HorizontalAlignment = HorizontalAlignment.Left;
        _scroll = EdgeFade.Across(new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, Fade);
        Children.Add(_scroll);
        _back = ArrowButton("back", L("Saisons précédentes", "Previous seasons"), HorizontalAlignment.Left, -1);
        _next = ArrowButton("next", L("Saisons suivantes", "Next seasons"), HorizontalAlignment.Right, 1);
        _scroll.ScrollChanged += (_, _) => UpdateArrows();
        PointerEntered += (_, _) => UpdateArrows();
        PointerExited += (_, _) => UpdateArrows();
    }

    /// <summary>For the self-check: how far the line has moved, how far it can.</summary>
    public double Offset => _scroll.Offset.X;
    public double Scrollable => Maximum;
    private double Maximum => Math.Max(0, _scroll.Extent.Width - _scroll.Viewport.Width);

    private Button ArrowButton(string icon, string name, HorizontalAlignment side, int way)
    {
        var button = Ui.IconButton(icon, name, () => Page(way), "icon", 15);
        button.Width = button.Height = Arrow; button.Padding = new Thickness(0); button.CornerRadius = new CornerRadius(Arrow / 2);
        button.Background = Ui.Raised; button.BorderBrush = Ui.LineStrong; button.BorderThickness = new Thickness(1);
        button.HorizontalAlignment = side; button.VerticalAlignment = VerticalAlignment.Center;
        button.HorizontalContentAlignment = HorizontalAlignment.Center; button.VerticalContentAlignment = VerticalAlignment.Center;
        button.Focusable = false; button.Opacity = 0; button.IsHitTestVisible = false; button.IsVisible = false;
        button.Transitions = [new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(120) }];
        Children.Add(button);
        return button;
    }

    /// <summary>A page of names that way, the fades left out, eased.</summary>
    public void Page(int way) => GlideBy(way * Math.Max(Arrow * 3, _scroll.Viewport.Width - Fade * 2));

    /// <summary>New pills: back to the start, the season shown placed at once.</summary>
    public void Reset() { _running = false; _scroll.Offset = new Vector(0, 0); _revealed = -1; }

    /// <summary>
    /// Pill <paramref name="index"/> brought into view, clear of the fades: glided when another season of the same line
    /// was shown before, at once the first time (the page opening).
    /// </summary>
    public void Reveal(int index)
    {
        var animate = _revealed >= 0 && index != _revealed && IsEffectivelyVisible;
        _revealed = index;
        // After the layout of the new pills.
        Dispatcher.UIThread.Post(() =>
        {
            if (index < 0 || index >= _row.Children.Count) return;
            var cell = _row.Children[index].Bounds;
            var to = PillRow.Reveal(cell.Left, cell.Right, _scroll.Offset.X, _scroll.Viewport.Width, Maximum, Fade);
            if (Math.Abs(to - _scroll.Offset.X) < .5) return;
            if (animate && !MacMotion.Reduced) GlideBy(to - _scroll.Offset.X);
            else { _running = false; _scroll.Offset = new Vector(to, 0); }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Eased (<see cref="ScrollMotion"/>, the Windows wheel's), frame by frame; a press while moving adds to it.</summary>
    private void GlideBy(double pixels)
    {
        if (!_running) _motion.Reset(_scroll.Offset.X);
        _motion.Add(pixels, Maximum);
        if (MacMotion.Reduced) { _running = false; _scroll.Offset = new Vector(_motion.Step(10, Maximum), 0); return; }
        if (_running || !_motion.Moving || TopLevel.GetTopLevel(this) is not { } top) return;
        _running = true; _last = null;
        top.RequestAnimationFrame(Frame);
    }
    private void Frame(TimeSpan now)
    {
        if (!_running) return;
        var seconds = _last is { } last ? (now - last).TotalSeconds : 1 / 60d; _last = now;
        _scroll.Offset = new Vector(_motion.Step(Math.Clamp(seconds, 0, .1), Maximum), 0);
        if (_motion.Moving && IsEffectivelyVisible && TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(Frame);
        else _running = false;
    }

    /// <summary>Each arrow while the pointer is over the line and more names wait that way.</summary>
    private void UpdateArrows()
    {
        var over = IsPointerOver;
        Show(_back, over && _scroll.Offset.X > .5);
        Show(_next, over && _scroll.Offset.X < Maximum - .5);
    }
    private static void Show(Button arrow, bool shown)
    {
        if (arrow.IsHitTestVisible == shown) return;
        // Hidden, out of what VoiceOver reads too.
        arrow.IsHitTestVisible = shown; arrow.IsVisible = shown; arrow.Opacity = shown ? 1 : 0;
    }
}
