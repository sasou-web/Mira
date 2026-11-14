using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Mira.Mac.Views;

/// <summary>
/// Réglages | Aide, as on Windows (Views/Segmented.cs of Mira.Desktop): a <see cref="ChoiceTrack"/> whose choice the
/// code sets, a solid pill going from one side to the other as a jelly rather than a frame sliding (ogsas, 2026-11-14:
/// « ya que le contour blanc qui bouge »). The parts keep no hover fill of their own (<c>bare</c>): only their ink lights.
/// </summary>
public sealed class Segmented : ChoiceTrack
{
    public Segmented(params Button[] parts)
    {
        PillRadius = 9;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            part.Classes.Add("bare");
            part.Background = Brushes.Transparent; part.BorderBrush = Brushes.Transparent;
            part.HorizontalAlignment = HorizontalAlignment.Stretch;
            Add(part, chosen: i == 0);
        }
    }
}
