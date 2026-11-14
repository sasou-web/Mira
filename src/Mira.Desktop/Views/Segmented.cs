namespace Mira.Desktop.Views;

/// <summary>
/// Réglages | Aide at the top of their windows: a <see cref="ChoiceTrack"/> whose choice the code sets, one switch in
/// each window. Turned over to the other side, its pill goes on from where the other window's stood
/// (<see cref="ChoiceTrack.SlideFrom"/>), a solid pill as a jelly rather than a frame sliding (ogsas, 2026-11-14: « ya
/// que le contour blanc qui bouge »).
/// </summary>
public sealed class Segmented : ChoiceTrack
{
    public Segmented() => PillRadius = 9;
}
