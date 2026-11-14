using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// Jellyfin not answering, as on Windows (RefreshAsync of MainWindow.xaml.cs, ShowOffline of MainWindow.Accessibility.cs):
// what the pages had kept stays on screen, said once as Jellyfin stops answering rather than at each page or each try,
// then « Hors connexion » at the top of the window, with « Réessayer », until it answers again: « Jellyfin répond de
// nouveau. ». While Jellyfin's WebSocket is closed, the page shown is asked again every 45 s (every 5 min while it is
// open: a change it did not say), as Windows' fallback refresh (Cadence).
public sealed partial class MainWindow
{
    /// <summary>When a page last failed to reach Jellyfin; null once one reached it again (<see cref="SyncStatus"/>).</summary>
    private DateTimeOffset? _offlineSince;
    /// <summary>When the last page that reached Jellyfin was asked (<see cref="Cadence.RefreshDue"/>).</summary>
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private readonly DispatcherTimer _fallbackRefresh = new() { Interval = Cadence.RefreshUnheard };
    private Border _offlineBadge = null!;
    private readonly TextBlock _offlineText = new() { FontSize = 12, Foreground = Ui.WarningText, VerticalAlignment = VerticalAlignment.Center };
    private Button _offlineRetry = null!;
    private bool _offlineShown;
    /// <summary>The notice that said Jellyfin stopped answering: « Jellyfin répond de nouveau. » takes its place.</summary>
    private string? _unansweredSaid;
    /// <summary>The status line changed (Réglages → Compte et serveur shows it).</summary>
    public event Action? SyncShown;

