namespace Mira.Core;

/// <summary>
/// « Mode canapé » of Mira for Windows and Mac: a PC or a Mac plugged into the TV becomes its player. Full screen, the pages
/// laid out as on a 1280 × 720 window and drawn larger, the focus always shown, and everything reached with the arrow keys,
/// a game controller or a TV remote (an HDMI-CEC adapter types arrows, Enter and Escape). The rules both apps share:
/// the zoom, where an arrow goes, and what each button of a controller does.
/// </summary>
public static class Couch
{
    public static string Title => L("Mode canapé", "Couch mode");
    public static string Description => L("Pour un PC ou un Mac branché à la télé : plein écran, tout en plus grand, et tout se fait aux flèches, à la manette ou à la télécommande.",
        "For a PC or Mac plugged into the TV: full screen, everything larger, and everything works with the arrow keys, a game controller or the remote.");
    /// <summary>The keys, said under the setting.</summary>
    public static string Keys => L("Flèches pour se déplacer, Entrée pour ouvrir, Échap ou Retour arrière pour revenir. Manette : A ouvre, B revient, Y cherche, LB et RB changent de page.",
        "Arrow keys to move, Enter to open, Esc or Backspace to go back. Controller: A opens, B goes back, Y searches, LB and RB change page.");
    public static string On => L("Mode canapé activé. Pour en sortir : Réglages → Général.", "Couch mode on. To leave it: Settings → General.");
    /// <summary>A controller came while couch mode is off: a notice with a button, said once per launch.</summary>
    public static string ControllerFound => L("Manette branchée. Passer en mode canapé, pour piloter Mira depuis le canapé ?", "Controller connected. Switch to couch mode, to drive Mira from the couch?");
    public static string TurnOn => L("Mode canapé", "Couch mode");

    /// <summary>The window the pages are laid out for in couch mode: seen from a couch, a TV shows this much.</summary>
    public const double LayoutWidth = 1280, LayoutHeight = 720;
    /// <summary>
    /// The zoom of the pages in couch mode: as large as the window holds a 1280 × 720 layout, from 1 to 2.5, rounded down to
    /// a hundredth. A TV at 1920 × 1080 (or 3840 × 2160 at 200 %) is 1.5; a window smaller than that layout stays at 1.
    /// </summary>
    public static double Zoom(double width, double height)
    {
        if (!(width > 0) || !(height > 0) || !double.IsFinite(width) || !double.IsFinite(height)) return 1;
        var zoom = Math.Min(width / LayoutWidth, height / LayoutHeight);
        return Math.Clamp(Math.Floor(zoom * 100) / 100, 1, 2.5);
    }

