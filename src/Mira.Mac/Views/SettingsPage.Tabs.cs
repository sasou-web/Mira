using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// The settings in categories, as on Windows: a list on the left, one category at a time on the right, the one last
// seen opened again.
public sealed partial class SettingsPage
{
    private static IReadOnlyList<(string Key, string Title)> Tabs =>
    [
        ("playback", L("Lecture", "Playback")),
        ("audio", L("Audio et sous-titres", "Audio & subtitles")),
        ("appearance", L("Apparence", "Appearance")),
        ("server", "Jellyfin"),
        ("sharing", L("Amis et invités", "Friends & guests")),
        ("care", L("Entretien", "Maintenance")),
        ("report", L("Ton bilan", "Your recap")),
        ("updates", L("Mises à jour", "Updates")),
    ];
    private static string _lastTab = "playback";
    private readonly Dictionary<string, StackPanel> _tabs = [];
    private readonly Dictionary<string, Button> _tabButtons = [];
    private readonly ScrollViewer _tabScroll = new();

    /// <summary>The category shown: « playback », « audio », « appearance », « server », « sharing », « care », « report » or « updates ».</summary>
    public string CurrentTab { get; private set; } = _lastTab;

    private Control Layout(MainWindow shell)
    {
        var list = new StackPanel { Spacing = 4, Width = 220, Margin = new Thickness(0, 0, 36, 0) };
        foreach (var (key, title) in Tabs)
        {
            var button = new Button { Content = title, Classes = { "quiet" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 11), FontWeight = FontWeight.Normal, FontSize = 14 };
            button.Click += (_, _) => Show(key);
            _tabButtons[key] = button; list.Children.Add(button);
        }
        // « Amis et invités » and « Entretien » hold only what an administrator sees: listed while one of their sections shows.
        foreach (var key in new[] { "sharing", "care" })
        {
            void Listed() => _tabButtons[key].IsVisible = _tabs[key].Children.Any(x => x.IsVisible);
            foreach (var section in _tabs[key].Children) section.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) Listed(); };
            Listed();
        }
        list.Children.Add(new TextBlock { Text = $"Mira {Mira.Mac.Services.Updates.Current} · Jellyfin + mpv", FontSize = 11, Foreground = Ui.Faint, Margin = new Thickness(16, 20, 0, 0) });
        var body = new DockPanel { Margin = new Thickness(56, 44 + shell.TitleBarInset, 56, 0) };
        var heading = Ui.Column(4, Ui.Heading(L("Réglages", "Settings"), 32, 1, FontWeight.SemiBold),
            new TextBlock { Text = L("Une lecture qui te ressemble.", "Playback that feels like yours."), FontSize = 14, Foreground = Ui.Muted });
        heading.Margin = new Thickness(0, 0, 0, 30);
        DockPanel.SetDock(heading, Dock.Top); body.Children.Add(heading);
        DockPanel.SetDock(list, Dock.Left); body.Children.Add(list);
        body.Children.Add(_tabScroll);
        Show(_tabs.ContainsKey(_lastTab) && _tabButtons[_lastTab].IsVisible ? _lastTab : "playback");
        return body;
    }
    /// <summary>The category the next Réglages opens on (a notice that names a setting).</summary>
    public static void OpenAt(string key) { if (Tabs.Any(x => x.Key == key)) _lastTab = key; }
    public void Show(string key)
    {
        if (!_tabs.TryGetValue(key, out var tab)) return;
        CurrentTab = _lastTab = key;
        foreach (var (name, button) in _tabButtons)
        {
            button.Classes.Set("on", name == key);
            button.Classes.Set("quiet", name != key);
            Avalonia.Automation.AutomationProperties.SetName(button, name == key ? L($"{button.Content}, affiché", $"{button.Content}, shown") : button.Content as string);
        }
        tab.MaxWidth = 860; tab.HorizontalAlignment = HorizontalAlignment.Left; tab.Margin = new Thickness(0, 0, 0, 60);
        _tabScroll.Content = tab;
        _tabScroll.Offset = default;
    }
}
