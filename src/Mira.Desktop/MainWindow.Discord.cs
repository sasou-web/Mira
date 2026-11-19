using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mira.Core;
using Mira.Core.Discord;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// The Rich Presence of Discord (ogsas, 2026-11-15, panel A « Aperçu en direct »): what plays shown on the person's
/// Discord profile, by the Discord app of this PC (<see cref="DiscordStatus"/> of Core: its local pipe, no connection of
/// Mira's own). Off until turned on, offered once after a playback while Discord runs. Réglages → Discord: the card friends
/// see on the left (what plays, or an example of each tab), every choice on the right and below; « Discord status » in the
/// player's ⋮ and the notification area's menu hides it for now; « Hide on Discord » in a title's « … ». Never for the
/// checks nor the demo.
/// </summary>
public partial class MainWindow
{
    private DiscordStatus? _discord;
    private readonly DiscordTitles _discordTitles = new();
    private DiscordTitle? _discordTitle;
    private string? _discordTitleFor;
    private JellyfinClient? _discordClient;
    private string? _discordParty;
    private JellyfinClient? _discordPartyFor;
    private DateTime _discordAt;
    private string? _discordSent;
    private string? _discordWithheld;
    /// <summary>The server, the account and this PC: never in an address of the card (<see cref="DiscordPresence.Leaks"/>), read on the loop's thread.</summary>
    private volatile string?[] _discordPrivate = [Environment.MachineName];

    private bool DiscordAllowed => !IsCheckRun && !_demo && !_closing;

    /// <summary>What plays now told to Discord: at each tick of the player (at most once a second unless something changed), at its start and its end.</summary>
    private void UpdateDiscord(bool force = false)
    {
        if (!DiscordAllowed) return;
        var settings = _settings.Discord;
        if (_discord is null)
        {
            if (!settings.Enabled) return;
            _discord = new DiscordStatus("Windows") { Private = () => _discordPrivate };
            _discord.Changed += () => Dispatcher.BeginInvoke(DiscordChanged);
        }
        var playback = DiscordPlaying();
        var key = playback is null ? "" : $"{playback.Item.Id}|{playback.Paused}|{playback.Friends}|{playback.Title is not null}|{playback.PartyLink}";
        var now = DateTime.UtcNow;
        if (!force && key == _discordSent && now - _discordAt < TimeSpan.FromSeconds(1)) return;
        _discordSent = key; _discordAt = now;
        _discordPrivate = DiscordPrivateNames();
        _discord.Update(settings, playback, DateTimeOffset.UtcNow);
    }

    private string?[] DiscordPrivateNames()
    {
        var names = new List<string?> { Environment.MachineName, Environment.UserName };
        foreach (var client in new[] { _client, _serverClient })
            if (client?.Connection is { } connection)
            {
                names.Add(connection.UserName);
                if (Uri.TryCreate(connection.Server, UriKind.Absolute, out var server)) names.Add(server.Host);
            }
        return [.. names];
    }

    /// <summary>The playback as the card is made of it; null when nothing plays.</summary>
    private DiscordPlayback? DiscordPlaying()
    {
        if (!_playing || _mpv is null || _session.Item is not { } item) return null;
        var settings = _settings.Discord;
        if (!ReferenceEquals(_discordClient, _client)) { _discordClient = _client; _discordTitles.Clear(); _discordTitleFor = null; _discordParty = null; _discordPartyFor = null; }
        var titleKey = item.Id + (settings.ShowPoster ? "+poster" : "");
        if (_discordTitleFor != titleKey)
        {
            _discordTitleFor = titleKey; _discordTitle = null;
            if (_client is { } client) _ = ReadDiscordTitleAsync(client, item, titleKey, settings.ShowPoster);
        }
        var friends = InGroup && _group is { } group ? Math.Max(0, group.Participants.Count - 1) : 0;
        if (InGroup && settings.JoinParty && !InParty) _ = ReadDiscordPartyAsync();
        return new(item, _discordTitle, _session.Paused, _session.Position, _session.Duration, _mpv.Number("speed", 1), InGroup, friends,
            InGroup && settings.JoinParty && !InParty ? _discordParty : null, InParty, _skips.Film is not null, _settings.HideSpoilers);
    }

    private async Task ReadDiscordTitleAsync(JellyfinClient client, MediaItem item, string key, bool poster)
    {
        try
        {
            var libraries = _homeLibraries.Select(x => x.Id).ToList();
            var title = await _discordTitles.ReadAsync(client, item, libraries, poster);
            if (_discordTitleFor != key || _closing) return;
            _discordTitle = title; UpdateDiscord(force: true); RefreshDiscordPreview();
        }
        catch (Exception ex) when (IsExpected(ex)) { }
    }

