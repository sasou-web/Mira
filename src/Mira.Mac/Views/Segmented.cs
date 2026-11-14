using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>
/// A segmented switch (Réglages | Aide), as on Windows (Views/Segmented.cs of Mira.Desktop): its parts side by side in
/// equal columns, a lit pill behind the chosen one. A new choice slides the pill there (<see cref="SegmentedSwitch"/> of
/// Core: 200 ms, ease-out, landing still) while the ink of the parts crosses over, instead of jumping (ogsas,
/// 2026-11-14). Without motion (<see cref="MacMotion.Reduced"/>), at once. The parts keep no hover fill of their own
/// (<c>bare</c>): the pill arriving under the pointer would slide in under it, unseen.
/// </summary>
public sealed class Segmented : Grid
{
    private readonly Border _pill = new()
    {
        Background = Ui.Brush("#242424"), BorderBrush = Ui.Brush("#3F3F3F"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
        HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false,
    };
    private readonly Button[] _parts;

    /// <summary>The part lit, from 0.</summary>
    public int Selected { get; private set; }

    public Segmented(params Button[] parts)
    {
        _parts = parts;
        ColumnDefinitions = new ColumnDefinitions(string.Join(',', parts.Select(_ => "*")));
        SetColumnSpan(_pill, Math.Max(1, parts.Length));
        Children.Add(_pill);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            part.Classes.Add("bare");
            part.Background = Brushes.Transparent; part.BorderBrush = Brushes.Transparent;
            part.HorizontalAlignment = HorizontalAlignment.Stretch;
            SetColumn(part, i); Children.Add(part);
        }
        // Laid out (shown for the first time, a new width): the pill under the chosen part at once.
        SizeChanged += (_, _) => Place(animate: false);
        Place(animate: false);
    }

    /// <summary>For the self-check: where the pill should end up (its left edge in this switch), and where it stands now, mid-slide too.</summary>
    public double PillTarget => SegmentedSwitch.At(Selected, _parts.Length, Bounds.Width);
    public double PillAt => _pill.RenderTransform?.Value.M31 ?? 0;

    /// <summary><paramref name="index"/> lit: the pill slides there, or is put there at once (no motion, not yet laid out).</summary>
    public void Select(int index, bool animate = true)
    {
        index = Math.Clamp(index, 0, Math.Max(0, _parts.Length - 1));
        if (index == Selected) return;
        Selected = index;
        Place(animate && !MacMotion.Reduced && Bounds.Width > 0 && IsEffectivelyVisible);
    }

    private void Place(bool animate)
    {
        if (_parts.Length == 0) return;
        var width = Bounds.Width;
        _pill.Width = width > 0 ? width / _parts.Length : 0;
        var time = TimeSpan.FromMilliseconds(SegmentedSwitch.SlideMs);
        _pill.Transitions = animate ? [new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = time, Easing = new CubicEaseOut() }] : null;
        _pill.RenderTransform = TransformOperations.Parse(FormattableString.Invariant($"translateX({SegmentedSwitch.At(Selected, _parts.Length, width)}px)"));
        for (var i = 0; i < _parts.Length; i++)
        {
            var part = _parts[i];
            // Never the buttons' empty transitions of the theme left out for good: only while this slide runs.
            part.Transitions = animate ? [new BrushTransition { Property = TemplatedControl.ForegroundProperty, Duration = time, Easing = new CubicEaseOut() }] : [];
            part.Foreground = i == Selected ? Ui.Ink : Ui.Muted;
        }
    }
}
