using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// « Sous-titres d'une saison » (maquette A « L'écrin », ogsas 2026-11-21): the button beside the season's counter takes a
// .zip or several subtitle files, the window under it shows each file on its episode (SeasonSubtitles of Core), one
// that gives none in orange with a list to choose it, then « Keep 6 on Jellyfin » sends them one by one, each said
// kept only once Jellyfin lists it. For an account allowed to manage subtitles.
public partial class MainWindow
{
    private Popup? _packPopup;

    /// <summary>The button on a season the library has, once Jellyfin says this account may add subtitles; never in the demo.</summary>
    private async Task ShowSeasonSubtitlesAsync(EpisodePage? page)
    {
        SeasonSubtitlesButton.Visibility = Visibility.Collapsed;
        if (_demo || page is not { Absent: false } || _client is not { } client || !SeasonEpisodes(page.Season).Any()) return;
        var name = SeasonSubtitles.ButtonName(page.Season);
        SeasonSubtitlesButton.ToolTip = name; AutomationProperties.SetName(SeasonSubtitlesButton, name);
        if (_subtitleRight is not { } known || known.Client != client) _subtitleRight = (client, client.CanManageSubtitlesAsync(_connectionLifetime.Token));
        bool? allowed;
        try { allowed = await _subtitleRight.Value.Allowed; }
        catch (Exception e) when (IsExpected(e)) { _subtitleRight = null; return; }
        if (allowed != false && client == _client && (SeasonSelector.SelectedItem as ComboBoxItem)?.Tag is EpisodePage { Absent: false } shown && shown.Season == page.Season && !ExtrasShown)
            SeasonSubtitlesButton.Visibility = Visibility.Visible;
    }
    /// <summary>A season's episodes the library has (TheTVDB's missing ones left out), by their numbers.</summary>
    private IEnumerable<MediaItem> SeasonEpisodes(int season) =>
        _episodes.Where(x => EpisodePages.Season(x) == season && x.IndexNumber is not null && x.LocationType != "Virtual").OrderBy(x => x.IndexNumber);