    /// <summary>This account's party link, once per connection: only one that reaches the internet (Funnel) may go on a card.</summary>
    private async Task ReadDiscordPartyAsync()
    {
        if (OwnClient is not { } client || ReferenceEquals(_discordPartyFor, client)) return;
        _discordPartyFor = client; _discordParty = null;
        try
        {
            var party = await client.PartyAsync(ct: _connectionLifetime.Token);
            if (!ReferenceEquals(_discordPartyFor, client)) return;
            _discordParty = party is { Everywhere: true } ? DiscordPresence.PartyButton(party.Link)?.Url : null;
            UpdateDiscord(force: true);
        }
        catch (Exception ex) when (IsExpected(ex)) { }
    }

    /// <summary>The card or the pipe changed: Réglages' line and preview, the ⋮ menu and the notification area's menu follow.</summary>
    private void DiscordChanged()
    {
        if (_closing || _discord is not { } discord) return;
        if (discord.Withheld.Count > 0 && string.Join("; ", discord.Withheld) is var withheld && withheld != _discordWithheld)
        {
            _discordWithheld = withheld;
            ErrorLog.Note(AppFiles.ProfileDirectory, "Discord: a card naming the server or this PC was held back: " + withheld);
        }
        ShowDiscordStateLine();
        ShowDiscordInMenus();
        RefreshDiscordPreview();
    }

    private void ShowDiscordInMenus()
    {
        var on = _settings.Discord.Enabled && DiscordAllowed;
        var shown = on && _discord?.HiddenForNow != true;
        DiscordStatusRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        DiscordStatusCheck.IsChecked = shown;
        DiscordStatusText.Text = !on ? "" : shown ? L("Ta carte est sur ton profil.", "Your card is on your profile.") : L("Masqué jusqu’à ce que tu le remettes.", "Hidden until you turn it back on.");
        _windows?.SetDiscord(on ? shown : null);
    }

    /// <summary>« Discord status » of the player's ⋮, the notification area's menu and Réglages: hidden for now, or shown again.</summary>
    private void ToggleDiscordForNow()
    {
        if (!_settings.Discord.Enabled || !DiscordAllowed) return;
        UpdateDiscord(force: true);
        if (_discord is not { } discord) return;
        discord.HiddenForNow = !discord.HiddenForNow;
        UpdateDiscord(force: true);
        ShowDiscordInMenus(); ShowDiscordSettingsState();
    }
    private void DiscordStatus_Click(object sender, RoutedEventArgs e) => ToggleDiscordForNow();

    /// <summary>
    /// The one notice, after a playback ends while Discord runs on this PC (in the player, a notice would be under the
    /// video): the card is on by default, Mira says so once, with the way to Réglages → Discord.
    /// </summary>
    private void OfferDiscord()
    {
        var settings = _settings.Discord;
        if (!settings.Enabled || settings.Offered || !DiscordAllowed || InParty || !DiscordIpc.Available()) return;
        settings.Offered = true; TrySaveSettings();
        SetNotice(DiscordPreview.Told, L("Réglages", "Settings"), OpenDiscordSettings, sticky: true);
    }

    private void OpenDiscordSettings() { Settings_Click(this, new()); SelectSettingsTab("discord"); }

    /// <summary>« Hide on Discord » of a title's « … »: films and series, while the card is on.</summary>
    private bool DiscordHideable(MediaItem item) => _settings.Discord.Enabled && !_demo && item.Type is "Movie" or "Series";
    private MenuItem DiscordHideMenuItem(MediaItem item)
    {
        var hidden = _settings.Discord.Hidden.Any(x => x.Id == item.Id);
        return MenuAction(hidden ? L("Montrer sur Discord", "Show on Discord") : L("Masquer sur Discord", "Hide on Discord"), hidden ? "eye" : "eye-off", () =>
        {
            var list = _settings.Discord.Hidden;
            if (hidden) list.RemoveAll(x => x.Id == item.Id); else list.Add(new(item.Id, item.Name));
            DiscordSettingChanged();
            return Task.CompletedTask;
        });
    }

    /// <summary>A choice of Réglages → Discord (or of a menu): saved, the card and the preview follow at once.</summary>
    private void DiscordSettingChanged()
    {
        if (!TrySaveSettings()) SetNotice(SettingsNotSaved);
        if (!_settings.Discord.Enabled && _discord is { } discord) discord.HiddenForNow = false;
        // A poster asked for, or no longer: the title is read again.
        _discordTitleFor = null;
        UpdateDiscord(force: true);
        ShowDiscordInMenus(); ShowDiscordSettingsState(); RefreshDiscordPreview();
    }

    private async Task CloseDiscordAsync()
    {
        if (_discord is not { } discord) return;
        _discord = null;
        await discord.DisposeAsync();
    }

