using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mira.Core;

namespace Mira.Desktop.Views;

/// <summary>
/// A segmented switch (Réglages | Aide): its parts side by side in equal columns, a lit pill behind the chosen one. A new
/// choice slides the pill there (<see cref="SegmentedSwitch"/> of Core: 200 ms, ease-out, landing still) while the ink
/// of the parts crosses over, instead of jumping (ogsas, 2026-11-14). Without motion (<see cref="Motion.Reduced"/>), at
/// once. The parts are buttons without a background of their own (<c>SheetSwitch</c> of the theme): this switch paints
/// their ink, never their style.
/// </summary>
public sealed class Segmented : Grid
{
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(nameof(Selected), typeof(int), typeof(Segmented), new FrameworkPropertyMetadata(0, (d, _) => ((Segmented)d).Place(animate: false)));
    /// <summary>The part lit, from 0; set by itself, at once.</summary>
    public int Selected { get => (int)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }

    private readonly Squircle _pill = new() { IsHitTestVisible = false, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9) };
    private readonly TranslateTransform _move = new();
    private readonly List<Control> _parts = [];
    /// <summary>Where the pill should start from once this switch has a width (shown for the first time mid-turn), in parts.</summary>
    private double? _from;

    public Segmented()
    {
        _pill.RenderTransform = _move;
        _pill.SetResourceReference(Squircle.BackgroundProperty, "Raised2");
        _pill.SetResourceReference(Squircle.BorderBrushProperty, "LineStrong");
        SizeChanged += (_, _) =>
        {
            if (_from is { } from) { _from = null; Slide(from); }
            else Place(animate: false);
        };
        // Shown again (another window over it, high contrast turned on meanwhile): the inks as the palette has them now.
        IsVisibleChanged += (_, _) => { if (IsVisible && _from is null && !Sliding) Place(animate: false); };
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        foreach (var part in Children.OfType<Control>())
        {
            ColumnDefinitions.Add(new ColumnDefinition());
            SetColumn(part, _parts.Count);
            _parts.Add(part);
        }
        _pill.HorizontalAlignment = HorizontalAlignment.Left;
        SetColumnSpan(_pill, Math.Max(1, _parts.Count));
        Children.Insert(0, _pill);
        Place(animate: false);
    }

    /// <summary>Where the pill stands now, in parts (mid-slide too).</summary>
    public double Position => SegmentedSwitch.Part(_move.X, _parts.Count, ActualWidth);
    private bool Sliding => Environment.TickCount64 < _slideEnds;
    private long _slideEnds;

    /// <summary>
    /// This switch shown in place of another one (Réglages turned over to Aide: one switch in each window): its pill
    /// starts where the other's stood, <paramref name="from"/> in parts, and slides to <see cref="Selected"/>.
    /// </summary>
    public void SlideFrom(double from)
    {
        if (ActualWidth <= 0) { _from = from; Place(animate: false); return; }
        Slide(from);
    }
    private void Slide(double from)
    {
        if (Motion.Reduced || _parts.Count < 2) { Place(animate: false); return; }
        var part = ActualWidth / _parts.Count;
        _move.BeginAnimation(TranslateTransform.XProperty, null);
        _move.X = from * part;
        Paint(from, animate: false);
        Place(animate: true);
    }

    /// <summary>The pill under <see cref="Selected"/>, the part's ink lit and the others' muted; sliding there or at once.</summary>
    private void Place(bool animate)
    {
        if (_parts.Count == 0) return;
        // Before the first layout, no width yet: the pill stays out of sight until it has one.
        _pill.Width = ActualWidth > 0 ? ActualWidth / _parts.Count : 0;
        var to = SegmentedSwitch.At(Selected, _parts.Count, ActualWidth);
        if (animate && !Motion.Reduced)
        {
            _slideEnds = Environment.TickCount64 + SegmentedSwitch.SlideMs;
            _move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(to, Duration()) { EasingFunction = Motion.EaseOut }, HandoffBehavior.SnapshotAndReplace);
            Paint(Selected, animate: true);
        }
        else
        {
            _move.BeginAnimation(TranslateTransform.XProperty, null);
            _move.X = to;
            Paint(Selected, animate: false);
        }
    }

    /// <summary>Each part's ink: Text on the pill, Muted beside it; in between while the pill stands between two parts.</summary>
    private void Paint(double at, bool animate)
    {
        var lit = Ink("Text"); var muted = Ink("Muted");
        for (var i = 0; i < _parts.Count; i++)
        {
            var share = Math.Clamp(1 - Math.Abs(i - at), 0, 1);
            var color = Mix(muted, lit, share);
            if (_parts[i].Foreground is not SolidColorBrush brush || brush.IsFrozen) _parts[i].Foreground = brush = new SolidColorBrush(color);
            if (animate) brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, Duration()) { EasingFunction = Motion.EaseOut }, HandoffBehavior.SnapshotAndReplace);
            else { brush.BeginAnimation(SolidColorBrush.ColorProperty, null); brush.Color = color; }
        }
    }
    private static Duration Duration() => new(TimeSpan.FromMilliseconds(SegmentedSwitch.SlideMs));
    private static Color Ink(string key) => Palette.Get(key) is SolidColorBrush solid ? solid.Color : Colors.Gray;
    private static Color Mix(Color from, Color to, double share) => Color.FromArgb(
        (byte)Math.Round(from.A + (to.A - from.A) * share), (byte)Math.Round(from.R + (to.R - from.R) * share),
        (byte)Math.Round(from.G + (to.G - from.G) * share), (byte)Math.Round(from.B + (to.B - from.B) * share));
}
