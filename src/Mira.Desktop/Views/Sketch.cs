using System.Windows;
using System.Windows.Media;

namespace Mira.Desktop.Views;

/// <summary>
/// A drawing redone by the code at each frame (<see cref="Drawing"/>, opened anew): nothing laid out again while it
/// changes, as the switches' pill (<see cref="ChoiceTrack"/>). Takes no room and no clicks.
/// </summary>
public sealed class Sketch : FrameworkElement
{
    public DrawingGroup Drawing { get; } = new();
    public Sketch() { IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc) => dc.DrawDrawing(Drawing);
}
