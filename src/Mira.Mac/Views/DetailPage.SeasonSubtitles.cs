using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// « Sous-titres d'une saison », as on Windows (MainWindow.SeasonSubtitles.cs, maquette A « L'écrin »): the button beside
// the season's counter takes a .zip or several subtitle files, the menu under it shows each file on its episode
// (SeasonSubtitles of Core), one that gives none with a list to choose it, then « Keep 6 on Jellyfin » sends them.
public sealed partial class DetailPage
{
    private readonly Button _seasonSubtitles = new() { Classes = { "icon" }, IsVisible = false, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Content = new Icon("subtitles", 18) };
    private static (JellyfinClient Client, Task<bool?> Allowed)? _subtitleRight;
    private Flyout? _packFlyout;
    private bool _packWired;

    private async Task ShowSeasonSubtitlesAsync(EpisodePage? page)
    {
        if (!_packWired) { _packWired = true; _seasonSubtitles.Click += (_, _) => _ = PickSeasonSubtitlesAsync(); }
        _seasonSubtitles.IsVisible = false;
        if (page is not { Absent: false } || Shell.Session is not { } session || !SeasonEpisodes(page.Season).Any()) return;
        var name = SeasonSubtitles.ButtonName(page.Season);
        ToolTip.SetTip(_seasonSubtitles, name); Avalonia.Automation.AutomationProperties.SetName(_seasonSubtitles, name);
        if (_subtitleRight is not { } known || known.Client != session.Client) _subtitleRight = (session.Client, session.Client.CanManageSubtitlesAsync(session.Lifetime));
        bool? allowed;
        try { allowed = await _subtitleRight.Value.Allowed; }
        catch (Exception ex) when (Errors.Expected(ex)) { _subtitleRight = null; return; }
        if (allowed != false && (_seasons.SelectedItem as ComboBoxItem)?.Tag is EpisodePage { Absent: false } shown && shown.Season == page.Season && !ExtrasShown) _seasonSubtitles.IsVisible = true;
    }
    private IEnumerable<MediaItem> SeasonEpisodes(int season) =>
        _allEpisodes.Where(x => EpisodePages.Season(x) == season && x.IndexNumber is not null && x.LocationType != "Virtual").OrderBy(x => x.IndexNumber);

