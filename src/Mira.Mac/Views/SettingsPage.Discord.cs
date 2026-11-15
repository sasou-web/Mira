using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Core.Discord;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// Réglages → Discord, as on Windows (panel A « Aperçu en direct », ogsas 2026-11-15): the card friends see on the left
/// (what plays, or an example of each tab), the style and what the card shows on the right; below, the buttons, who sees
/// what, the behaviour, « Hide for now » and what leaves this Mac. Each change is saved and reaches the card at once
/// (<see cref="MainWindow.DiscordSettingChanged"/>).
/// </summary>
public sealed partial class SettingsPage
{
    private const string NowTab = "now";
    private string _discordTab = DiscordPreview.Episode;
    private readonly StackPanel _discordCard = new() { Spacing = 10 };
    private readonly StackPanel _discordTabs = new();
    private readonly TextBlock _discordState = new() { FontSize = 12, LineHeight = 19, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 30, 0) };
    private readonly StackPanel _discordLibraryRows = new() { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel _discordHiddenRows = new() { Spacing = 6, IsVisible = false, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _discordHiddenCount = new() { FontSize = 12, Foreground = Ui.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock _discordCustomNote = new() { FontSize = 12, LineHeight = 19, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 30, 0) };
    private readonly List<(ToggleSwitch Switch, Func<bool> Read)> _discordSwitches = [];
    private readonly List<(Button Card, string Style)> _discordStyles = [];
    private Control? _discordCustom;
    private Button? _discordForNow;
    private ToggleSwitch? _discordSwitch;
    private int _discordGeneration;

    /// <summary>For the self-check: the preview's card as it stands (its main line), and the tab shown.</summary>
    public string DiscordPreviewTab => _discordTab;

    private void DrawDiscord()
    {
        var tab = _tabs["discord"];
        var s = Shell.Settings.Discord;
        _discordSwitch = new ToggleSwitch { IsChecked = s.Enabled, OnContent = null, OffContent = null, MinWidth = 0 };
        Avalonia.Automation.AutomationProperties.SetName(_discordSwitch, L("Montrer ce que je regarde sur Discord", "Show what I’m watching on Discord"));
        _discordSwitch.IsCheckedChanged += (_, _) => { if (s.Enabled == (_discordSwitch.IsChecked == true)) return; s.Enabled = _discordSwitch.IsChecked == true; Shell.DiscordSettingChanged(); };
        var top = Row(L("Montrer ce que je regarde sur Discord", "Show what I’m watching on Discord"), "", _discordSwitch);
        ((StackPanel)((DockPanel)top).Children[^1]).Children.Add(_discordState);
        tab.Children.Add(Card(top));

        // The preview on the left, the style and the card's switches on the right.
        var preview = Ui.Column(0, new TextBlock { Text = L("APERÇU · CE QUE VOIENT TES AMIS", "PREVIEW · WHAT FRIENDS SEE"), FontSize = 11, FontWeight = FontWeight.Bold, LetterSpacing = .6, Foreground = Ui.Faint, Margin = new Thickness(2, 0, 0, 10) }, _discordTabs, _discordCard);
        _discordCard.Margin = new Thickness(0, 12, 0, 0);
        var left = Ui.Column(8, new Border { Background = Ui.Brush("#181818"), CornerRadius = new CornerRadius(11), Padding = new Thickness(14), Child = preview },
            Description(L("Seules les autres personnes voient les boutons : c’est Discord qui les montre, pas Mira.", "Only other people see the buttons. Discord shows them, not Mira."), 0));
        var styles = new UniformGrid { Columns = 3 };
        foreach (var style in DiscordStyle.All)
        {
            var words = Ui.Column(3, new TextBlock { Text = DiscordStyle.Name(style), FontSize = 14, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = DiscordStyle.About(style), FontSize = 11.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap });
            var card = new Button { Content = words, Classes = { "quiet" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 6, 0), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9) };
            Avalonia.Automation.AutomationProperties.SetName(card, DiscordStyle.Name(style) + " · " + DiscordStyle.About(style));
            card.Click += (_, _) => { if (s.Style == style) return; DiscordPreview.Apply(s, style); foreach (var (box, read) in _discordSwitches) box.IsChecked = read(); Shell.DiscordSettingChanged(); };
            _discordStyles.Add((card, style)); styles.Children.Add(card);
        }
        var right = Ui.Column(6, Label(L("Style", "Style")), styles, Label(L("Sur la carte", "On the card")),
            CardSwitch(L("Titre", "Title"), null, () => s.ShowTitle, v => s.ShowTitle = v),
            CardSwitch(L("Saison et épisode", "Season and episode"), null, () => s.ShowEpisode, v => s.ShowEpisode = v),
            CardSwitch(L("Nom de l’épisode", "Episode name"), Shell.Settings.HideSpoilers ? L("Caché pour les épisodes pas encore vus", "Hidden for unseen episodes") : null, () => s.ShowEpisodeName, v => s.ShowEpisodeName = v),
            CardSwitch(L("Affiche", "Poster"), L("De TMDB", "From TMDB"), () => s.ShowPoster, v => s.ShowPoster = v),
            CardSwitch(L("Barre de temps", "Time bar"), null, () => s.ShowTime, v => s.ShowTime = v),
            CardSwitch(L("Pastille de Mira sur l’affiche", "Mira badge on the poster"), null, () => s.ShowBadge, v => s.ShowBadge = v));
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("300,18,*") };
        Grid.SetColumn(right, 2); columns.Children.Add(left); columns.Children.Add(right);
        tab.Children.Add(columns);

        // The buttons, the party, where the title leads.
        var buttons = DiscordButton.All.Select(x => (DiscordButton.Hint(x) is { Length: > 0 } hint ? $"{DiscordButton.Name(x)} · {hint}" : DiscordButton.Name(x), x)).ToArray();
        _discordCustom = CustomLink(s);
        tab.Children.Add(Section(L("Boutons · deux au plus, la limite de Discord", "Buttons · two at most, Discord’s limit"),
            Row(L("Premier bouton", "First button"), "", DiscordCombo(L("Premier bouton", "First button"), buttons, () => s.FirstButton, v => s.FirstButton = v)),
            Row(L("Second bouton", "Second button"), "", DiscordCombo(L("Second bouton", "Second button"), buttons, () => s.SecondButton, v => s.SecondButton = v)),
            _discordCustom,
            Row(L("Laisser mes amis rejoindre ma party depuis Discord", "Let friends join my party from Discord"),
                L("Pendant que vous regardez ensemble, le second bouton devient « Rejoindre la party ». Toute personne qui voit ton profil Discord peut alors ouvrir ton lien de party. Seulement un lien qui passe par Internet (Tailscale Funnel), jamais l’adresse de chez toi.",
                    "While you watch together, the second button becomes “Join the party”. Anyone who can see your Discord profile can then open your party link. Only a link that goes through the internet (Tailscale Funnel), never your home address."),
                DiscordSwitch(L("Laisser mes amis rejoindre ma party depuis Discord", "Let friends join my party from Discord"), () => s.JoinParty, v => s.JoinParty = v)),
            Row(L("L’affiche et le titre ouvrent", "Poster and title open"), "", DiscordCombo(L("L’affiche et le titre ouvrent", "Poster and title open"),
                [(L("La page TMDB du titre", "The title’s TMDB page"), DiscordButton.Tmdb), (L("La page IMDb du titre", "The title’s IMDb page"), DiscordButton.Imdb), (L("Rien", "Nothing"), DiscordButton.None)],
                () => s.TitleLink, v => s.TitleLink = v))));
        // A card of its own is wrapped by Section: its own Border shows while a button is « Custom link… ».
        if (_discordCustom.Parent is Border customCard) _discordCustom = customCard;

        // Who sees what.
        var libraries = new DockPanel();
        var choose = new TextBlock { Text = L("Au choix pour chacune", "Choose for each one"), FontSize = 12, Foreground = Ui.Faint, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(choose, Dock.Right); libraries.Children.Add(choose); libraries.Children.Add(CardTitle(L("Bibliothèques", "Libraries")));
        var manage = Ui.Action(L("Gérer", "Manage"), null, "quiet", () => { _discordHiddenRows.IsVisible = !_discordHiddenRows.IsVisible; FillDiscordHidden(); });
        var hiddenRight = new StackPanel { Orientation = Orientation.Horizontal, Children = { _discordHiddenCount, manage } };
        tab.Children.Add(Section(L("Qui voit quoi", "Who sees what"),
            Ui.Column(0, libraries, Description(L("Discret : « Regarde un film » ou « Regarde une série », sans le titre.", "Discreet: “Watching a film” or “Watching a series”, without the title.")), _discordLibraryRows),
            Row(L("Titres pour adultes", "Mature titles"), L("Classés R, TV-MA, 16 ans et plus.", "Rated R, TV-MA, 16+ and above."), Ways(L("Titres pour adultes", "Mature titles"), "mature", () => s.Mature, v => s.Mature = v)),
            Ui.Column(0, Row(L("Titres masqués", "Hidden titles"), L("« Masquer sur Discord » dans le menu « … » d’un film ou d’une série.", "“Hide on Discord” in the “…” menu of a film or a series."), hiddenRight), _discordHiddenRows)));

        // Behaviour.
        tab.Children.Add(Section(L("Comportement", "Behaviour"),
            Row(L("Quand je mets en pause", "When I pause"), "", DiscordCombo(L("Quand je mets en pause", "When I pause"),
                [.. DiscordPreview.PauseChoices.Select(x => (DiscordPreview.PauseName(x), x.ToString(CultureInfo.InvariantCulture)))],
                () => s.PausedMinutes.ToString(CultureInfo.InvariantCulture), v => s.PausedMinutes = int.Parse(v, CultureInfo.InvariantCulture))),
            Row(L("Dans la liste des membres", "In the member list"), L("La courte ligne sous ton nom.", "The short line under your name."), DiscordCombo(L("Dans la liste des membres", "In the member list"),
                [(L("Regarde Arcane (le titre)", "Watching Arcane (the title)"), DiscordStatusLine.Title), (L("Regarde Mira", "Watching Mira"), DiscordStatusLine.App)], () => s.MemberLine, v => s.MemberLine = v)),
            Row(L("Quand rien ne joue", "When nothing plays"), "", DiscordCombo(L("Quand rien ne joue", "When nothing plays"),
                [(L("Ne rien montrer", "Show nothing"), "nothing"), (L("« Parcourt la bibliothèque »", "“Browsing the library”"), "browsing")], () => s.ShowBrowsing ? "browsing" : "nothing", v => s.ShowBrowsing = v == "browsing")),
            Row(L("Invité de la party d’un ami", "As a guest in a friend’s party"), L("Sa bibliothèque, ses titres.", "Their library, their titles."),
                DiscordCombo(L("Invité de la party d’un ami", "As a guest in a friend’s party"), [.. DiscordShow.All.Select(x => (DiscordShow.Name(x), x))], () => s.Guest, v => s.Guest = v))));

        // Right now, and what leaves this Mac.
        _discordForNow = Ui.Action(L("Masquer pour l’instant", "Hide for now"), "eye-off", "", () => Shell.ToggleDiscordForNow());
        tab.Children.Add(Section(L("Maintenant", "Right now"),
            Row(L("Mettre mon statut Discord en pause", "Pause my Discord status"), L("Jusqu’à ce que tu le remettes, ou que Mira quitte. Aussi dans le menu ⋮ du lecteur.", "Until you turn it back on, or Mira quits. Also in the player’s ⋮ menu."), _discordForNow)));
        var note = Description("", 0);
        note.IsVisible = true;
        note.Inlines = [new Run(L("Ce qui sort de ce Mac. ", "What leaves this Mac. ")) { FontWeight = FontWeight.SemiBold, Foreground = Ui.TextBody },
            new Run(L("Mira donne la carte à l’app Discord de ce Mac, qui la montre sur ton profil. Jamais l’adresse de ton Jellyfin, ton adresse Tailscale ni le nom de ton compte. L’affiche est une adresse publique de TMDB que ton serveur Jellyfin connaît déjà : c’est Discord qui la charge, pas Mira.",
                "Mira hands the card to the Discord app on this Mac; Discord then shows it on your profile. Never your Jellyfin address, your Tailscale address or your account name. The poster is a public TMDB address your Jellyfin server already knows; Discord fetches it, not Mira."))];
        var privacy = Ui.Action(L("Confidentialité", "Privacy"), "shield", "quiet", () => Show("privacy"));
        privacy.HorizontalAlignment = HorizontalAlignment.Left; privacy.Margin = new Thickness(0, 10, 0, 0);
        tab.Children.Add(Card(Ui.Column(0, note, privacy)));

        Page.WhileShown(_discordCard, shown =>
        {
            if (shown) { Shell.DiscordShown += ShowDiscord; _ = Shell.LoadDiscordLibrariesAsync(); ShowDiscord(); }
            else Shell.DiscordShown -= ShowDiscord;
        });
        ShowDiscord();
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.Faint, Margin = new Thickness(4, 8, 0, 2), Classes = { "label" } };

    /// <summary>A switch of « On the card »: a short card, a hint before the switch when it says something.</summary>
    private Border CardSwitch(string name, string? hint, Func<bool> read, Action<bool> write)
    {
        var toggle = DiscordSwitch(name, read, write);
        _discordSwitches.Add((toggle, read));
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (hint is not null) right.Children.Add(new TextBlock { Text = hint, FontSize = 12, Foreground = Ui.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        right.Children.Add(toggle);
        var row = new DockPanel();
        DockPanel.SetDock(right, Dock.Right); row.Children.Add(right);
        row.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return new Border { Background = Ui.Brush("#181818"), CornerRadius = new CornerRadius(11), Padding = new Thickness(16, 9), Child = row, Classes = { "setting" } };
    }

    private ToggleSwitch DiscordSwitch(string name, Func<bool> read, Action<bool> write)
    {
        var toggle = new ToggleSwitch { IsChecked = read(), OnContent = null, OffContent = null, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(toggle, name);
        toggle.IsCheckedChanged += (_, _) => { if (read() == (toggle.IsChecked == true)) return; write(toggle.IsChecked == true); Shell.DiscordSettingChanged(); };
        return toggle;
    }

    private ComboBox DiscordCombo(string name, (string Label, string Value)[] options, Func<string> read, Action<string> write)
    {
        var box = new ComboBox { MinWidth = 240 };
        foreach (var (label, value) in options) box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag! == read()) ?? box.Items[0];
        Avalonia.Automation.AutomationProperties.SetName(box, name);
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: string value } && value != read()) { write(value); Shell.DiscordSettingChanged(); } };
        return box;
    }

    /// <summary>« Custom link… »: its label and https address, shown while a button uses it.</summary>
    private Control CustomLink(DiscordSettings s)
    {
        var label = new TextBox { Text = s.CustomLabel, MaxLength = 32, Width = 220, Margin = new Thickness(0, 0, 10, 0) };
        var url = new TextBox { Text = s.CustomUrl, Watermark = "https://" };
        Avalonia.Automation.AutomationProperties.SetName(label, L("Texte du bouton", "Button text"));
        Avalonia.Automation.AutomationProperties.SetName(url, L("Adresse du bouton (https)", "Button address (https)"));
        void Changed() { s.CustomLabel = (label.Text ?? "").Trim(); s.CustomUrl = (url.Text ?? "").Trim(); Shell.DiscordSettingChanged(); }
        label.LostFocus += (_, _) => Changed(); url.LostFocus += (_, _) => Changed();
        var fields = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(label, Dock.Left); fields.Children.Add(label); fields.Children.Add(url);
        return Ui.Column(0, CardTitle(L("Lien personnalisé", "Custom link")), fields, _discordCustomNote);
    }

    /// <summary>Show, Discreet or Hide side by side, the pill of the player's menus.</summary>
    private Border Ways(string name, string key, Func<string> read, Action<string> write)
    {
        var track = new ChoiceTrack(0, "discord." + key);
        var chips = new List<Button>();
        foreach (var way in DiscordShow.All)
        {
            var index = track.Count;
            var chip = new Button { Content = DiscordShow.Name(way), Classes = { "menuchip" }, MinWidth = 76 };
            chip.Classes.Set("on", DiscordShow.Normal(read()) == way);
            Avalonia.Automation.AutomationProperties.SetName(chip, name + " · " + DiscordShow.Name(way));
            chip.Click += (_, _) =>
            {
                track.Select(index);
                foreach (var other in chips) other.Classes.Set("on", ReferenceEquals(other, chip));
                if (DiscordShow.Normal(read()) == way) return;
                write(way); Shell.DiscordSettingChanged();
            };
            chips.Add(chip); track.Add(chip, DiscordShow.Normal(read()) == way);
        }
        return new Border { Background = Ui.Inset, BorderBrush = Ui.Rule, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Child = track };
    }

    /// <summary>The section as things stand now: the line under the switch, the styles, the libraries, the preview.</summary>
    private void ShowDiscord()
    {
        var s = Shell.Settings.Discord;
        if (_discordSwitch is not null) _discordSwitch.IsChecked = s.Enabled;
        var state = Shell.DiscordState;
        _discordState.Text = DiscordPreview.StateLine(state, Shell.DiscordError, mac: true);
        _discordState.Foreground = state == DiscordState.Live ? Ui.Success : Ui.TextSecondary;
        foreach (var (card, style) in _discordStyles)
        {
            var on = s.Style == style;
            card.Background = on ? Ui.Raised2 : Ui.Brush("#181818");
            card.BorderBrush = on ? Ui.Ink : Ui.Brush("#181818");
        }
        if (_discordForNow is not null)
        {
            var hidden = Shell.DiscordHiddenForNow;
            _discordForNow.Content = Ui.Row(8, new Icon(hidden ? "eye" : "eye-off", 15), new TextBlock { Text = hidden ? L("Remettre", "Show again") : L("Masquer pour l’instant", "Hide for now"), VerticalAlignment = VerticalAlignment.Center });
            Avalonia.Automation.AutomationProperties.SetName(_discordForNow, hidden ? L("Remettre mon statut Discord", "Show my Discord status again") : L("Masquer mon statut Discord pour l’instant", "Hide my Discord status for now"));
            _discordForNow.IsEnabled = s.Enabled;
        }
        if (_discordCustom is not null) _discordCustom.IsVisible = s.FirstButton == DiscordButton.Custom || s.SecondButton == DiscordButton.Custom;
        _discordCustomNote.Text = DiscordPresence.CustomLink(s.CustomLabel, s.CustomUrl) is not null ? L("Le bouton mène à cette page.", "The button leads to this page.")
            : L("Un texte de 1 à 32 caractères et une adresse https complète : sinon, pas de bouton.", "A text of 1 to 32 characters and a full https address: otherwise, no button.");
        _discordHiddenCount.Text = s.Hidden.Count == 0 ? L("Aucun", "None") : Lang.Count(s.Hidden.Count, "titre jamais montré", "titres jamais montrés", "title never shown", "titles never shown");
        if (_discordHiddenRows.IsVisible) FillDiscordHidden();
        FillDiscordLibraries();
        DrawDiscordPreview();
    }

    private string _discordLibrariesShown = "?";
    private void FillDiscordLibraries()
    {
        var libraries = Shell.DiscordLibraries;
        var key = string.Join(",", libraries.Select(x => x.Id));
        if (key == _discordLibrariesShown) return;
        _discordLibrariesShown = key;
        _discordLibraryRows.Children.Clear();
        var s = Shell.Settings.Discord;
        if (libraries.Count == 0) { _discordLibraryRows.Children.Add(Description(L("Tes bibliothèques apparaissent ici une fois Mira connecté à ton Jellyfin.", "Your libraries show here once Mira is connected to your Jellyfin."), 0)); return; }
        foreach (var library in libraries)
        {
            var id = library.Id;
            var ways = Ways(library.Name, "library." + id, () => s.Libraries.GetValueOrDefault(id, DiscordShow.Show), v => { if (v == DiscordShow.Show) s.Libraries.Remove(id); else s.Libraries[id] = v; });
            var icon = new Icon(library.CollectionType switch { "movies" => "film", "tvshows" => "series", _ => "folder" }, 16) { Foreground = Ui.TextSecondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var line = new DockPanel();
            DockPanel.SetDock(ways, Dock.Right); DockPanel.SetDock(icon, Dock.Left);
            line.Children.Add(ways); line.Children.Add(icon);
            line.Children.Add(new TextBlock { Text = library.Name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            _discordLibraryRows.Children.Add(line);
        }
    }

    private void FillDiscordHidden()
    {
        _discordHiddenRows.Children.Clear();
        var hidden = Shell.Settings.Discord.Hidden;
        if (hidden.Count == 0) _discordHiddenRows.Children.Add(Description(L("Aucun titre masqué.", "No hidden title."), 0));
        foreach (var title in hidden.ToList())
        {
            var again = Ui.Action(L("Montrer de nouveau", "Show again"), null, "quiet", () => { hidden.RemoveAll(x => x.Id == title.Id); Shell.DiscordSettingChanged(); });
            Avalonia.Automation.AutomationProperties.SetName(again, L($"Montrer {title.Name} de nouveau", $"Show {title.Name} again"));
            var line = new DockPanel();
            DockPanel.SetDock(again, Dock.Right); line.Children.Add(again);
            line.Children.Add(new TextBlock { Text = title.Name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            _discordHiddenRows.Children.Add(line);
        }
    }

    /// <summary>The tabs (what plays first, while something does) and the card of the one chosen.</summary>
    private void DrawDiscordPreview()
    {
        var playing = Shell.DiscordPlaying();
        var tabs = (playing is null ? DiscordPreview.Tabs : DiscordPreview.Tabs.Prepend(NowTab)).ToArray();
        if (!tabs.Contains(_discordTab)) _discordTab = tabs[0];
        var key = string.Join(",", tabs);
        if (_discordTabs.Tag as string != key)
        {
            _discordTabs.Tag = key;
            var track = new ChoiceTrack(0, "discord.preview");
            var chips = new List<Button>();
            foreach (var tab in tabs)
            {
                var index = track.Count;
                var chip = new Button { Content = tab == NowTab ? L("En cours", "Now") : DiscordPreview.Name(tab), Classes = { "menuchip" }, Padding = new Thickness(8, 0) };
                chip.Classes.Set("on", tab == _discordTab);
                chip.Click += (_, _) => { track.Select(index); _discordTab = tab; foreach (var other in chips) other.Classes.Set("on", ReferenceEquals(other, chip)); DrawDiscordCard(); };
                chips.Add(chip); track.Add(chip, tab == _discordTab);
            }
            _discordTabs.Children.Clear();
            _discordTabs.Children.Add(new Border { Background = Ui.Inset, BorderBrush = Ui.Rule, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11), Padding = new Thickness(3), Child = track });
        }
        DrawDiscordCard();
    }

    private void DrawDiscordCard()
    {
        var playback = _discordTab == NowTab ? Shell.DiscordPlaying() : DiscordPreview.Example(_discordTab);
        var generation = ++_discordGeneration;
        _discordCard.Children.Clear();
        var card = playback is null ? null : DiscordPreview.Card(Shell.Settings.Discord, playback, DateTimeOffset.UtcNow, "Mac");
        if (card is null || playback is null)
        {
            _discordCard.Children.Add(Description(L("Rien sur Discord pour ce titre : sa bibliothèque, ou le titre lui-même, est masqué (ou c’est une bande-annonce).", "Nothing on Discord for this one: its library, or the title itself, is hidden (or it’s a trailer)."), 0));
            return;
        }
        var example = _discordTab != NowTab;
        var picture = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(7), Background = Ui.Raised2, ClipToBounds = true };
        if (card.HasPoster)
        {
            picture.Child = new Icon(playback.Item.Type == "Episode" ? "series" : "film", 24) { Foreground = Ui.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (!example && Shell.Session is { } session) _ = PosterAsync(session, playback.Item, picture, generation);
        }
        else picture.Child = new Icon("mark", 30) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var art = new Grid { Width = 72, Height = 70, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0), Children = { new Border { Child = picture, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top } } };
        if (card.SmallImage is not null)
        {
            var kind = card.SmallImage == DiscordPresence.PauseSign ? "pause" : card.SmallImage == DiscordPresence.PlaySign ? "play" : "mark";
            var badge = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Ui.Black, BorderBrush = Ui.Raised, BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Child = new Icon(kind, 12) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            ToolTip.SetTip(badge, card.SmallText);
            art.Children.Add(badge);
        }
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = card.Details, FontSize = 14, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (card.State is { } state) words.Children.Add(new TextBlock { Text = state, FontSize = 12, Foreground = Ui.TextSecondary, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        if (card.Start is { } start && card.End is { } end)
        {
            var total = Math.Max(1, (end - start).TotalSeconds); var done = Math.Clamp((DateTimeOffset.UtcNow - start).TotalSeconds, 0, total);
            var bar = new Grid { Height = 3, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center, ColumnDefinitions = new ColumnDefinitions($"{done.ToString(CultureInfo.InvariantCulture)}*,{(total - done).ToString(CultureInfo.InvariantCulture)}*") };
            var track = new Border { CornerRadius = new CornerRadius(1.5), Background = Ui.LineStrong }; Grid.SetColumnSpan(track, 2);
            bar.Children.Add(track); bar.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = Ui.Ink });
            var times = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            TextBlock Time(double seconds) { var t = TimeSpan.FromSeconds(seconds); return new TextBlock { Text = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}", FontSize = 11, Foreground = Ui.Muted }; }
            var from = Time(done); var to = Time(total);
            DockPanel.SetDock(from, Dock.Left); DockPanel.SetDock(to, Dock.Right);
            times.Children.Add(from); times.Children.Add(to); times.Children.Add(bar);
            words.Children.Add(times);
        }
        var body = new DockPanel();
        DockPanel.SetDock(art, Dock.Left); body.Children.Add(art); body.Children.Add(words);
        var inside = Ui.Column(0, new TextBlock { Text = L("Regarde Mira", "Watching Mira"), FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.Muted, Margin = new Thickness(0, 0, 0, 8) }, body);
        foreach (var button in card.Buttons)
        {
            var shown = new Border { Background = Ui.Raised2, CornerRadius = new CornerRadius(5), Height = 30, Margin = new Thickness(0, 8, 0, 0), Child = new TextBlock { Text = button.Label, FontSize = 12.5, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            ToolTip.SetTip(shown, button.Url);
            inside.Children.Add(shown);
        }
        _discordCard.Children.Add(new Border { Background = Ui.Raised, CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = inside });

        var person = Shell.Session?.Connection.UserName is { Length: > 0 } name ? name : "Alex";
        var dot = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Ui.LineStrong, Margin = new Thickness(0, 0, 10, 0), Child = new TextBlock { Text = person[..1].ToUpperInvariant(), FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var lines = Ui.Column(0, new TextBlock { Text = person, FontSize = 12.5, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = DiscordPreview.MemberLine(card), FontSize = 11.5, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        lines.VerticalAlignment = VerticalAlignment.Center;
        var member = new DockPanel();
        DockPanel.SetDock(dot, Dock.Left); member.Children.Add(dot); member.Children.Add(lines);
        _discordCard.Children.Add(new Border { Background = Ui.Raised, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 50, 0), Child = member });
        if (example) _discordCard.Children.Add(new TextBlock { Text = L("Exemple : Discord montre l’affiche de TMDB du titre.", "Example: Discord shows the title’s TMDB poster."), FontSize = 11.5, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap });
    }

    /// <summary>What plays: its poster as Mira already has it from Jellyfin (Discord fetches its own copy from TMDB).</summary>
    private async Task PosterAsync(Session session, MediaItem item, Border picture, int generation)
    {
        try
        {
            var image = await session.Images.PosterAsync(item, 128);
            if (image is null || generation != _discordGeneration) return;
            picture.Child = null; picture.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        }
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }
}
