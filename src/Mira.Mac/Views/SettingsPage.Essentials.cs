using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « L’essentiel », the front page of Réglages, as on Windows (ogsas, 2026-11-14, maquette A: a friend could not find where
/// Mira updates): Mira's version and its update, the account, and six settings people change most. Each shortcut is tied
/// to the same control in its own section, so that one value shows in two places. A version ready to install puts a dot
/// on « Mises à jour » and on the rail's gear (MainWindow.BuildRail).
/// </summary>
public sealed partial class SettingsPage
{
    private ToggleSwitch _autoNext = null!, _autoSkip = null!;
    private ComboBox _quality = null!, _audioLanguage = null!, _subtitleLanguage = null!, _language = null!;
    private Button? _retry, _signOut;
    private bool _jellyfinAdmin;
    private Action? _listed;
    private readonly TextBlock _essentialsVersion = new() { FontSize = 17, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _essentialsUpdate = new() { FontSize = 12.5, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _essentialsUpdateDot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    private Button _essentialsRestart = null!, _essentialsCheck = null!, _essentialsRecap = null!;
    private readonly TextBlock _essentialsLetter = new() { FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Ui.Brush("#DEDEE4"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _essentialsPhoto = new() { Width = 44, Height = 44 };
    private readonly TextBlock _essentialsName = new() { FontSize = 17, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _essentialsServer = new() { FontSize = 12.5, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _essentialsServerDot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _essentialsSubtitles = new() { FontSize = 12.5, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };

    /// <summary>For the self-check: the shortcut of « Enchaîner les épisodes » and its own switch in « Lecture ».</summary>
    internal ToggleSwitch EssentialsAutoNext { get; private set; } = null!;
    internal ToggleSwitch AutoNextSwitch => _autoNext;
    /// <summary>For the self-check: the dot on « Mises à jour ».</summary>
    internal bool UpdateDotShown => _tabButtons["updates"].Content is DockPanel row && row.Children.OfType<Ellipse>().Any(x => x.IsVisible);

    private Control Essentials()
    {
        // —— Mira: its version, where its update stands, and its buttons ——
        // Mira's mark as the rail draws it.
        var logo = new Icon("brand", 44) { Foreground = Ui.Ink, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_essentialsUpdate, Avalonia.Automation.AutomationLiveSetting.Polite);
        _essentialsRestart = Ui.Action(L("Redémarrer et mettre à jour", "Restart and update"), null, "primary", () => Updates.Restart(Shell));
        _essentialsCheck = Ui.Action(L("Rechercher une mise à jour", "Check for updates"), "refresh", "", () => _ = Updates.CheckAsync(Shell, asked: true));
        var notes = Ui.Action(L("Notes de version", "Release notes"), null, "quiet", () => Shell.ShowWhatsNew(WhatsNew.Recent(Updates.Current)));
        var mira = Head(logo, _essentialsVersion, _essentialsUpdateDot, _essentialsUpdate, _essentialsRestart, _essentialsCheck, notes);
        Page.WhileShown(mira, shown => { if (shown) { Updates.Changed += ShowEssentialsUpdate; ShowEssentialsUpdate(); } else Updates.Changed -= ShowEssentialsUpdate; });

        // —— The account: who, on which Jellyfin, and whether it answers ——
        var face = new Grid { Width = 44, Height = 44, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top, Children = { new Ellipse { Fill = Ui.Brush("#3F3F3F") }, _essentialsLetter, _essentialsPhoto } };
        var account = Ui.Action(L("Compte & serveur", "Account & server"), "user", "", () => Go("server"));
        _essentialsRecap = Ui.Action(L("Ton bilan", "Your recap"), null, "quiet", () => Go("report"));
        var signOut = Ui.Action(L("Se déconnecter…", "Sign out…"), null, "quiet", () =>
        {
            Go("server");
            if (_signOut is { } button) Dispatcher.UIThread.Post(() => { button.BringIntoView(); button.Focus(); }, DispatcherPriority.Loaded);
        });
        Avalonia.Automation.AutomationProperties.SetHelpText(signOut, L("Ouvre la page du compte : se déconnecter ou changer de compte.", "Opens the account page: sign out or switch account."));
        var who = Head(face, _essentialsName, _essentialsServerDot, _essentialsServer, account, _essentialsRecap, signOut);
        Page.WhileShown(who, shown => { if (shown) { Shell.SyncShown += ShowEssentialsAccount; Shell.PhotoChanged += ShowEssentialsAccount; } else { Shell.SyncShown -= ShowEssentialsAccount; Shell.PhotoChanged -= ShowEssentialsAccount; } });

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
        var left = Card(mira); var right = Card(who);
        Grid.SetColumn(right, 2); top.Children.Add(left); top.Children.Add(right);

        // —— Most used: each tied to its own setting ——
        var label = new TextBlock { Text = L("Les plus utilisés", "Most used"), FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.Faint, Margin = new Thickness(4, 14, 0, 0) };
        Avalonia.Automation.AutomationProperties.SetHeadingLevel(label, 2);
        var quality = Mirror(_quality, L("Qualité", "Quality"), 210);
        var audio = Mirror(_audioLanguage, L("Langue audio préférée", "Preferred audio language"), 150);
        var subtitles = Mirror(_subtitleLanguage, L("Langue des sous-titres", "Subtitle language"), 150);
        var languages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Small(L("Audio", "Audio")), audio, Small(L("Sous-titres", "Subtitles")), subtitles } };
        languages.Children[2].Margin = new Thickness(10, 0, 0, 0);
        var adjust = Ui.Action(L("Ajuster", "Adjust"), null, "", () => Go("audio"));
        Avalonia.Automation.AutomationProperties.SetName(adjust, L("Ajuster la taille et le style des sous-titres", "Adjust subtitle size and style"));
        var style = new StackPanel { Orientation = Orientation.Horizontal, Children = { _essentialsSubtitles, adjust } };
        EssentialsAutoNext = Mirror(_autoNext, L("Enchaîner les épisodes", "Play episodes back to back"));
        var hint = new DockPanel { Margin = new Thickness(4, 8, 0, 0) };
        var glass = new Icon("search", 14) { Foreground = Ui.Faint, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(glass, Dock.Left); hint.Children.Add(glass);
        hint.Children.Add(new TextBlock { Text = L("Tout le reste est dans les sections à gauche, et ⌘F cherche dans tous les réglages.", "Everything else is in the sections on the left, and ⌘F searches every setting."), FontSize = 12.5, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap });

        var list = new StackPanel { Spacing = 10 };
        list.Children.Add(top);
        list.Children.Add(label);
        list.Children.Add(Card(Row(L("Qualité", "Quality"), "", quality)));
        list.Children.Add(Card(Row(L("Langues de l’audio et des sous-titres", "Audio and subtitle languages"), "", languages)));
        list.Children.Add(Card(Row(L("Taille et style des sous-titres", "Subtitle size and style"), "", style)));
        list.Children.Add(Card(Row(L("Enchaîner les épisodes", "Play episodes back to back"), "", EssentialsAutoNext)));
        list.Children.Add(Card(Row(L("Passer les intros et les récaps", "Skip intros and recaps"), "", Mirror(_autoSkip, L("Passer les intros et les récaps", "Skip intros and recaps")))));
        list.Children.Add(Card(Row(L("Langue de Mira", "Mira’s language"), "", Mirror(_language, L("Langue de Mira", "Mira’s language"), 240))));
        list.Children.Add(hint);
        return list;

        static TextBlock Small(string text) => new() { Text = text, FontSize = 12.5, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>A card's head as Windows': its picture, its name, a dot and a line under it; its buttons below.</summary>
    private static Control Head(Control picture, TextBlock name, Ellipse dot, TextBlock state, params Button[] buttons)
    {
        var line = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
        DockPanel.SetDock(dot, Dock.Left); line.Children.Add(dot); line.Children.Add(state);
        var text = Ui.Column(0, name, line); text.VerticalAlignment = VerticalAlignment.Center;
        var head = new DockPanel();
        DockPanel.SetDock(picture, Dock.Left); head.Children.Add(picture); head.Children.Add(text);
        var row = new WrapPanel { Margin = new Thickness(0, 16, 0, -8) };
        // Room for three buttons on one line in half the window: the quiet ones only as wide as their words.
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(0, 0, 8, 8);
            if (button.Classes.Contains("quiet")) button.Padding = new Thickness(10, button.Padding.Top, 10, button.Padding.Bottom);
            row.Children.Add(button);
        }
        return Ui.Column(0, head, row);
    }

    /// <summary>A section of the list opened from « L’essentiel », focus on its first setting.</summary>
    private void Go(string key)
    {
        _search.Text = ""; _searched = "";
        Show(key);
        Dispatcher.UIThread.Post(() => FirstFocusable(_tabBody)?.Focus(Avalonia.Input.NavigationMethod.Directional), DispatcherPriority.Loaded);
    }

    /// <summary>A switch of « L’essentiel » that is the one of its section: each follows the other, the section's saves.</summary>
    private static ToggleSwitch Mirror(ToggleSwitch setting, string name)
    {
        var shortcut = new ToggleSwitch { IsChecked = setting.IsChecked, OnContent = null, OffContent = null, MinWidth = 0 };
        Avalonia.Automation.AutomationProperties.SetName(shortcut, name);
        shortcut.IsCheckedChanged += (_, _) => { if (setting.IsChecked != shortcut.IsChecked) setting.IsChecked = shortcut.IsChecked; };
        setting.IsCheckedChanged += (_, _) => { if (shortcut.IsChecked != setting.IsChecked) shortcut.IsChecked = setting.IsChecked; };
        return shortcut;
    }
    /// <summary>A list of « L’essentiel » that is the one of its section: the same choices, each following the other.</summary>
    private static ComboBox Mirror(ComboBox setting, string name, double width)
    {
        var shortcut = new ComboBox { Width = width };
        foreach (var item in setting.Items.OfType<ComboBoxItem>()) shortcut.Items.Add(new ComboBoxItem { Content = item.Content, Tag = item.Tag });
        shortcut.SelectedIndex = setting.SelectedIndex;
        Avalonia.Automation.AutomationProperties.SetName(shortcut, name);
        shortcut.SelectionChanged += (_, _) => { if (shortcut.SelectedIndex >= 0 && setting.SelectedIndex != shortcut.SelectedIndex) setting.SelectedIndex = shortcut.SelectedIndex; };
        setting.SelectionChanged += (_, _) => { if (shortcut.SelectedIndex != setting.SelectedIndex) shortcut.SelectedIndex = setting.SelectedIndex; };
        return shortcut;
    }

    /// <summary>« L’essentiel » read again each time it shows: the update, the account, the subtitles' look.</summary>
    private void ShowEssentials()
    {
        ShowEssentialsUpdate();
        ShowEssentialsAccount();
        var colour = Shell.Settings.SubtitleColor == SubtitleStyle.Yellow ? L("jaune", "yellow") : L("blanc", "white");
        _essentialsSubtitles.Text = L("Taille", "Size") + " " + Shell.Settings.SubtitleSize.ToString(Lang.Culture) + " · " + colour;
    }

    /// <summary>The card of Mira: its version and where its update stands, in a few words.</summary>
    private void ShowEssentialsUpdate()
    {
        _essentialsVersion.Text = $"Mira {Updates.Current}";
        var ready = Updates.ReadyVersion;
        (string text, IBrush dot) = ready is { } version ? (L($"Mira {version.ToString(3)} est prête", $"Mira {version.ToString(3)} is ready"), Ui.Ink)
            : Updates.Checking ? (L("Recherche d’une nouvelle version…", "Looking for a new version…"), Ui.Faint)
            : Updates.SearchFailed ? (L("La recherche n’a pas abouti", "The check didn’t go through"), Ui.Warning)
            : Updates.UpToDateAt is { } time ? (L("À jour · vérifiée à ", "Up to date · checked at ") + Time(time), Ui.Success)
            : Shell.Settings.AutoUpdate ? (L("Elle se cherche toute seule", "Checked by itself"), Ui.Faint)
            : (L("Recherche automatique coupée", "Automatic check off"), Ui.Faint);
        _essentialsUpdate.Text = text; _essentialsUpdateDot.Fill = dot;
        _essentialsRestart.IsVisible = ready is not null;
        _essentialsCheck.IsVisible = ready is null; _essentialsCheck.IsEnabled = !Updates.Checking;
    }

    /// <summary>The card of the account: its name and photo, its server's name, and whether Jellyfin answers.</summary>
    private void ShowEssentialsAccount()
    {
        var connection = Shell.Session?.Connection;
        _essentialsLetter.Text = connection?.UserName is { Length: > 0 } name ? name[..1].ToUpperInvariant() : "?";
        _essentialsPhoto.Fill = Shell.Photo is { } photo ? new ImageBrush(photo) { Stretch = Stretch.UniformToFill } : null;
        _essentialsPhoto.IsVisible = Shell.Photo is not null;
        if (connection is null)
        {
            _essentialsName.Text = L("Pas connecté", "Not signed in");
            _essentialsServer.Text = L("Aucun serveur Jellyfin", "No Jellyfin server"); _essentialsServerDot.Fill = Ui.Faint;
        }
        else
        {
            var offline = Shell.NotAnswering;
            _essentialsName.Text = connection.UserName + (Shell.InParty ? " · " + L("invité", "guest") : "");
            var host = Uri.TryCreate(connection.Server, UriKind.Absolute, out var server) ? server.Host : connection.Server;
            _essentialsServer.Text = host + " · " + (offline ? L("ne répond pas", "not answering") : L("connecté", "connected"));
            _essentialsServerDot.Fill = offline ? Ui.Warning : Ui.Success;
        }
        _essentialsRecap.IsVisible = connection is not null && !Shell.InParty;
    }

    /// <summary>A version ready to install: a dot on « Mises à jour », in the button's own ink, said to VoiceOver too.</summary>
    private static void UpdateDot(Button button)
    {
        if (button.Content is not DockPanel row) return;
        var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(8, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, IsVisible = false };
        dot.Bind(Shape.FillProperty, button.GetObservable(Button.ForegroundProperty));
        DockPanel.SetDock(dot, Dock.Right); row.Children.Insert(1, dot);
        void Show()
        {
            dot.IsVisible = Updates.ReadyVersion is not null;
            Avalonia.Automation.AutomationProperties.SetHelpText(button, dot.IsVisible ? L("Une nouvelle version est prête.", "A new version is ready.") : null);
        }
        Page.WhileShown(button, shown => { if (shown) { Updates.Changed += Show; Show(); } else Updates.Changed -= Show; });
    }

    /// <summary>« Serveur Jellyfin » listed for an administrator: who watches what is asked only while it shows.</summary>
    private async Task JellyfinAdminAsync(Session session)
    {
        try { _jellyfinAdmin = await session.Client.IsAdministratorAsync(session.Lifetime) == true; }
        catch (Exception ex) when (Errors.Expected(ex)) { }
        _listed?.Invoke();
    }
}