    /// <summary>
    /// Where an arrow key goes from <paramref name="from"/>, among the controls that take the focus (<paramref name="targets"/>,
    /// where they are drawn, the one focused included or not): the nearest one past its edge that way. Those in its lane (a
    /// card of the same row for ← →, of the same column for ↑ ↓) first; otherwise the nearest, its distance across counting
    /// double. Null at the edge, where the key does nothing.
    /// </summary>
    public static int? Next(IReadOnlyList<CardGrid.Box> targets, CardGrid.Box from, CardGrid.Direction direction)
    {
        int? best = null, lane = null; double bestScore = double.MaxValue, laneScore = double.MaxValue;
        for (var i = 0; i < targets.Count; i++)
        {
            var to = targets[i];
            if (to == from || !(to.Width > 0) || !(to.Height > 0)) continue;
            var horizontal = direction is CardGrid.Direction.Left or CardGrid.Direction.Right;
            // How far past its edge (negative: overlapping), and its middle past this one's, that way.
            var (gap, beyond, along) = direction switch
            {
                CardGrid.Direction.Right => (to.Left - (from.Left + from.Width), to.CenterX - from.CenterX, Math.Min(to.Width, from.Width)),
                CardGrid.Direction.Left => (from.Left - (to.Left + to.Width), from.CenterX - to.CenterX, Math.Min(to.Width, from.Width)),
                CardGrid.Direction.Down => (to.Top - (from.Top + from.Height), to.CenterY - from.CenterY, Math.Min(to.Height, from.Height)),
                _ => (from.Top - (to.Top + to.Height), from.CenterY - to.CenterY, Math.Min(to.Height, from.Height)),
            };
            // Overlapping by more than half: beside it, not past it (a button over a picture, a slightly taller neighbour).
            if (beyond <= 1 || gap < -along / 2) continue;
            // The distance across: none when their spans overlap, else the space between them.
            var across = horizontal
                ? Math.Max(0, Math.Max(to.Top - (from.Top + from.Height), from.Top - (to.Top + to.Height)))
                : Math.Max(0, Math.Max(to.Left - (from.Left + from.Width), from.Left - (to.Left + to.Width)));
            var offset = horizontal ? Math.Abs(to.CenterY - from.CenterY) : Math.Abs(to.CenterX - from.CenterX);
            var score = Math.Max(0, gap) + 2 * across + offset / 4;
            if (across == 0 && score < laneScore) { laneScore = score; lane = i; }
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return lane ?? best;
    }

    /// <summary>
    /// Where the focus starts when nothing on the screen has it (a page just opened): the first control in reading order
    /// among those in view (<paramref name="targets"/>), rows read from the top within <paramref name="rowTolerance"/>.
    /// </summary>
    public static int? First(IReadOnlyList<CardGrid.Box> targets, double rowTolerance = 12)
    {
        int? first = null;
        for (var i = 0; i < targets.Count; i++)
        {
            if (!(targets[i].Width > 0) || !(targets[i].Height > 0)) continue;
            if (first is not { } f) { first = i; continue; }
            var (a, b) = (targets[i], targets[f]);
            if (a.Top < b.Top - rowTolerance || Math.Abs(a.Top - b.Top) <= rowTolerance && a.Left < b.Left) first = i;
        }
        return first;
    }

    /// <summary>
    /// An Xbox controller as Windows' XInput reads it (XINPUT_GAMEPAD): its buttons' bits and its left stick, from
    /// −32768 to 32767 (up positive). The stick's middle (XInput's dead zone, 7849) reads as still.
    /// </summary>
    public static PadState FromXInput(ushort buttons, short stickX, short stickY)
    {
        var pad = Pad.None;
        foreach (var (bit, button) in new[] { (0x0001, Pad.Up), (0x0002, Pad.Down), (0x0004, Pad.Left), (0x0008, Pad.Right), (0x0010, Pad.Menu), (0x0020, Pad.View), (0x0100, Pad.LeftShoulder), (0x0200, Pad.RightShoulder), (0x1000, Pad.A), (0x2000, Pad.B), (0x4000, Pad.X), (0x8000, Pad.Y) })
            if ((buttons & bit) != 0) pad |= button;
        static double Axis(short value) => Math.Abs((int)value) < 7849 ? 0 : Math.Clamp(value / 32767d, -1, 1);
        return new PadState(pad, Axis(stickX), Axis(stickY));
    }

    /// <summary>What a game controller's button does, in Mira's words (each app turns it into its keys).</summary>
    public enum Action { Up, Down, Left, Right, Select, Back, PlayPause, Subtitles, Skip, Search, PreviousPage, NextPage, JumpBack, JumpForward }
    /// <summary>
    /// A controller's buttons, as a TV app reads them: the cross and the left stick move, A opens, B goes back. In the
    /// player A (or ☰) pauses, X turns the subtitles on and off, Y skips an opening, LB and RB jump a minute; in the library
    /// Y searches, LB and RB go to the rail's previous or next page. Null: nothing there.
    /// </summary>
    public static Action? For(Pad button, bool playing) => button switch
    {
        Pad.Up => Action.Up,
        Pad.Down => Action.Down,
        Pad.Left => Action.Left,
        Pad.Right => Action.Right,
        Pad.A => playing ? Action.PlayPause : Action.Select,
        Pad.B or Pad.View => Action.Back,
        Pad.Menu => playing ? Action.PlayPause : null,
        Pad.X => playing ? Action.Subtitles : null,
        Pad.Y => playing ? Action.Skip : Action.Search,
        Pad.LeftShoulder => playing ? Action.JumpBack : Action.PreviousPage,
        Pad.RightShoulder => playing ? Action.JumpForward : Action.NextPage,
        _ => null,
    };
}

/// <summary>A game controller's buttons (Xbox names; a PlayStation's ✕ ○ □ △ are A B X Y), the cross included.</summary>
[Flags]
public enum Pad
{
    None = 0, Up = 1, Down = 2, Left = 4, Right = 8, A = 16, B = 32, X = 64, Y = 128,
    /// <summary>☰ (Start, Options).</summary>
    Menu = 256,
    /// <summary>⧉ (Back, View, Share, Create).</summary>
    View = 512,
    LeftShoulder = 1024, RightShoulder = 2048,
}

/// <summary>A controller read at a moment: the buttons held, and the left stick from −1 to 1 (up positive).</summary>
public readonly record struct PadState(Pad Buttons, double StickX = 0, double StickY = 0);

/// <summary>
/// Turns a controller read again and again (60 times a second) into presses: a button counts when it goes down; a
/// direction (the cross, or the left stick pushed past half way) also repeats while held, as a key does, after 400 ms then
/// every 110 ms. One direction at a time: the cross before the stick, up and down before left and right.
/// </summary>
public sealed class PadReader
{
    public const double StickThreshold = .5;
    public static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400), RepeatEvery = TimeSpan.FromMilliseconds(110);
    private Pad _held;
    private Pad _direction;
    private TimeSpan _nextRepeat;

