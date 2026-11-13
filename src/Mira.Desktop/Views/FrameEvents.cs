namespace Mira.Desktop.Views;

/// <summary>
/// What changed in the window since the last frame, for the trace of --smooth-check (<c>FrameMeter</c>): a card's
/// highlight set or taken off, an image set on a card, a card's picture made or let go. Each is an addition on the
/// interface's thread; the meter reads and clears them at each frame, nothing else reads them.
/// </summary>
internal static class FrameEvents
{
    public static int Hover, Images, Pictures;
    public static (int Hover, int Images, int Pictures) Take()
    {
        var taken = (Hover, Images, Pictures);
        Hover = Images = Pictures = 0;
        return taken;
    }
}
