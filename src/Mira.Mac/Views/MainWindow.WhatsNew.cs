using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mira.Core;
using Mira.Core.Updates;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// Sheets over the window, and « Nouveautés » after an update, as on Windows (MainWindow.Guide.cs): the main points of
// the versions installed since the last one this profile saw.
public sealed partial class MainWindow
{
    private readonly Panel _sheets = new();
    /// <summary>The sheet shown, if any: it has the keys, Escape closes it.</summary>
    public Sheet? CurrentSheet => _sheets.Children.OfType<Sheet>().LastOrDefault();
    /// <summary>« Nouveautés » shown at this opening: the update's own notice then says nothing more.</summary>
    public bool WhatsNewShown { get; private set; }

    /// <summary>Over the window, in place of the one shown; the focus on its first button.</summary>
    public void ShowSheet(Sheet sheet)
    {
        foreach (var shown in _sheets.Children.OfType<Sheet>().ToList()) CloseSheet(shown);
        // As on Windows, the focus goes back where it was once the sheet closes (the button that opened it).
        _sheetOrigin = FocusManager?.GetFocusedElement() as Control;
        sheet.Opacity = 0;
        sheet.Transitions = [new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) }];
        _sheets.Children.Add(sheet);
        Dispatcher.UIThread.Post(() => { sheet.Opacity = 1; sheet.FirstFocus?.Focus(); sheet.Opened(); }, DispatcherPriority.Input);
    }
    public void CloseSheet(Sheet sheet)
    {
        if (!_sheets.Children.Remove(sheet)) return;
        sheet.Close();
        // Back to the control that opened it, else to the page: the focus would otherwise stay on a control no longer there.
        if (CurrentSheet is not null) return;
        if (_sheetOrigin is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } origin && origin.GetVisualRoot() == this) origin.Focus(NavigationMethod.Tab);
        else (Current as Control)?.Focus();
        _sheetOrigin = null;
    }
    private Control? _sheetOrigin;

    /// <summary>On opening: the main points of what this profile has not seen yet, once; a new profile gets nothing.</summary>
    private void ShowStartupScreen()
    {
        var current = Updates.Current;
        var seen = ReleaseFeed.ParseVersion(Settings.SeenVersion);
        var screen = WhatsNew.Decide(Settings.SeenVersion, current, knownProfile: !Profile.IsNew);
        if (Settings.SeenVersion != current.ToString(3))
        {
            Settings.SeenVersion = current.ToString(3);
            // Not written now (a full disk): the opening goes on, and quitting Mira writes it again.
            try { Profile.SaveSettings(Settings); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (screen == StartupScreen.WhatsNew) ShowWhatsNew(WhatsNew.Since(seen, current));
    }

    /// <summary>The latest version's points, with their icons; the versions skipped by this update in a line each.</summary>
    public void ShowWhatsNew(IReadOnlyList<ReleaseHighlights> releases)
    {
        if (releases.Count == 0) return;
        var latest = releases[0];
        var sheet = new Sheet(L("NOUVEAUTÉS", "WHAT’S NEW"), "sparkle", $"Mira {latest.Version}", latest.Summary);
        var items = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
        foreach (var item in latest.Items) items.Children.Add(HighlightRow(item));
        foreach (var release in releases.Skip(1).Take(2))
            items.Children.Add(new TextBlock { Text = L($"Aussi en {release.Version} : {release.Summary}", $"Also in {release.Version}: {release.Summary}"), FontSize = 12, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) });
        sheet.Body.Children.Add(items);
        var done = Ui.Action(L("C’est parti", "Let’s go"), null, "primary", () => CloseSheet(sheet));
        var details = Ui.Action(L("Tous les détails", "All the details"), null, "quiet", () => Updates.Open(new Uri($"https://github.com/sasou-web/Mira/releases/tag/v{latest.Version}")));
        sheet.Buttons(done, details);
        WhatsNewShown = true;
        ShowSheet(sheet);
    }
    private static Control HighlightRow(Highlight item)
    {
        var badge = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(11), Background = Ui.Raised, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 16, 0),
            Child = new Icon(Views.Icon.Draws(item.Icon) ? item.Icon : "sparkle", 20) { Foreground = Ui.Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(badge, Dock.Left); row.Children.Add(badge);
        row.Children.Add(Ui.Column(4,
            new TextBlock { Text = item.Title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = item.Text, FontSize = 12.5, LineHeight = 19, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap }));
        return row;
    }
}