    // —— Réglages → Discord ——

    private string _discordTab = DiscordPreview.Episode;
    private const string NowTab = "now";
    private TextBlock? _discordState;
    private CheckBox? _discordSwitch;
    private Panel? _discordCardHost, _discordTabs, _discordStyles, _discordLibraries, _discordHiddenList;
    private TextBlock? _discordHiddenCount;
    private Button? _discordForNow, _discordManage;
    private readonly List<(CheckBox Box, Func<bool> Read)> _discordSwitches = [];
    private int _discordPreviewGeneration;

    private void ShowDiscordSettings()
    {
        if (DiscordPage.Children.Count > 0) { ShowDiscordSettingsState(); FillDiscordLibraries(); RefreshDiscordPreview(); return; }
        var s = _settings.Discord;
        _discordSwitch = PrivacySwitch(L("Montrer ce que je regarde sur Discord", "Show what I’m watching on Discord"), s.Enabled, on => { s.Enabled = on; DiscordSettingChanged(); });
        _discordState = new TextBlock { Style = (Style)FindResource("SettingDescription"), Foreground = Palette.Get("TextSecondary") };
        AutomationProperties.SetLiveSetting(_discordState, AutomationLiveSetting.Polite);
        DiscordPage.Children.Add(DiscordRow(L("Montrer ce que je regarde sur Discord", "Show what I’m watching on Discord"), null, _discordSwitch, _discordState));

        // The preview on the left, the style and what the card shows on the right.
        var columns = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(318) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        var preview = new StackPanel();
        var previewBox = new Border { Style = (Style)DiscordPage.FindResource("SettingRow"), Padding = new Thickness(14), Margin = new Thickness(0), Child = preview };
        preview.Children.Add(new TextBlock { Text = L("APERÇU · CE QUE VOIENT TES AMIS", "PREVIEW · WHAT FRIENDS SEE"), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Palette.Get("Faint"), Margin = new Thickness(2, 0, 0, 10) });
        _discordTabs = new StackPanel();
        preview.Children.Add(_discordTabs);
        _discordCardHost = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        preview.Children.Add(_discordCardHost);
        var left = new StackPanel { Children = { previewBox } };
        left.Children.Add(new TextBlock { Text = L("Seules les autres personnes voient les boutons : c’est Discord qui les montre, pas Mira.", "Only other people see the buttons. Discord shows them, not Mira."), Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(4, 8, 4, 0) });
        columns.Children.Add(left);

        var right = new StackPanel();
        Grid.SetColumn(right, 2);
        right.Children.Add(DiscordHeading(L("Style", "Style"), top: false));
        _discordStyles = new UniformGrid { Columns = 3 };
        foreach (var style in DiscordStyle.All)
        {
            var words = new StackPanel { Children = { new TextBlock { Text = DiscordStyle.Name(style), FontWeight = FontWeights.SemiBold }, new TextBlock { Text = DiscordStyle.About(style), FontSize = 11.5, FontWeight = FontWeights.Normal, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) } } };
            var choice = new RadioButton { Style = (Style)FindResource("PopoverRadio"), GroupName = "discord-style", Content = words, Tag = style, Margin = new Thickness(0, 0, 6, 0), IsChecked = s.Style == style };
            AutomationProperties.SetName(choice, DiscordStyle.Name(style) + " · " + DiscordStyle.About(style));
            choice.Checked += (_, _) => { if (s.Style == style) return; DiscordPreview.Apply(s, style); ReadDiscordSwitches(); DiscordSettingChanged(); };
            _discordStyles.Children.Add(new Border { Background = Palette.Get("Surface"), CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 6, 0), Child = choice });
        }
        right.Children.Add(_discordStyles);
        right.Children.Add(DiscordHeading(L("Sur la carte", "On the card")));
        right.Children.Add(DiscordCardSwitch(L("Titre", "Title"), null, () => s.ShowTitle, on => s.ShowTitle = on));
        right.Children.Add(DiscordCardSwitch(L("Saison et épisode", "Season and episode"), null, () => s.ShowEpisode, on => s.ShowEpisode = on));
        right.Children.Add(DiscordCardSwitch(L("Nom de l’épisode", "Episode name"), () => _settings.HideSpoilers ? L("Caché pour les épisodes pas encore vus", "Hidden for unseen episodes") : null, () => s.ShowEpisodeName, on => s.ShowEpisodeName = on));
        right.Children.Add(DiscordCardSwitch(L("Affiche", "Poster"), () => L("De TMDB", "From TMDB"), () => s.ShowPoster, on => s.ShowPoster = on));
        right.Children.Add(DiscordCardSwitch(L("Barre de temps", "Time bar"), null, () => s.ShowTime, on => s.ShowTime = on));
        right.Children.Add(DiscordCardSwitch(L("Pastille de Mira sur l’affiche", "Mira badge on the poster"), null, () => s.ShowBadge, on => s.ShowBadge = on));
        columns.Children.Add(right);
        DiscordPage.Children.Add(columns);

        // The buttons, the party, where the title leads.
        DiscordPage.Children.Add(DiscordHeading(L("Boutons · deux au plus, la limite de Discord", "Buttons · two at most, Discord’s limit")));
        DiscordPage.Children.Add(DiscordRow(L("Premier bouton", "First button"), null, DiscordButtonChoice(L("Premier bouton", "First button"), () => s.FirstButton, x => s.FirstButton = x)));
        DiscordPage.Children.Add(DiscordRow(L("Second bouton", "Second button"), null, DiscordButtonChoice(L("Second bouton", "Second button"), () => s.SecondButton, x => s.SecondButton = x)));
        DiscordPage.Children.Add(DiscordRow(L("Laisser mes amis rejoindre ma party depuis Discord", "Let friends join my party from Discord"),
            L("Pendant que vous regardez ensemble, le second bouton devient « Rejoindre la party ». Toute personne qui voit ton profil Discord peut alors ouvrir ton lien de party. Seulement un lien qui passe par Internet (Tailscale Funnel), jamais l’adresse de chez toi.",
                "While you watch together, the second button becomes “Join the party”. Anyone who can see your Discord profile can then open your party link. Only a link that goes through the internet (Tailscale Funnel), never your home address."),
            PrivacySwitch(L("Laisser mes amis rejoindre ma party depuis Discord", "Let friends join my party from Discord"), s.JoinParty, on => { s.JoinParty = on; _discordPartyFor = null; DiscordSettingChanged(); })));
        DiscordPage.Children.Add(DiscordRow(L("L’affiche et le titre ouvrent", "Poster and title open"), null, DiscordChoice(L("L’affiche et le titre ouvrent", "Poster and title open"),
            [(DiscordButton.Tmdb, L("La page TMDB du titre", "The title’s TMDB page")), (DiscordButton.Imdb, L("La page IMDb du titre", "The title’s IMDb page")), (DiscordButton.None, L("Rien", "Nothing"))],
            () => s.TitleLink, x => s.TitleLink = x)));

        // Who sees what.
        DiscordPage.Children.Add(DiscordHeading(L("Qui voit quoi", "Who sees what")));
        _discordLibraries = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var libraries = new StackPanel();
        libraries.Children.Add(new DockPanel { Children = { DockRight(new TextBlock { Text = L("Au choix pour chacune", "Choose for each one"), FontSize = 12, Foreground = Palette.Get("Faint"), VerticalAlignment = VerticalAlignment.Center }), new TextBlock { Text = L("Bibliothèques", "Libraries"), FontWeight = FontWeights.SemiBold } } });
        libraries.Children.Add(new TextBlock { Text = L("Discret : « Regarde un film » ou « Regarde une série », sans le titre.", "Discreet: “Watching a film” or “Watching a series”, without the title."), Style = (Style)FindResource("SettingDescription") });
        libraries.Children.Add(_discordLibraries);
        DiscordPage.Children.Add(new Border { Style = (Style)DiscordPage.FindResource("SettingRow"), Child = libraries });
        FillDiscordLibraries();
        DiscordPage.Children.Add(DiscordRow(L("Titres pour adultes", "Mature titles"), L("Classés R, TV-MA, 16 ans et plus.", "Rated R, TV-MA, 16+ and above."),
            DiscordWays(L("Titres pour adultes", "Mature titles"), "discord-mature", () => s.Mature, x => s.Mature = x)));
        _discordHiddenCount = new TextBlock { FontSize = 12, Foreground = Palette.Get("Faint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        _discordManage = new Button { Content = L("Gérer", "Manage"), Style = (Style)FindResource("Quiet"), VerticalAlignment = VerticalAlignment.Center };
        _discordHiddenList = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
        _discordManage.Click += (_, _) => { _discordHiddenList.Visibility = _discordHiddenList.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; FillDiscordHidden(); };
        var hiddenRight = new StackPanel { Orientation = Orientation.Horizontal, Children = { _discordHiddenCount, _discordManage } };
        DiscordPage.Children.Add(DiscordRow(L("Titres masqués", "Hidden titles"), L("« Masquer sur Discord » dans le menu « … » d’un film ou d’une série.", "“Hide on Discord” in the “…” menu of a film or a series."), hiddenRight, _discordHiddenList));

        // Behaviour.
        DiscordPage.Children.Add(DiscordHeading(L("Comportement", "Behaviour")));
        DiscordPage.Children.Add(DiscordRow(L("Quand je mets en pause", "When I pause"), null, DiscordChoice(L("Quand je mets en pause", "When I pause"),
            DiscordPreview.PauseChoices.Select(x => (x.ToString(System.Globalization.CultureInfo.InvariantCulture), DiscordPreview.PauseName(x))).ToArray(),
            () => s.PausedMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture), x => s.PausedMinutes = int.Parse(x, System.Globalization.CultureInfo.InvariantCulture))));
        DiscordPage.Children.Add(DiscordRow(L("Dans la liste des membres", "In the member list"), L("La courte ligne sous ton nom.", "The short line under your name."), DiscordChoice(L("Dans la liste des membres", "In the member list"),
            [(DiscordStatusLine.Title, L("Regarde Arcane (le titre)", "Watching Arcane (the title)")), (DiscordStatusLine.App, L("Regarde Mira", "Watching Mira"))], () => s.MemberLine, x => s.MemberLine = x)));
        DiscordPage.Children.Add(DiscordRow(L("Quand rien ne joue", "When nothing plays"), null, DiscordChoice(L("Quand rien ne joue", "When nothing plays"),
            [("nothing", L("Ne rien montrer", "Show nothing")), ("browsing", L("« Parcourt la bibliothèque »", "“Browsing the library”"))], () => s.ShowBrowsing ? "browsing" : "nothing", x => s.ShowBrowsing = x == "browsing")));
        DiscordPage.Children.Add(DiscordRow(L("Invité de la party d’un ami", "As a guest in a friend’s party"), L("Sa bibliothèque, ses titres.", "Their library, their titles."),
            DiscordChoice(L("Invité de la party d’un ami", "As a guest in a friend’s party"), DiscordShow.All.Select(x => (x, DiscordShow.Name(x))).ToArray(), () => s.Guest, x => s.Guest = x)));

        // Right now.
        DiscordPage.Children.Add(DiscordHeading(L("Maintenant", "Right now")));
        _discordForNow = new Button { Style = (Style)FindResource("Quiet"), VerticalAlignment = VerticalAlignment.Center };
        _discordForNow.Click += (_, _) => ToggleDiscordForNow();
        DiscordPage.Children.Add(DiscordRow(L("Mettre mon statut Discord en pause", "Pause my Discord status"),
            L("Jusqu’à ce que tu le remettes, ou que Mira se ferme. Aussi dans le menu ⋮ du lecteur et le menu de la zone de notification.", "Until you turn it back on, or Mira closes. Also in the player’s ⋮ menu and the notification area’s menu."), _discordForNow));

        // What leaves this PC.
        var note = new TextBlock { Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 0, 0, 0) };
        note.Inlines.Add(new System.Windows.Documents.Run(L("Ce qui sort de ce PC. ", "What leaves this PC. ")) { FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextBody") });
        note.Inlines.Add(new System.Windows.Documents.Run(L("Mira donne la carte à l’app Discord de ce PC, qui la montre sur ton profil. Jamais l’adresse de ton Jellyfin, ton adresse Tailscale ni le nom de ton compte. L’affiche est une adresse publique de TMDB que ton serveur Jellyfin connaît déjà : c’est Discord qui la charge, pas Mira.",
            "Mira hands the card to the Discord app on this PC; Discord then shows it on your profile. Never your Jellyfin address, your Tailscale address or your account name. The poster is a public TMDB address your Jellyfin server already knows; Discord fetches it, not Mira.")));
        var privacy = new Button { Content = L("Confidentialité", "Privacy"), Style = (Style)FindResource("Quiet"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        privacy.Click += (_, _) => SelectSettingsTab("privacy");
        var icon = new Icon { Kind = "shield", Width = 18, Height = 18, Foreground = Palette.Get("TextSecondary"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        DiscordPage.Children.Add(new Border { Style = (Style)DiscordPage.FindResource("SettingRow"), Margin = new Thickness(0, 6, 0, 10), Child = new DockPanel { Children = { DockLeft(icon), new StackPanel { Children = { note, privacy } } } } });

        ShowDiscordSettingsState();
        RefreshDiscordPreview();
    }

    private static T DockRight<T>(T element) where T : UIElement { DockPanel.SetDock(element, Dock.Right); return element; }
    private static T DockLeft<T>(T element) where T : UIElement { DockPanel.SetDock(element, Dock.Left); return element; }

    private TextBlock DiscordHeading(string text, bool top = true)
    {
        var heading = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("Faint"), Margin = new Thickness(4, top ? 18 : 0, 0, 10) };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
        return heading;
    }

    private Border DiscordRow(string title, string? about, FrameworkElement? control, FrameworkElement? below = null)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (about is not null) text.Children.Add(new TextBlock { Text = about, Style = (Style)FindResource("SettingDescription") });
        var row = new DockPanel();
        if (control is not null) { control.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(DockRight(control)); }
        row.Children.Add(text);
        var whole = new StackPanel { Children = { row } };
        if (below is not null) whole.Children.Add(below);
        return new Border { Style = (Style)DiscordPage.FindResource("SettingRow"), Child = whole };
    }

    /// <summary>A switch of « On the card »: a short row, a hint on the right when it says something.</summary>
    private Border DiscordCardSwitch(string name, Func<string?>? hint, Func<bool> read, Action<bool> write)
    {
        var box = PrivacySwitch(name, read(), on => { write(on); DiscordSettingChanged(); });
        _discordSwitches.Add((box, read));
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (hint?.Invoke() is { } said) right.Children.Add(new TextBlock { Text = said, FontSize = 12, Foreground = Palette.Get("Faint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        box.VerticalAlignment = VerticalAlignment.Center;
        right.Children.Add(box);
        var row = new DockPanel { Children = { DockRight(right), new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center } } };
        return new Border { Style = (Style)DiscordPage.FindResource("SettingRow"), Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 0, 6), Child = row };
    }

    /// <summary>The switches put back as the settings are (a style's preset changed several).</summary>
    private void ReadDiscordSwitches() { foreach (var (box, read) in _discordSwitches) box.IsChecked = read(); }

    private ComboBox DiscordChoice(string name, (string Tag, string Label)[] choices, Func<string> read, Action<string> write)
    {
        var box = new ComboBox { Width = 250 };
        AutomationProperties.SetName(box, name);
        box.PreviewMouseLeftButtonDown += Combo_PreviewClick;
        foreach (var (tag, label) in choices) box.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == read()) ?? box.Items[0];
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is not ComboBoxItem { Tag: string tag } || tag == read()) return;
            write(tag); DiscordSettingChanged();
        };
        return box;
    }

    private ComboBox DiscordButtonChoice(string name, Func<string> read, Action<string> write) =>
        DiscordChoice(name, DiscordButton.All.Select(x => (x, DiscordButton.Hint(x) is { Length: > 0 } hint ? $"{DiscordButton.Name(x)} · {hint}" : DiscordButton.Name(x))).ToArray(), read, write);

    /// <summary>Show, Discreet or Hide, side by side (the pill of the player's menus).</summary>
    private Border DiscordWays(string name, string group, Func<string> read, Action<string> write)
    {
        var track = new ChoiceTrack();
        foreach (var way in DiscordShow.All)
        {
            var part = new RadioButton { Style = (Style)FindResource("PlayerSpeedChoice"), GroupName = group, Content = DiscordShow.Name(way), IsChecked = DiscordShow.Normal(read()) == way, MinWidth = 76 };
            AutomationProperties.SetName(part, name + " · " + DiscordShow.Name(way));
            part.Checked += (_, _) => { if (DiscordShow.Normal(read()) == way) return; write(way); DiscordSettingChanged(); };
            track.Children.Add(part);
        }
        return new Border { Style = (Style)FindResource("PlayerSlider"), Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, Child = track };
    }

    /// <summary>Each library of this server with its way; the ones Mira does not show (music, photos…) left out.</summary>
    private void FillDiscordLibraries()
    {
        if (_discordLibraries is null) return;
        _discordLibraries.Children.Clear();
        var s = _settings.Discord;
        var libraries = _homeLibraries.Where(x => !LibrarySection.Groups(x.CollectionType)).ToList();
        if (libraries.Count == 0)
        {
            _discordLibraries.Children.Add(new TextBlock { Text = L("Tes bibliothèques apparaissent ici une fois Mira connecté à ton Jellyfin.", "Your libraries show here once Mira is connected to your Jellyfin."), Style = (Style)FindResource("SettingDescription") });
            return;
        }
        foreach (var library in libraries)
        {
            var id = library.Id;
            var ways = DiscordWays(library.Name, "discord-library-" + id, () => s.Libraries.GetValueOrDefault(id, DiscordShow.Show), x => { if (x == DiscordShow.Show) s.Libraries.Remove(id); else s.Libraries[id] = x; });
            var kind = library.CollectionType switch { "movies" => "film", "tvshows" => "series", _ => "folder" };
            var icon = new Icon { Kind = kind, Width = 16, Height = 16, Foreground = Palette.Get("TextSecondary"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = library.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            _discordLibraries.Children.Add(new DockPanel { Margin = new Thickness(0, 4, 0, 4), Children = { DockRight(ways), DockLeft(icon), name } });
        }
    }

    private void FillDiscordHidden()
    {
        if (_discordHiddenList is null) return;
        _discordHiddenList.Children.Clear();
        var hidden = _settings.Discord.Hidden;
        if (hidden.Count == 0)
            _discordHiddenList.Children.Add(new TextBlock { Text = L("Aucun titre masqué.", "No hidden title."), Style = (Style)FindResource("SettingDescription") });
        foreach (var title in hidden.ToList())
        {
            var again = new Button { Content = L("Montrer de nouveau", "Show again"), Style = (Style)FindResource("Quiet") };
            AutomationProperties.SetName(again, L($"Montrer {title.Name} de nouveau", $"Show {title.Name} again"));
            again.Click += (_, _) => { hidden.RemoveAll(x => x.Id == title.Id); DiscordSettingChanged(); FillDiscordHidden(); };
            _discordHiddenList.Children.Add(new DockPanel { Margin = new Thickness(0, 3, 0, 3), Children = { DockRight(again), new TextBlock { Text = title.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } } });
        }
    }

    /// <summary>The line under the switch, « Hide for now » and the hidden titles' count, as things are.</summary>
    private void ShowDiscordSettingsState()
    {
        if (_discordSwitch is null) return;
        var s = _settings.Discord;
        _discordSwitch.IsChecked = s.Enabled;
        ShowDiscordStateLine();
        if (_discordForNow is not null)
        {
            var hidden = _discord?.HiddenForNow == true;
            _discordForNow.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new Icon { Kind = hidden ? "eye" : "eye-off", Width = 15, Height = 15, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = hidden ? L("Remettre", "Show again") : L("Masquer pour l’instant", "Hide for now"), VerticalAlignment = VerticalAlignment.Center } } };
            AutomationProperties.SetName(_discordForNow, hidden ? L("Remettre mon statut Discord", "Show my Discord status again") : L("Masquer mon statut Discord pour l’instant", "Hide my Discord status for now"));
            _discordForNow.IsEnabled = s.Enabled;
        }
        if (_discordHiddenCount is not null)
            _discordHiddenCount.Text = s.Hidden.Count == 0 ? L("Aucun", "None") : Lang.Count(s.Hidden.Count, "titre jamais montré", "titres jamais montrés", "title never shown", "titles never shown");
        if (_discordHiddenList is { Visibility: Visibility.Visible }) FillDiscordHidden();
    }

    private void ShowDiscordStateLine()
    {
        if (_discordState is null) return;
        var s = _settings.Discord;
        var state = !s.Enabled ? DiscordState.Off : _discord?.State ?? DiscordState.Idle;
        _discordState.Text = DiscordPreview.StateLine(state, _discord?.Error);
        _discordState.Foreground = Palette.Get(state == DiscordState.Live ? "Success" : "TextSecondary");
    }

    /// <summary>The tabs (what plays first, while something does) and the card of the one chosen.</summary>
    private void RefreshDiscordPreview()
    {
        if (_discordCardHost is null || _discordTabs is null || SettingsOverlay.Visibility != Visibility.Visible || DiscordPage.Visibility != Visibility.Visible) return;
        var playing = DiscordPlaying();
        var tabs = (playing is null ? DiscordPreview.Tabs : DiscordPreview.Tabs.Prepend(NowTab)).ToArray();
        if (!tabs.Contains(_discordTab)) _discordTab = tabs[0];
        if (_discordTabs.Children.Count == 0 || (_discordTabs.Tag as string) != string.Join(",", tabs))
        {
            _discordTabs.Tag = string.Join(",", tabs);
            var track = new ChoiceTrack();
            foreach (var tab in tabs)
            {
                var part = new RadioButton { Style = (Style)FindResource("PlayerSpeedChoice"), GroupName = "discord-preview", Content = tab == NowTab ? L("En cours", "Now") : DiscordPreview.Name(tab), IsChecked = tab == _discordTab, Padding = new Thickness(6, 0, 6, 0) };
                part.Checked += (_, _) => { _discordTab = tab; DrawDiscordCard(); };
                track.Children.Add(part);
            }
            _discordTabs.Children.Clear();
            _discordTabs.Children.Add(new Border { Style = (Style)FindResource("PlayerSlider"), Margin = new Thickness(0), Child = track });
        }
        DrawDiscordCard();
    }

    private void DrawDiscordCard()
    {
        if (_discordCardHost is null) return;
        var playback = _discordTab == NowTab ? DiscordPlaying() : DiscordPreview.Example(_discordTab);
        var generation = ++_discordPreviewGeneration;
        _discordCardHost.Children.Clear();
        var card = playback is null ? null : DiscordPreview.Card(_settings.Discord, playback, DateTimeOffset.UtcNow, "Windows");
        if (card is null || playback is null)
        {
            _discordCardHost.Children.Add(new TextBlock { Text = L("Rien sur Discord pour ce titre : sa bibliothèque, ou le titre lui-même, est masqué (ou c’est une bande-annonce).", "Nothing on Discord for this one: its library, or the title itself, is hidden (or it’s a trailer)."), Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(2, 0, 2, 0) });
            return;
        }
        var example = _discordTab != NowTab;
        var picture = new Squircle { Width = 64, Height = 64, CornerRadius = new CornerRadius(7), Background = Palette.Get("Field") };
        if (card.HasPoster)
        {
            picture.Child = new Icon { Kind = playback.Item.Type == "Episode" ? "series" : "film", Width = 24, Height = 24, Foreground = Palette.Get("TextSecondary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (!example && _images is { } images) _ = DrawDiscordPosterAsync(images, playback.Item, picture, generation);
        }
        else picture.Child = new Icon { Kind = "mark", Width = 30, Height = 30, Foreground = Palette.Get("Text"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var art = new Grid { Width = 72, Height = 70, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0), Children = { new Border { Child = picture, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top } } };
        if (card.SmallImage is not null)
        {
            var kind = card.SmallImage == DiscordPresence.PauseSign ? "pause" : card.SmallImage == DiscordPresence.PlaySign ? "play" : "mark";
            var badge = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Palette.Get("Bg"), BorderBrush = Palette.Get("Raised"), BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, ToolTip = card.SmallText,
                Child = new Icon { Kind = kind, Width = 12, Height = 12, Foreground = Palette.Get("Text"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            art.Children.Add(badge);
        }
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = card.Details, FontWeight = FontWeights.Bold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = card.LargeText });
        if (card.State is { } state) words.Children.Add(new TextBlock { Text = state, FontSize = 12, Foreground = Palette.Get("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0), ToolTip = state });
        if (card.Start is { } start && card.End is { } end)
        {
            var total = Math.Max(1, (end - start).TotalSeconds); var done = Math.Clamp((DateTimeOffset.UtcNow - start).TotalSeconds, 0, total);
            var bar = new Grid { Height = 3, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(done, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(total - done, GridUnitType.Star) });
            var track = new Border { CornerRadius = new CornerRadius(1.5), Background = Palette.Get("LineStrong") }; Grid.SetColumnSpan(track, 2);
            bar.Children.Add(track); bar.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = Palette.Get("Text") });
            var times = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            times.Children.Add(DockLeft(new TextBlock { Text = TimeLabel(done), FontSize = 11, Foreground = Palette.Get("Muted") }));
            times.Children.Add(DockRight(new TextBlock { Text = TimeLabel(total), FontSize = 11, Foreground = Palette.Get("Muted") }));
            times.Children.Add(bar);
            words.Children.Add(times);
        }
        var body = new DockPanel { Children = { DockLeft(art), words } };
        var inside = new StackPanel();
        inside.Children.Add(new TextBlock { Text = L("Regarde Mira", "Watching Mira"), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("Muted"), Margin = new Thickness(0, 0, 0, 8) });
        inside.Children.Add(body);
        foreach (var button in card.Buttons)
            inside.Children.Add(new Border
            {
                Background = Palette.Get("Field"), CornerRadius = new CornerRadius(5), Height = 30, Margin = new Thickness(0, 8, 0, 0), ToolTip = button.Url,
                Child = new TextBlock { Text = button.Label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
        _discordCardHost.Children.Add(new Border { Background = Palette.Get("Raised"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = inside });

        // The member list's line under the name.
        var person = _client?.Connection.UserName is { Length: > 0 } name ? name : "Alex";
        var dot = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Palette.Get("LineStrong"), Margin = new Thickness(0, 0, 10, 0), Child = new TextBlock { Text = person[..1].ToUpperInvariant(), FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = person, FontWeight = FontWeights.SemiBold, FontSize = 12.5 }, new TextBlock { Text = DiscordPreview.MemberLine(card), FontSize = 11.5, Foreground = Palette.Get("Muted"), TextTrimming = TextTrimming.CharacterEllipsis } } };
        _discordCardHost.Children.Add(new Border { Background = Palette.Get("Raised"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 10, 50, 0), Child = new DockPanel { Children = { DockLeft(dot), lines } } });
        if (example)
            _discordCardHost.Children.Add(new TextBlock { Text = L("Exemple : Discord montre l’affiche de TMDB du titre.", "Example: Discord shows the title’s TMDB poster."), FontSize = 11.5, Foreground = Palette.Get("Faint"), Margin = new Thickness(2, 8, 0, 0) });
    }

    /// <summary>What plays: its poster as Mira already has it from Jellyfin (Discord fetches its own copy from TMDB).</summary>
    private async Task DrawDiscordPosterAsync(ImageCache images, MediaItem item, Squircle picture, int generation)
    {
        try
        {
            var image = await images.GetAsync(item, targetWidth: 128);
            if (image is null || generation != _discordPreviewGeneration) return;
            picture.Child = null; picture.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        }
        catch (Exception ex) when (IsExpected(ex)) { }
    }
}
