using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
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
        var screen = WhatsNew.Decide(Settings.SeenVersion, current, knownProfile: !Profile.IsNew);
        if (Settings.SeenVersion != current.ToString(3))
        {
            Settings.SeenVersion = current.ToString(3);
            // Not written now (a full disk): the opening goes on, and quitting Mira writes it again.
            try { Profile.SaveSettings(Settings); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (screen == StartupScreen.WhatsNew) ShowWhatsNew(WhatsNew.Recent(current));
    }

    /// <summary>
    /// « Nouveautés », as on Windows (MainWindow.Guide.cs): the last versions (WhatsNew.Recent), each its version,
    /// « LATEST » on the newest, its date, then one line per change, « Catégorie : texte ».
    /// </summary>
    public void ShowWhatsNew(IReadOnlyList<ReleaseHighlights> releases)
    {
        if (releases.Count == 0) return;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var content = new DockPanel();
        Sheet? sheet = null;
        var close = Ui.IconButton("close", L("Fermer · Échap", "Close · Esc"), () => CloseSheet(sheet!), size: 17, name: L("Fermer les nouveautés", "Close what’s new"));
        close.VerticalAlignment = VerticalAlignment.Top;
        var head = new DockPanel { Margin = new Thickness(38, 32, 26, 22) };
        DockPanel.SetDock(close, Dock.Right); head.Children.Add(close);
        var heading = Ui.Heading(L("Nouveautés", "What’s new"), 32, 1, FontWeight.ExtraBold);
        head.Children.Add(Ui.Column(4, heading, new TextBlock { Text = L("Chaque mise à jour de Mira, la plus récente en haut", "Every Mira update, newest first"), FontSize = 14, Foreground = Ui.Muted }));
        DockPanel.SetDock(head, Dock.Top); content.Children.Add(head);
        var done = Ui.Action(L("C’est parti", "Let’s go"), null, "primary", () => CloseSheet(sheet!));
        done.Padding = new Thickness(26, 11);
        var github = Ui.Action(L("Voir sur GitHub", "View on GitHub"), null, "quiet", () => Updates.Open(new Uri("https://github.com/sasou-web/Mira/releases")));
        github.Margin = new Thickness(0, 0, 10, 0);
        var footRow = new DockPanel();
        DockPanel.SetDock(done, Dock.Right); footRow.Children.Add(done);
        DockPanel.SetDock(github, Dock.Right); footRow.Children.Add(github);
        footRow.Children.Add(new TextBlock { Text = releases.Count == 1 ? L("La dernière version", "Showing 1 release") : L($"Les {releases.Count} dernières versions", $"Showing {releases.Count} releases"), FontSize = 13, Foreground = Ui.Faint, VerticalAlignment = VerticalAlignment.Center });
        var foot = new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(38, 18, 38, 22), Child = footRow };
        DockPanel.SetDock(foot, Dock.Bottom); content.Children.Add(foot);
        var rule = new Border { Height = 1, Background = Line };
        DockPanel.SetDock(rule, Dock.Top); content.Children.Add(rule);
        var list = new StackPanel { Margin = new Thickness(38, 0, 38, 4) };
        for (var i = 0; i < releases.Count; i++) list.Children.Add(ReleaseBlock(releases[i], latest: i == 0, last: i == releases.Count - 1, today));
        content.Children.Add(new ScrollViewer { MaxHeight = 540, Content = list });
        sheet = new Sheet(content, 720) { FirstFocus = done };
        WhatsNewShown = true;
        ShowSheet(sheet);
    }
    private static readonly IBrush Line = Ui.Brush("#2A2A2A"), LineStrong = Ui.Brush("#3F3F3F"), Raised2 = Ui.Brush("#242424"), OnAccent = Ui.Brush("#121212"),
        TextSecondary = Ui.Brush("#C7C7C7"), Success = Ui.Brush("#86C6A2"), SuccessSurface = Ui.Brush("#1F86C6A2"), SuccessLine = Ui.Brush("#5986C6A2");
    private static Control ReleaseBlock(ReleaseHighlights release, bool latest, bool last, DateOnly today)
    {
        var block = new StackPanel { Margin = new Thickness(0, 26, 0, 24) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        if (release.Date is { } date)
        {
            var when = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            when.Children.Add(new Icon("calendar", 15) { Foreground = Ui.Faint, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            when.Children.Add(new TextBlock { Text = WhatsNew.Day(date), FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Ui.Muted });
            when.Children.Add(new TextBlock { Text = " · " + WhatsNew.Ago(date, today), FontSize = 13, Foreground = Ui.Faint });
            DockPanel.SetDock(when, Dock.Right); head.Children.Add(when);
        }
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        badges.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(7), Padding = new Thickness(11, 5), Background = latest ? Ui.Ink : Raised2, BorderBrush = latest ? Ui.Ink : LineStrong, BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = release.Version, FontSize = 13, FontWeight = FontWeight.ExtraBold, Foreground = latest ? OnAccent : Ui.Ink },
        });
        if (latest)
            badges.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 5), Background = SuccessSurface, BorderBrush = SuccessLine, BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = L("DERNIÈRE", "LATEST"), FontSize = 11, FontWeight = FontWeight.ExtraBold, Foreground = Success, VerticalAlignment = VerticalAlignment.Center },
            });
        head.Children.Add(badges);
        block.Children.Add(head);
        foreach (var line in release.Lines)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4) };
            var dot = new Ellipse { Width = 6, Height = 6, Fill = Ui.Ink, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(3, 9, 11, 0) };
            DockPanel.SetDock(dot, Dock.Left); row.Children.Add(dot);
            var text = new TextBlock { FontSize = 15, LineHeight = 22, TextWrapping = TextWrapping.Wrap, Foreground = TextSecondary, Inlines = [] };
            text.Inlines!.Add(new Run(line.Category + L(" : ", ": ")) { FontWeight = FontWeight.Bold, Foreground = Ui.Ink });
            text.Inlines.Add(new Run(line.Text));
            row.Children.Add(text);
            block.Children.Add(row);
        }
        return last ? block : new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = block };
    }
}