    private async Task PickSeasonSubtitlesAsync()
    {
        if ((_seasons.SelectedItem as ComboBoxItem)?.Tag is not EpisodePage { Absent: false } page || Shell.Session is not { } session) return;
        var picked = await Shell.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = SeasonSubtitles.PickerTitle(page.Season), AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(L("Sous-titres ou .zip", "Subtitles or a .zip")) { Patterns = ["*.zip", "*.srt", "*.ass", "*.ssa", "*.vtt", "*.sub"] }],
        });
        var paths = picked.Select(x => x.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0) return;
        IReadOnlyList<PackFile> files;
        try { files = await SeasonSubtitles.ReadAsync(paths); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Shell.Notice(SeasonSubtitles.Unreadable, tone: NoticeTone.Error); return; }
        if (files.Count == 0) { Shell.Notice(SeasonSubtitles.NoneFound, tone: NoticeTone.Error); return; }
        var episodes = SeasonEpisodes(page.Season).ToList();
        OpenPackMenu(session, page.Season, episodes, SeasonSubtitles.Match(files, episodes, page.Season, SeasonSubtitles.Before(_allEpisodes, page.Season), Shell.Settings.SubtitleLanguage));
    }

    private void OpenPackMenu(Session session, int season, IReadOnlyList<MediaItem> episodes, IReadOnlyList<PackMatch> found)
    {
        _packFlyout?.Hide();
        var anchor = _seasonSubtitles;
        var body = new Border { Width = MissingMenuWidth };
        var flyout = new Flyout { Placement = PlacementMode.Custom, ShowMode = FlyoutShowMode.Standard, Content = body };
        flyout.CustomPopupPlacementCallback = placement => PlaceMissingMenu(anchor, placement);
        FitMissingMenu(anchor, body);
        var sending = false;
        body.KeyDown += (_, e) => { if (e.Key == Key.Escape && !sending) { flyout.Hide(); e.Handled = true; anchor.Focus(); } };
        flyout.Closing += (_, e) => { if (sending) e.Cancel = true; };

        var chosen = found.ToDictionary(x => x, x => x.Episode);
        var lead = new TextBlock { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var goLabel = new TextBlock { FontSize = 14.5, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        var go = new Button { Classes = { "primary" }, Height = 42, Padding = new Thickness(20, 0), CornerRadius = new CornerRadius(21), VerticalContentAlignment = VerticalAlignment.Center, Content = goLabel };
        var cancel = new Button { Classes = { "quiet" }, Content = SeasonSubtitles.Cancel, Height = 42, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
        var result = new TextBlock { FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap, Foreground = Ui.TextSecondary, IsVisible = false, Margin = new Thickness(14, 10, 14, 0) };
        var bar = new ProgressBar { Height = 4, MinHeight = 4, Minimum = 0, IsVisible = false, Margin = new Thickness(14, 10, 14, 0) };
        void Count()
        {
            var ready = chosen.Count(x => x.Value is not null);
            lead.Text = SeasonSubtitles.Lead([.. found.Select(x => x with { Episode = chosen[x] })]);
            goLabel.Text = SeasonSubtitles.Go(ready); go.IsEnabled = ready > 0;
        }
        var list = new StackPanel();
        // A file without an episode is offered only the episodes no other file's name gives.
        var free = episodes.Where(x => !found.Any(m => m.Episode?.Id == x.Id)).ToList();
        foreach (var match in found) list.Children.Add(PackRow(match, free, chosen, Count));
        var note = new TextBlock { Text = L("Tes autres appareils les proposent aussi.", "Your other devices offer them too."), FontSize = 12.5, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        var buttons = Ui.Row(0, cancel, go); DockPanel.SetDock(buttons, Dock.Right);
        var foot = new DockPanel { Margin = new Thickness(14, 10, 6, 4), Children = { buttons, note } };
        body.Child = new StackPanel
        {
            Children =
            {
                new StackPanel { Margin = new Thickness(14, 4, 14, 10), Children = { new TextBlock { Text = SeasonSubtitles.Heading(season), FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Ui.Ink, TextTrimming = TextTrimming.CharacterEllipsis }, lead } },
                new ScrollViewer { Content = list, MaxHeight = 360, Margin = new Thickness(0, 0, 0, 4) },
                bar, result,
                new Border { Height = 1, Background = Ui.MenuLine, Margin = new Thickness(6, 10, 6, 0) },
                foot,
            },
        };
        Count();
        cancel.Click += (_, _) => { if (!sending) flyout.Hide(); };
        go.Click += async (_, _) =>
        {
            if (sending) return;
            if (result.IsVisible) { flyout.Hide(); return; }
            var send = found.Where(x => chosen[x] is not null).Select(x => x with { Episode = chosen[x] }).ToList();
            sending = true; list.IsEnabled = false; cancel.IsVisible = false; go.IsEnabled = false;
            bar.Maximum = send.Count; bar.Value = 0; bar.IsVisible = true;
            var sent = new List<(PackMatch, SubtitleKept, bool)>();
            try
            {
                foreach (var match in send)
                {
                    lead.Text = SeasonSubtitles.Sending(sent.Count + 1, send.Count);
                    var upload = new SubtitleUpload(JellyfinSubtitles.FormatOf(match.File.Data, match.Guess.Format, match.Guess.Language), match.Guess.Language, match.Guess.Forced, match.Guess.HearingImpaired, match.File.Data);
                    var (kept, listed) = await JellyfinSubtitles.KeepAsync(session.Client, match.Episode!.Id, null, upload, null, TimeSpan.FromSeconds(20), session.Lifetime);
                    sent.Add((match, kept, listed)); bar.Value = sent.Count;
                    if (kept == SubtitleKept.Refused) break;
                }
                result.Text = SeasonSubtitles.Result(sent);
            }
            catch (Exception ex) when (Errors.Expected(ex)) { result.Text = (sent.Count > 0 ? SeasonSubtitles.Result(sent) + "\n" : "") + JellyfinSubtitles.NotKeptText + Errors.Friendly(ex); }
            var failed = sent.Count < send.Count || sent.Any(x => !JellyfinSubtitles.IsKept(x.Item2));
            result.Foreground = failed ? Ui.Danger : Ui.TextSecondary;
            lead.Text = SeasonSubtitles.Lead(send);
            bar.IsVisible = false; result.IsVisible = true; note.Opacity = 0;
            goLabel.Text = SeasonSubtitles.Done; go.IsEnabled = true; sending = false; go.Focus();
        };
        _packFlyout = flyout;
        flyout.ShowAt(anchor);
    }

    private static Control PackRow(PackMatch match, IReadOnlyList<MediaItem> episodes, Dictionary<PackMatch, MediaItem?> chosen, Action changed)
    {
        var code = new TextBlock { Width = 52, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Text = match.Episode is { } own ? $"E{own.IndexNumber:00}" : "—", Foreground = match.Episode is null ? Ui.Faint : Ui.Ink };
        var pill = new Border { BorderBrush = Ui.LineStrong, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = SeasonSubtitles.Pill(match.Guess.Language), FontSize = 11.5, FontWeight = FontWeight.Bold, Foreground = Ui.TextBody } };
        ToolTip.SetTip(pill, PlayerText.Language(match.Guess.Language));
        var file = new TextBlock { Text = match.File.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = match.Episode is null ? Ui.Warning : Ui.TextSecondary };
        ToolTip.SetTip(file, match.File.Name);
        var right = Ui.Row(10, pill); right.VerticalAlignment = VerticalAlignment.Center; right.Margin = new Thickness(10, 0, 0, 0);
        if (match.Episode is null)
        {
            var pick = new ComboBox { MinWidth = 170, VerticalAlignment = VerticalAlignment.Center };
            Avalonia.Automation.AutomationProperties.SetName(pick, SeasonSubtitles.Choose + " · " + match.File.Name);
            pick.Items.Add(new ComboBoxItem { Content = SeasonSubtitles.Choose });
            foreach (var episode in episodes) pick.Items.Add(new ComboBoxItem { Content = $"E{episode.IndexNumber:00}  {episode.Name}", Tag = episode });
            pick.SelectedIndex = 0;
            pick.SelectionChanged += (_, _) =>
            {
                var to = (pick.SelectedItem as ComboBoxItem)?.Tag as MediaItem;
                chosen[match] = to; code.Text = to is null ? "—" : $"E{to.IndexNumber:00}"; code.Foreground = to is null ? Ui.Faint : Ui.Ink;
                file.Foreground = to is null ? Ui.Warning : Ui.TextSecondary; changed();
            };
            right.Children.Add(pick);
        }
        else right.Children.Add(new Icon("check", 15) { Foreground = Ui.Success, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(code, Dock.Left); DockPanel.SetDock(right, Dock.Right);
        var row = new DockPanel { Margin = new Thickness(14, 0, 10, 0), MinHeight = 36, Children = { code, right, file } };
        Avalonia.Automation.AutomationProperties.SetName(row, $"{code.Text} · {match.File.Name} · {PlayerText.Language(match.Guess.Language) ?? match.Guess.Language}");
        return row;
    }
}
