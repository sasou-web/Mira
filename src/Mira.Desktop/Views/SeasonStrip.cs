using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using Mira.Core;
using static Mira.Core.Lang;

namespace Mira.Desktop.Views;

/// <summary>
/// The season pills on one line that scrolls (maquette A « Le fondu », ogsas 2026-11-19): their whole names, never cut to
/// their number, the line stopping at <see cref="FrameworkElement.MaxWidth"/> (the end of the page's buttons, under « ⋮ »)
/// and its last name fading into the dark over <see cref="Fade"/>; scrolled, a fade at the left too (<see cref="EdgeFade"/>).
/// An arrow only while the pointer is over the line, on the fade, a click moving a page of names; the wheel over the line
/// moves it along until its end (<see cref="SmoothScroll"/>). The season shown is brought into view. A short series fits:
/// nothing fades, no arrow.
/// </summary>
[ContentProperty(nameof(Pills))]
public sealed class SeasonStrip : Grid
{
    /// <summary>Width of the fade at an edge where more names wait.</summary>
    public const double Fade = 110;
    private const double Arrow = 32;

    private readonly ScrollViewer _scroll = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false, CanContentScroll = false
    };
    private readonly Button _back, _next;
    private SeasonRow? _row;
    private int _revealed = -1;

    public SeasonStrip()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        _scroll.SetResourceReference(StyleProperty, typeof(ScrollViewer));
        SmoothScroll.SetAcross(_scroll, true);
        EdgeFade.SetLength(_scroll, Fade); EdgeFade.SetAcross(_scroll, true);
        Children.Add(_scroll);
        _back = ArrowButton("back", L("Saisons précédentes", "Previous seasons"), HorizontalAlignment.Left, -1);
        _next = ArrowButton("next", L("Saisons suivantes", "Next seasons"), HorizontalAlignment.Right, 1);
        _scroll.ScrollChanged += (_, _) => UpdateArrows();
        MouseEnter += (_, _) => UpdateArrows();
        MouseLeave += (_, _) => UpdateArrows();
    }

    /// <summary>The pills, in the line that scrolls.</summary>
    public SeasonRow? Pills { get => _row; set { _row = value; _scroll.Content = value; } }

    /// <summary>For the checks: how far the line has moved, how far it can, and whether an arrow shows.</summary>
    public double Offset => _scroll.HorizontalOffset;
    public double Scrollable => _scroll.ScrollableWidth;
    public bool ArrowsShown => _back.IsHitTestVisible || _next.IsHitTestVisible;

    private Button ArrowButton(string icon, string name, HorizontalAlignment side, int way)
    {
        var button = new Button
        {
            Width = Arrow, Height = Arrow, Padding = new Thickness(0), HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Center,
            Content = new Icon { Kind = icon, Width = 15, Height = 15 }, Focusable = false, Opacity = 0, IsHitTestVisible = false,
            ToolTip = name
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        button.SetResourceReference(Control.BackgroundProperty, "Raised");
        button.SetResourceReference(Control.BorderBrushProperty, "LineStrong");
        Motion.SetRadius(button, new CornerRadius(Arrow / 2));
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => Page(way);
        Children.Add(button);
        return button;
    }

    /// <summary>A page of names that way, the fades left out, eased.</summary>
    public void Page(int way) => SmoothScroll.By(_scroll, way * Math.Max(Arrow * 3, _scroll.ViewportWidth - Fade * 2), horizontal: true);

    /// <summary>New pills: back to the start, the season shown placed at once.</summary>
    public void Reset() { SmoothScroll.Cancel(_scroll); _scroll.ScrollToHorizontalOffset(0); _revealed = -1; }

    /// <summary>
    /// Pill <paramref name="index"/> brought into view, clear of the fades: eased when another season of the same line was
    /// shown before, at once the first time (the page opening).
    /// </summary>
    public void Reveal(int index)
    {
        var animate = _revealed >= 0 && index != _revealed && IsVisible;
        _revealed = index;
        // After the layout of the new pills.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_row is null || index < 0 || index >= _row.Children.Count) return;
            var slot = LayoutInformation.GetLayoutSlot((FrameworkElement)_row.Children[index]);
            var to = PillRow.Reveal(slot.Left, slot.Right, _scroll.HorizontalOffset, _scroll.ViewportWidth, _scroll.ScrollableWidth, Fade);
            if (Math.Abs(to - _scroll.HorizontalOffset) < .5) return;
            if (animate && !Motion.Reduced) SmoothScroll.By(_scroll, to - _scroll.HorizontalOffset, horizontal: true);
            else { SmoothScroll.Cancel(_scroll); _scroll.ScrollToHorizontalOffset(to); }
        });
    }

    /// <summary>Each arrow while the pointer is over the line and more names wait that way.</summary>
    private void UpdateArrows()
    {
        var over = IsMouseOver;
        Show(_back, over && _scroll.HorizontalOffset > .5);
        Show(_next, over && _scroll.HorizontalOffset < _scroll.ScrollableWidth - .5);
    }
    private static void Show(Button arrow, bool shown)
    {
        if (arrow.IsHitTestVisible == shown) return;
        arrow.IsHitTestVisible = shown;
        Motion.Fade(arrow, shown ? 1 : 0, 120);
    }
}
