using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Mira.Mac.Views;

/// <summary>
/// A list that scrolls under something that stays (the search field of Téléchargements): its top fades out over 18 px once
/// it has left its start, instead of lines cut short against the edge. Nothing at the start, where its first line shows whole.
/// As on Windows (Views/ScrollFade.cs of Mira.Desktop).
/// </summary>
public static class ScrollFade
{
    private static readonly IImmutableBrush Mask = new ImmutableLinearGradientBrush(
        [new ImmutableGradientStop(0, Colors.Transparent), new ImmutableGradientStop(1, Colors.Black)],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Absolute), endPoint: new RelativePoint(0, 18, RelativeUnit.Absolute));

    public static void Top(ScrollViewer scroll) =>
        scroll.ScrollChanged += (_, _) =>
        {
            var mask = scroll.Offset.Y > 0.5 ? Mask : null;
            if (!ReferenceEquals(scroll.OpacityMask, mask)) scroll.OpacityMask = mask;
        };
}
