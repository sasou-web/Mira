using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Mira.Core;
using Mira.Core.Family;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Sécurité », as on Windows (MainWindow.Security.cs there; ogsas, 2026-11-20, maquette A « La page Security » with the
/// steps of C): the accounts and devices read once for Réglages, « L’essentiel » and the list's dot, and the card each
/// question of the page is asked on.
/// </summary>
public sealed partial class MainWindow
{
    private SecurityReport? _security;
    private Session? _securityRead;
    private int _securityDraw;
    /// <summary>Why the last read failed, in words; empty when it did not.</summary>
    internal string SecurityProblem { get; private set; } = "";
    /// <summary>Told again each time the accounts and devices are read.</summary>
    internal event Action? SecurityChanged;

    /// <summary>What was read, for the session open now only.</summary>
    internal SecurityReport? Security => ReferenceEquals(_securityRead, Session) ? _security : null;

    /// <summary>The accounts and devices read again for this administrator; <paramref name="again"/>: even when already read.</summary>
    internal async Task ReadSecurityAsync(bool again = false)
    {
        if (InParty || Session is not { } session) { ++_securityDraw; _security = null; _securityRead = null; SecurityChanged?.Invoke(); return; }
        if (!again && ReferenceEquals(session, _securityRead)) { SecurityChanged?.Invoke(); return; }
        var draw = ++_securityDraw;
        try
        {
            var report = await session.Client.IsAdministratorAsync(session.Lifetime) == true ? await session.Client.SecurityAsync(session.Lifetime) : null;
            // « Your server »: Jellyfin's access from the whole Internet (the firewall's public Wi-Fi is Windows' only).
            if (report is not null)
            {
                InternetOpening? internet = null;
                try { internet = await session.Client.InternetOpeningAsync(session.Lifetime); }
                catch (Exception ex) when (Errors.Expected(ex)) { }
                report = report with { Server = new(internet, null) };
            }
            if (draw != _securityDraw || !ReferenceEquals(session, Session)) return;
            _security = report; _securityRead = session; SecurityProblem = "";
        }
        catch (Exception ex) when (Errors.Expected(ex))
        {
            if (draw != _securityDraw || !ReferenceEquals(session, Session)) return;
            SecurityProblem = Errors.Friendly(ex);
        }
        SecurityChanged?.Invoke();
    }

    internal bool SecurityQuiet(string key) => AccountSecurity.Quiet(Settings.SecurityQuiet, key, DateTimeOffset.UtcNow);

    /// <summary>« Passer » or « Oui, je le garde »: not asked again, nor counted, for 30 days.</summary>
    internal void QuietSecurity(string key)
    {
        AccountSecurity.Forget(Settings.SecurityQuiet, DateTimeOffset.UtcNow);
        Settings.SecurityQuiet[key] = DateTimeOffset.UtcNow;
        Profile.SaveSettings(Settings);
    }

    /// <summary>This account's new password length kept with its session on this Mac, so « Sécurité » knows it next time too.</summary>
    internal async Task KeepPasswordLengthAsync(Connection connection)
    {
        try
        {
            await Task.Run(() =>
            {
                if (Profile.LoadConnection() is { } saved && saved.UserId == connection.UserId && saved.Token == connection.Token)
                    Profile.SaveConnection(saved with { PasswordLength = connection.PasswordLength });
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private TaskCompletionSource<int>? _card;

    /// <summary>
    /// A question on Mira's card with its own content (<paramref name="body"/>) and its buttons, left to right: the answer
    /// of the one pressed, -1 for Escape. <paramref name="act"/> runs with the card still open: false keeps it open (a
    /// wrong password said in the body). <paramref name="step"/> and <paramref name="progress"/>: « Tout régler… »'s step.
    /// As Windows' CardAsync: with three buttons, the first one on the left.
    /// </summary>
    internal Task<int> CardAsync(string title, string note, Control? body, IReadOnlyList<(string Label, string? Icon, string Classes, int Answer)> buttons,
        Func<int, Task<bool>>? act = null, string? step = null, double progress = 0, double width = 520)
    {
        if (_card is not null) return Task.FromResult(-1);
        var asked = _card = new TaskCompletionSource<int>();
        Sheet? sheet = null;
        var busy = false;
        void Close(int answer)
        {
            if (_card != asked) return;
            _card = null;
            if (sheet is not null) CloseSheet(sheet);
            asked.TrySetResult(answer);
        }
        var card = new StackPanel { Margin = new Thickness(34, 30) };
        if (step is not null)
        {
            card.Children.Add(new TextBlock { Text = step, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.Muted });
            var track = new Grid { Height = 3, Margin = new Thickness(0, 8, 0, 14) };
            track.Children.Add(new Border { Background = Ui.Brush("#2A2A2A"), CornerRadius = new CornerRadius(1.5) });
            track.Children.Add(new Border { Background = Ui.Ink, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Clamp(progress, 0, 1) * (width - 68) });
            card.Children.Add(track);
        }
        var heading = Ui.Heading(title, 24, 1);
        heading.TextWrapping = TextWrapping.Wrap;
        card.Children.Add(heading);
        if (note.Length > 0) card.Children.Add(new TextBlock { Text = note, FontSize = 14, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        if (body is not null) { body.Margin = new Thickness(0, 18, 0, 0); card.Children.Add(body); }
        var row = new DockPanel { Margin = new Thickness(0, 24, 0, 0), LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        DockPanel.SetDock(left, Dock.Left); DockPanel.SetDock(right, Dock.Right); row.Children.Add(left); row.Children.Add(right);
        Button? safe = null;
        for (var i = 0; i < buttons.Count; i++)
        {
            var (label, icon, classes, answer) = buttons[i];
            var button = Ui.Action(label, icon, classes, () => { });
            button.Padding = new Thickness(18, 9);
            button.Click += async (_, _) =>
            {
                if (busy || _card != asked) return;
                if (act is not null)
                {
                    busy = true; row.IsEnabled = false; if (sheet is not null) sheet.Busy = true;
                    bool done;
                    try { done = await act(answer); }
                    finally { busy = false; row.IsEnabled = true; if (sheet is not null) sheet.Busy = false; }
                    if (!done) return;
                }
                Close(answer);
            };
            (buttons.Count > 2 && i == 0 ? left : right).Children.Add(button);
            if (!classes.Contains("primary", StringComparison.Ordinal)) safe ??= button;
        }
        card.Children.Add(row);
        sheet = new Sheet(card, width) { FirstFocus = (Control?)FirstField(body) ?? safe };
        sheet.Closed += () => Close(-1);
        ShowSheet(sheet);
        return asked.Task;

        static TextBox? FirstField(Control? root) => root?.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(x => x.IsVisible);
    }
}