    /// <summary>« Hors connexion » at the top left of the window, beside the rail, as Windows' title bar shows it.</summary>
    private Border BuildOfflineBadge()
    {
        _offlineRetry = new Button { Content = L("Réessayer", "Try again"), Classes = { "quiet" }, FontSize = 12, Padding = new Thickness(10, 3), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_offlineRetry, L("Réessayer de joindre Jellyfin", "Try reaching Jellyfin again"));
        _offlineRetry.Click += async (_, _) => await RetryJellyfinAsync(saysStill: true);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Ui.Warning, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(_offlineText); row.Children.Add(_offlineRetry);
        var badge = new Border
        {
            IsVisible = false, Opacity = 0, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            // In the strip of the title bar, beside the traffic lights' rail; below the window's top elsewhere (Linux).
            Margin = new Thickness(RailWidth + 8, TitleBarInset > 0 ? 2 : 8, 0, 0), Padding = new Thickness(11, 2, 3, 2),
            CornerRadius = new CornerRadius(12), Background = Ui.Brush("#F21C1A16"), BorderBrush = Ui.Brush("#4D8C7650"), BorderThickness = new Thickness(1),
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) }],
            Child = row
        };
        ToolTip.SetTip(badge, L("Jellyfin ne répond pas : Mira montre ce qu’il avait gardé et réessaie tout seul.", "Jellyfin isn’t answering: Mira shows what it had kept and tries again on its own."));
        Grid.SetColumnSpan(badge, 2);
        _fallbackRefresh.Tick += async (_, _) => await FallbackRefreshAsync();
        return badge;
    }

    /// <summary>A new account's session: nothing known of it yet, its reports' line followed.</summary>
    private void FollowSession(Session session)
    {
        _offlineSince = null; _lastRefresh = DateTimeOffset.MinValue; _unansweredSaid = null;
        session.Sync.Changed += () => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(session, Session)) SyncChanged(); });
        _fallbackRefresh.Start();
        SyncChanged();
    }
    /// <summary>The session closed: no « Hors connexion » left over, nothing said of it.</summary>
    private void ForgetUnanswered()
    {
        _fallbackRefresh.Stop();
        _offlineSince = null; _lastRefresh = DateTimeOffset.MinValue; _unansweredSaid = null;
        _offlineShown = false; _offlineBadge.IsVisible = false; _offlineBadge.Opacity = 0;
        SyncShown?.Invoke();
    }

    /// <summary>
    /// No answer from Jellyfin (no connection, too slow, « Tailscale intégré » that could not reach its PC), as opposed to
    /// an answer that is an error (a refusal, a title gone, Jellyfin starting).
    /// </summary>
    internal static bool NoAnswer(Exception error) => error is HttpRequestException { StatusCode: null } or OperationCanceledException or TimeoutException;
    /// <summary>
    /// Jellyfin answered a page asked at <paramref name="started"/>: no longer offline (« Jellyfin répond de nouveau. » if
    /// it was), and the reports a failure left waiting go now rather than at their next try.
    /// </summary>
    internal void Reached(DateTimeOffset started)
    {
        _lastRefresh = started; _unanswered = false;
        if (Session is not { } session) return;
        _offlineSince = null;
        if (session.Sync.PendingCount > 0) session.Sync.Wake();
        SyncChanged();
    }
    /// <summary>
    /// A page could not reach Jellyfin: what it had kept stays on screen. Said once as Jellyfin stops answering, not at each
    /// page or try; « Hors connexion » then stays at the top until it answers again. False for an answer that is an error,
    /// which the caller says.
    /// </summary>
    internal bool Unreached(Exception error)
    {
        if (!NoAnswer(error) || Session is not { } session) return false;
        _unanswered = true;
        var wasOffline = SyncStatus.Offline(session.Sync.LastSynced, _offlineSince);
        _offlineSince = DateTimeOffset.Now;
        if (!wasOffline && !InParty)
        {
            // Through « Tailscale intégré »: why, with what helps (its PC off, the node signed out).
            if (Tailnet.Says(session.Connection.Server, error) is { } said) { _unansweredSaid = said.Text; Notice(said.Text, said.Action, said.Act, sticky: said.Action is not null); }
            else Notice(_unansweredSaid = Errors.Friendly(error) + L(" Les informations déjà chargées restent disponibles.", " What’s already loaded stays available."), tone: NoticeTone.Error);
        }
        SyncChanged();
        return true;
    }

    /// <summary>The reports' line: offline first, neither a delivered report nor the retry loop replaces it before Jellyfin answers.</summary>
    public (string Text, bool Warning)? SyncLine => Session is { } session && !InParty
        ? SyncStatus.Describe(session.Sync.PendingCount, session.Sync.Error, session.Sync.LastSynced, _offlineSince) : null;
    private void SyncChanged()
    {
        var line = SyncLine;
        ShowOffline(line is not null && Session is { } session && SyncStatus.Offline(session.Sync.LastSynced, _offlineSince), line?.Text ?? "");
        SyncShown?.Invoke();
    }
    /// <param name="status">The status line, whose words the badge takes: « Hors connexion · 2 envois en attente ».</param>
    private void ShowOffline(bool offline, string status)
    {
        if (offline && status.TrimStart('○', ' ') is var text && _offlineText.Text != text) _offlineText.Text = text;
        if (offline == _offlineShown) return;
        _offlineShown = offline;
        if (offline) { _offlineBadge.IsVisible = true; _offlineBadge.Opacity = 1; return; }
        _offlineBadge.Opacity = 0;
        DispatcherTimer.RunOnce(() => { if (!_offlineShown) _offlineBadge.IsVisible = false; }, TimeSpan.FromMilliseconds(220));
        // The other half of what was said when it stopped answering: in its place, or under a message still waiting for you.
        var back = L("Jellyfin répond de nouveau.", "Jellyfin is answering again.");
        if (NoticeText is { } shown && shown != _unansweredSaid && _toastSticky) AddNotice(back);
        else Notice(back);
        _unansweredSaid = null;
    }

    /// <summary>
    /// « Réessayer », and the fallback refresh while offline: Jellyfin asked whether it answers, whatever page shows (the
    /// titles kept on this Mac, a page that does not ask Jellyfin), then the page shown asked again.
    /// </summary>
    private async Task RetryJellyfinAsync(bool saysStill)
    {
        if (Session is not { } session) return;
        _offlineRetry.IsEnabled = false;
        try
        {
            var started = DateTimeOffset.UtcNow;
            bool answers;
            try { answers = await session.Client.PingAsync(session.Lifetime); }
            catch (OperationCanceledException) { answers = false; }
            if (!ReferenceEquals(session, Session)) return;
            if (answers) { Reached(started); await RefreshPagesAsync(fromServer: !saysStill); }
            else _offlineSince = DateTimeOffset.Now;
        }
        finally { _offlineRetry.IsEnabled = true; }
        if (saysStill && ReferenceEquals(session, Session) && SyncStatus.Offline(session.Sync.LastSynced, _offlineSince))
            Notice(L("Jellyfin ne répond toujours pas. Mira réessaie tout seul et te le dira dès qu’il répond.", "Jellyfin still isn’t answering. Mira keeps trying on its own and will tell you as soon as it answers."), tone: NoticeTone.Error);
    }
    /// <summary>
    /// Every 45 s while Jellyfin's WebSocket is closed (every 5 min while it is open), as on Windows: the page shown asked
    /// again, never while something plays, Mira away or behind another window, nor during the checks (their timing is theirs).
    /// </summary>
    private async Task FallbackRefreshAsync()
    {
        if (Session is null || InParty || Player is not null || !IsActive || Away || IsCheckRun) return;
        if (!Cadence.RefreshDue(_listening, _lastRefresh, DateTimeOffset.UtcNow)) return;
        if (_offlineShown) await RetryJellyfinAsync(saysStill: false);
        else await RefreshPagesAsync(fromServer: true);
    }
    /// <summary>
    /// A page asked again in the background (gone back to, the window over it closed): no answer from Jellyfin said once
    /// (<see cref="Unreached"/>); another error said only when <paramref name="say"/>.
    /// </summary>
    private async Task RefreshPageAsync(Page page, bool say)
    {
        try { await page.RefreshAsync(); }
        catch (Exception ex) when (Errors.Expected(ex)) { if (!Unreached(ex) && say) Say(ex); }
    }
    /// <summary>« Synchroniser maintenant » (Réglages → Compte et serveur), as on Windows: the reports waiting sent, Jellyfin asked again.</summary>
    public async Task SyncNowAsync()
    {
        if (Session is not { } session) return;
        session.Sync.Wake();
        if (_offlineShown) { await RetryJellyfinAsync(saysStill: true); return; }
        session.Forget();
        if (_underSheet is { } page) await RefreshPageAsync(page, say: true);
        else await RetryJellyfinAsync(saysStill: true);
    }

    /// <summary>Jellyfin does not answer (the badge shows): « Réessayer » in Réglages, the account's card of « L’essentiel ».</summary>
    public bool NotAnswering => _offlineShown;
    /// <summary>For the self-check: the badge's words while it shows.</summary>
    internal string? OfflineBadgeText => _offlineShown ? _offlineText.Text : null;
    /// <summary>For the self-check: « Réessayer » pressed.</summary>
    internal Task RetryForCheck() => RetryJellyfinAsync(saysStill: true);
}
