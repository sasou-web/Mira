using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// The settings in sections, as on Windows: « Réglages » heads the list on the left, each section with its icon, what
// only the server's owner sees under « Ton serveur »; the section open heads the page, said in one line, the one last
// seen opened again.
public sealed partial class SettingsPage
{
    private static IReadOnlyList<(string Key, string Title, string Icon, string About)> Tabs =>
    [
        ("playback", L("Lecture", "Playback"), "play", L("Ce qui compte pendant que tu regardes.", "What matters while you watch.")),
        ("audio", L("Audio & sous-titres", "Audio & subtitles"), "subtitles", L("Les bonnes langues dès le premier épisode, et des sous-titres lisibles.", "The right languages from the very first episode, and readable subtitles.")),
        ("appearance", L("Apparence", "Appearance"), "sparkle", L("La langue de Mira et ce que montre l’accueil.", "Mira’s language and what Home shows.")),
        ("server", L("Compte & serveur", "Account & server"), "server", L("Ton compte, ton serveur Jellyfin et le chemin jusqu’à lui.", "Your account, your Jellyfin server and the way to it.")),
        ("updates", L("Mira sur ce Mac", "Mira on this Mac"), "refresh", L("Mises à jour, titres gardés sur ce Mac et rapport pour signaler un problème.", "Updates, titles kept on this Mac and a report for a problem.")),
        ("report", L("Ton bilan", "Your recap"), "clock", L("Ce que tu as regardé, sur tous tes appareils.", "What you’ve watched, on all your devices.")),
        ("sharing", L("Amis & invités", "Friends & guests"), "users", L("Un compte pour un ami, un lien pour un invité, un appareil à autoriser.", "An account for a friend, a link for a guest, a device to let in.")),
        ("care", L("Entretien", "Maintenance"), "sliders", L("Ce qui garde ta bibliothèque en forme : titres à corriger, sous-titres.", "What keeps your library in shape: titles to fix, subtitles.")),
    ];
    /// <summary>The sections only the server's owner has something in, listed under « Ton serveur ».</summary>
    private static readonly string[] ServerTools = ["sharing", "care"];
    private static string _lastTab = "playback";
    private readonly Dictionary<string, StackPanel> _tabs = [];
    private readonly Dictionary<string, Button> _tabButtons = [];
    private readonly ScrollViewer _tabScroll = new();
    private readonly ContentControl _tabBody = new();
    private readonly TextBlock _tabTitle = new() { FontSize = 28, FontWeight = FontWeight.Bold };
    private readonly TextBlock _tabAbout = new() { FontSize = 14.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 26) };

    /// <summary>The section shown: « playback », « audio », « appearance », « server », « updates », « report », « sharing » or « care ».</summary>
    public string CurrentTab { get; private set; } = _lastTab;

    private Control Layout(MainWindow shell)
    {
        var list = new StackPanel { Spacing = 6 };
        var serverLabel = new TextBlock { Text = L("Ton serveur", "Your server"), FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.Faint, Margin = new Thickness(17, 16, 0, 2) };
        Avalonia.Automation.AutomationProperties.SetHeadingLevel(serverLabel, 2);
        foreach (var (key, title, icon, _) in Tabs)
        {
            if (key == ServerTools[0]) list.Children.Add(serverLabel);
            var words = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
            var button = new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 13, Children = { new Icon(icon, 19) { VerticalAlignment = VerticalAlignment.Center }, words } },
                Classes = { "quiet" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(17, 13), FontWeight = FontWeight.Normal, FontSize = 14, Tag = title,
            };
            button.Click += (_, _) => Show(key);
            _tabButtons[key] = button; list.Children.Add(button);
        }
        // « Amis & invités » and « Entretien » hold only what an administrator sees: listed while one of their sections
        // shows, and « Ton serveur » with them.
        void Listed()
        {
            foreach (var key in ServerTools) _tabButtons[key].IsVisible = _tabs[key].Children.Any(x => x.IsVisible);
            serverLabel.IsVisible = ServerTools.Any(x => _tabButtons[x].IsVisible);
        }
        foreach (var key in ServerTools)
            foreach (var section in _tabs[key].Children) section.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) Listed(); };
        Listed();

        var heading = Ui.Heading(L("Réglages", "Settings"), 30, 1);
        heading.Margin = new Thickness(4, 0, 0, 24);
        var foot = Ui.Column(10,
            new TextBlock { Text = L("Chaque changement s’applique et s’enregistre tout de suite.", "Each change applies and is saved at once."), FontSize = 12, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = $"Mira {Mira.Mac.Services.Updates.Current} · Jellyfin + mpv", FontSize = 11, Foreground = Ui.Faint });
        foot.Margin = new Thickness(12, 20, 0, 4);
        var side = new DockPanel { Width = 232, Margin = new Thickness(0, 0, 44, 0) };
        DockPanel.SetDock(heading, Dock.Top); side.Children.Add(heading);
        DockPanel.SetDock(foot, Dock.Bottom); side.Children.Add(foot);
        side.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });

        // The section open heads the page; its lines at most 940 wide, as on Windows: a description across a whole
        // screen is hard to follow.
        Avalonia.Automation.AutomationProperties.SetHeadingLevel(_tabTitle, 2);
        var page = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 8, 60), Children = { _tabTitle, _tabAbout, _tabBody } };
        _tabScroll.Content = page;
        _tabScroll.SizeChanged += (_, e) => page.Width = Math.Max(0, Math.Min(940, e.NewSize.Width - 8));
        var body = new DockPanel { Margin = new Thickness(56, 44 + shell.TitleBarInset, 40, 22) };
        DockPanel.SetDock(side, Dock.Left); body.Children.Add(side);
        body.Children.Add(_tabScroll);
        Show(_tabs.ContainsKey(_lastTab) && _tabButtons[_lastTab].IsVisible ? _lastTab : "playback");
        return body;
    }
    /// <summary>The section the next Réglages opens on (a notice that names a setting).</summary>
    public static void OpenAt(string key) { if (Tabs.Any(x => x.Key == key)) _lastTab = key; }
    public void Show(string key)
    {
        if (!_tabs.TryGetValue(key, out var tab)) return;
        CurrentTab = _lastTab = key;
        var (_, title, _, about) = Tabs.First(x => x.Key == key);
        _tabTitle.Text = title; _tabAbout.Text = about;
        foreach (var (name, button) in _tabButtons)
        {
            button.Classes.Set("on", name == key);
            button.Classes.Set("quiet", name != key);
            var words = (string)button.Tag!;
            Avalonia.Automation.AutomationProperties.SetName(button, name == key ? L($"{words}, affiché", $"{words}, shown") : words);
        }
        _tabBody.Content = tab;
        _tabScroll.Offset = default;
    }
}
