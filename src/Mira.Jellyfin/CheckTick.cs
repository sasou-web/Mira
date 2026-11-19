namespace Mira.Jellyfin;

/// <summary>
/// The period of the extension's own loops (<see cref="SubtitleAutomation"/>, <see cref="TvdbOrders"/>), shortened for
/// the checks only: web.yml starts Jellyfin with MIRA_CHECK_TICK_SECONDS so that each loop runs within the minutes of a
/// check, not after 10 of them. Never set anywhere else: without it, each loop keeps its own period.
/// </summary>
internal static class CheckTick
{
    public static TimeSpan Or(TimeSpan period) =>
        int.TryParse(Environment.GetEnvironmentVariable("MIRA_CHECK_TICK_SECONDS"), out var seconds) && seconds is > 0 and < 600 ? TimeSpan.FromSeconds(seconds) : period;
}
