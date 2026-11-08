using System.Runtime.InteropServices;
using Mira.Core;

namespace Mira.Mac.Views;

// « Tu regardes toujours ? » (StillWatching of Mira.Core, as on Windows and the iPhone): three episodes in a row with
// nobody touching the Mac, and the next one waits for an answer on the player's screen instead of starting by itself.
public sealed partial class PlayerPage
{
    private readonly StillWatching _stillWatching = new();
    /// <summary>The next <see cref="StartAsync"/> is the countdown's or the end of an episode's, not a press.</summary>
    private bool _nextByItself;

    /// <summary>A controller read by Mira: someone is there, though macOS saw no input.</summary>
    public void Touched() => _stillWatching.Touched(DateTimeOffset.Now);

    /// <summary>The next episode would start by itself: true when it should ask instead. Never in a group.</summary>
    private bool AsksStillWatching() => !Group.Active && _stillWatching.Asks(LastInput(), _shell.Settings.StillWatching);

    /// <summary>The episode ended, watched, and <paramref name="next"/> waits for « Continuer ».</summary>
    private async Task AskStillWatchingAsync(MediaItem next)
    {
        await StopAsync(completed: true);
        var reason = StillWatching.Reason(PlayerText.Subtitle(next));
        await ShowScreenAsync(StillWatching.Question, "eye", reason, next, StillWatching.Continue, () => _ = ContinueAsync(next), StillWatching.Question + " " + reason, NoticeTone.Info);
    }
    private async Task ContinueAsync(MediaItem next)
    {
        if (_shell.Player != this) { await _shell.PlayAsync(next); return; }
        HideFailure(); ShowControls();
        if (!await StartAsync(next)) await LeaveFailureAsync();
    }

    /// <summary>
    /// When the Mac last had a keyboard, mouse or trackpad input (CGEventSource, the whole session); now when it cannot be
    /// read (Linux, where the self-check runs): nobody is taken for asleep on a guess.
    /// </summary>
    private static DateTimeOffset LastInput()
    {
        if (!OperatingSystem.IsMacOS()) return DateTimeOffset.Now;
        try
        {
            var seconds = CGEventSourceSecondsSinceLastEventType(CombinedSessionState, AnyInputEvent);
            return double.IsFinite(seconds) && seconds >= 0 ? DateTimeOffset.Now.AddSeconds(-seconds) : DateTimeOffset.Now;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return DateTimeOffset.Now; }
    }
    private const int CombinedSessionState = 0;
    private const uint AnyInputEvent = uint.MaxValue;
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern double CGEventSourceSecondsSinceLastEventType(int stateId, uint eventType);
}