    /// <returns>The buttons pressed since the last read, in the order of <see cref="Pad"/>; a held direction again when it repeats.</returns>
    public IReadOnlyList<Pad> Read(PadState state, TimeSpan now)
    {
        var pressed = new List<Pad>();
        var direction = Direction(state);
        if (direction != _direction)
        {
            _direction = direction;
            if (direction != Pad.None) { pressed.Add(direction); _nextRepeat = now + RepeatDelay; }
        }
        else if (direction != Pad.None && now >= _nextRepeat)
        {
            pressed.Add(direction);
            // Late reads (a busy window) never pile up repeats: one, then the next a full step later.
            _nextRepeat = now + RepeatEvery;
        }
        var buttons = state.Buttons & ~(Pad.Up | Pad.Down | Pad.Left | Pad.Right);
        foreach (var button in new[] { Pad.A, Pad.B, Pad.X, Pad.Y, Pad.Menu, Pad.View, Pad.LeftShoulder, Pad.RightShoulder })
            if (buttons.HasFlag(button) && !_held.HasFlag(button)) pressed.Add(button);
        _held = buttons;
        return pressed;
    }

    /// <summary>Forgets what was held (the window left the front): a button still down when it comes back is not pressed again.</summary>
    public void Reset(PadState state) { _held = state.Buttons & ~(Pad.Up | Pad.Down | Pad.Left | Pad.Right); _direction = Direction(state); _nextRepeat = TimeSpan.MaxValue; }

    private static Pad Direction(PadState state)
    {
        foreach (var way in new[] { Pad.Up, Pad.Down, Pad.Left, Pad.Right })
            if (state.Buttons.HasFlag(way)) return way;
        var (x, y) = (double.IsFinite(state.StickX) ? state.StickX : 0, double.IsFinite(state.StickY) ? state.StickY : 0);
        if (Math.Max(Math.Abs(x), Math.Abs(y)) < StickThreshold) return Pad.None;
        return Math.Abs(y) >= Math.Abs(x) ? (y > 0 ? Pad.Up : Pad.Down) : (x > 0 ? Pad.Right : Pad.Left);
    }
}