    private async void SeasonSubtitles_Click(object sender, RoutedEventArgs e)
    {
        if ((SeasonSelector.SelectedItem as ComboBoxItem)?.Tag is not EpisodePage { Absent: false } page || _detail is not { Type: "Series" } series || _client is not { } client) return;
        var dialog = new OpenFileDialog { Multiselect = true, Filter = SeasonSubtitles.PickerFilter, Title = SeasonSubtitles.PickerTitle(page.Season) };
        if (dialog.ShowDialog(this) != true) return;
        IReadOnlyList<PackFile> files;
        try { files = await SeasonSubtitles.ReadAsync(dialog.FileNames); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { SetError(SeasonSubtitles.Unreadable); return; }
        if (files.Count == 0) { SetError(SeasonSubtitles.NoneFound); return; }
        if (_detail?.Id != series.Id) return;
        var episodes = SeasonEpisodes(page.Season).ToList();
        var matches = SeasonSubtitles.Match(files, episodes, page.Season, SeasonSubtitles.Before(_episodes, page.Season), _settings.SubtitleLanguage);
        OpenPackPopup(client, series, page.Season, episodes, matches);
    }

    private void OpenPackPopup(JellyfinClient client, MediaItem series, int season, IReadOnlyList<MediaItem> episodes, IReadOnlyList<PackMatch> found)
    {
        if (_packPopup is { IsOpen: true }) _packPopup.IsOpen = false;
        var anchor = SeasonSubtitlesButton;
        var body = new Squircle { Style = (Style)FindResource("PopoverWindow"), Width = MissingMenuWidth };
        TextElement.SetFontFamily(body, (FontFamily)FindResource("AppFont")); TextElement.SetForeground(body, Palette.Get("Text"));
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Custom, StaysOpen = true, AllowsTransparency = true, Child = body, PopupAnimation = PopupAnimation.None };
        popup.CustomPopupPlacementCallback = (size, _, _) => [new CustomPopupPlacement(MissingMenuPlace(anchor, size), PopupPrimaryAxis.None)];
        FitMissingMenu(anchor, body);
        void Refit(object? s, EventArgs e) { if (!popup.IsOpen) return; FitMissingMenu(anchor, body); NudgeMissingMenu(popup); }
        ScrollChangedEventHandler scrolled = Refit;
        SizeChanged += Refit; AddHandler(ScrollViewer.ScrollChangedEvent, scrolled);
        var sending = false;
        // Closed by hand, not by StaysOpen = false: a file's episode list is a popup of its own, and closing it (or a click on
        // « Keep » while it was open) closed the whole window, the sending then going on unseen (seen on the Dell, 2026-11-21).
        // A click in the window or leaving it closes it, never while sending.
        void Outside(object? s, EventArgs e) { if (!sending) popup.IsOpen = false; }
        MouseButtonEventHandler pressed = Outside;
        PreviewMouseDown += pressed; Deactivated += Outside;
        popup.Closed += (_, _) => { SizeChanged -= Refit; RemoveHandler(ScrollViewer.ScrollChangedEvent, scrolled); PreviewMouseDown -= pressed; Deactivated -= Outside; if (ReferenceEquals(_packPopup, popup)) _packPopup = null; };
        body.KeyDown += (_, e) => { if (e.Key == Key.Escape && !sending) { popup.IsOpen = false; e.Handled = true; anchor.Focus(); } };

        // Each file and its episode, as chosen now: a file without one goes once one is chosen in its list.
        var chosen = found.ToDictionary(x => x, x => x.Episode);
        var lead = new TextBlock { FontSize = 13, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var go = new Button { Style = (Style)FindResource("Primary"), Height = 42, Padding = new Thickness(20, 0, 20, 0) };
        var cancel = new Button { Style = (Style)FindResource("Quiet"), Content = SeasonSubtitles.Cancel, Height = 42, Margin = new Thickness(0, 0, 8, 0) };
        var result = new TextBlock { FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Get("TextSecondary"), Visibility = Visibility.Collapsed, Margin = new Thickness(14, 10, 14, 0) };
        var bar = new ProgressBar { Height = 4, Minimum = 0, Visibility = Visibility.Collapsed, Margin = new Thickness(14, 10, 14, 0) };
        void Count()
        {
            var ready = chosen.Count(x => x.Value is not null);
            lead.Text = SeasonSubtitles.Lead([.. found.Select(x => x with { Episode = chosen[x] })]);
            go.Content = SeasonSubtitles.Go(ready); go.IsEnabled = ready > 0;
        }

        var root = new StackPanel();
        var head = new StackPanel { Margin = new Thickness(14, 4, 14, 10) };
        head.Children.Add(new TextBlock { Text = SeasonSubtitles.Heading(season), FontSize = 16, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
        head.Children.Add(lead);
        root.Children.Add(head);
        var list = new StackPanel();
        // A file without an episode is offered only the episodes no other file's name gives.
        var free = episodes.Where(x => !found.Any(m => m.Episode?.Id == x.Id)).ToList();
        foreach (var match in found) list.Children.Add(PackRow(match, free, chosen, Count));
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(bar); root.Children.Add(result);
        root.Children.Add(new Border { Style = (Style)FindResource("PlayerMenuRule"), Margin = new Thickness(6, 10, 6, 0) });
        var foot = new DockPanel { Margin = new Thickness(14, 10, 6, 4), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(buttons, Dock.Right);
        buttons.Children.Add(cancel); buttons.Children.Add(go); foot.Children.Add(buttons);
        var note = new TextBlock { Text = L("Tes autres appareils les proposent aussi.", "Your other devices offer them too."), FontSize = 12.5, Foreground = Palette.Get("Muted"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        foot.Children.Add(note);
        root.Children.Add(foot);
        body.Child = root;
        Count();

        cancel.Click += (_, _) => { if (!sending) popup.IsOpen = false; };
        go.Click += async (_, _) =>
        {
            if (sending) return;
            if (result.Visibility == Visibility.Visible) { popup.IsOpen = false; return; }
            var send = found.Where(x => chosen[x] is not null).Select(x => x with { Episode = chosen[x] }).ToList();
            sending = true; list.IsEnabled = false; cancel.Visibility = Visibility.Collapsed; go.IsEnabled = false;
            bar.Maximum = send.Count; bar.Value = 0; bar.Visibility = Visibility.Visible;
            var sent = new List<(PackMatch, SubtitleKept, bool)>();
            try
            {
                foreach (var match in send)
                {
                    lead.Text = SeasonSubtitles.Sending(sent.Count + 1, send.Count);
                    var upload = new SubtitleUpload(JellyfinSubtitles.FormatOf(match.File.Data, match.Guess.Format, match.Guess.Language), match.Guess.Language, match.Guess.Forced, match.Guess.HearingImpaired, match.File.Data);
                    var (kept, listed) = await JellyfinSubtitles.KeepAsync(client, match.Episode!.Id, null, upload, null, TimeSpan.FromSeconds(20), _connectionLifetime.Token);
                    sent.Add((match, kept, listed)); bar.Value = sent.Count;
                    // Refused for one is refused for all: the account may not add subtitles.
                    if (kept == SubtitleKept.Refused) break;
                }
                result.Text = SeasonSubtitles.Result(sent);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                result.Text = (sent.Count > 0 ? SeasonSubtitles.Result(sent) + "\n" : "") + JellyfinSubtitles.NotKeptText + Friendly(ex);
            }
            var failed = sent.Count < send.Count || sent.Any(x => !JellyfinSubtitles.IsKept(x.Item2));
            result.Foreground = Palette.Get(failed ? "Danger" : "TextSecondary");
            lead.Text = SeasonSubtitles.Lead(send);
            bar.Visibility = Visibility.Collapsed; result.Visibility = Visibility.Visible; note.Visibility = Visibility.Hidden;
            go.Content = SeasonSubtitles.Done; go.IsEnabled = true; sending = false; go.Focus();
            Accessible.Say(result);
            _metadata?.Clear();
        };
        _packPopup = popup;
        popup.IsOpen = true;
        Dispatcher.BeginInvoke(() => go.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>A file of the pack: its episode (or a list to choose it), its name, its language, and whether it goes.</summary>
    private FrameworkElement PackRow(PackMatch match, IReadOnlyList<MediaItem> episodes, Dictionary<PackMatch, MediaItem?> chosen, Action changed)
    {
        var row = new DockPanel { Margin = new Thickness(14, 0, 10, 0), MinHeight = 36 };
        var code = new TextBlock { Width = 52, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Text = match.Episode is { } own ? $"E{own.IndexNumber:00}" : "—", Foreground = Palette.Get(match.Episode is null ? "Faint" : "Text") };
        DockPanel.SetDock(code, Dock.Left); row.Children.Add(code);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) }; DockPanel.SetDock(right, Dock.Right);
        right.Children.Add(new Border
        {
            BorderBrush = Palette.Get("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center, ToolTip = PlayerText.Language(match.Guess.Language),
            Child = new TextBlock { Text = SeasonSubtitles.Pill(match.Guess.Language), FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = Palette.Get("TextBody") }
        });
        var file = new TextBlock { Text = match.File.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = match.File.Name, Foreground = Palette.Get(match.Episode is null ? "Warning" : "TextSecondary") };
        if (match.Episode is null)
        {
            // Nothing in the name: the list of the season's episodes, « Don't send » first.
            var pick = new ComboBox { MinWidth = 170, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(pick, SeasonSubtitles.Choose + " · " + match.File.Name);
            pick.Items.Add(new ComboBoxItem { Content = SeasonSubtitles.Choose, Tag = null });
            foreach (var episode in episodes) pick.Items.Add(new ComboBoxItem { Content = $"E{episode.IndexNumber:00}  {episode.Name}", Tag = episode });
            pick.SelectedIndex = 0;
            pick.SelectionChanged += (_, _) =>
            {
                var to = (pick.SelectedItem as ComboBoxItem)?.Tag as MediaItem;
                chosen[match] = to; code.Text = to is null ? "—" : $"E{to.IndexNumber:00}"; code.Foreground = Palette.Get(to is null ? "Faint" : "Text");
                file.Foreground = Palette.Get(to is null ? "Warning" : "TextSecondary"); changed();
            };
            right.Children.Add(pick);
        }
        else right.Children.Add(new Icon { Kind = "check", Width = 15, Height = 15, Margin = new Thickness(10, 0, 0, 0), Foreground = Palette.Get("Success"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(right); row.Children.Add(file);
        AutomationProperties.SetName(row, $"{code.Text} · {match.File.Name} · {PlayerText.Language(match.Guess.Language) ?? match.Guess.Language}");
        return row;
    }
}
