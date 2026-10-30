using System.Windows;
using System.Windows.Controls;
using Mira.Core;
using Mira.Desktop.Views;
using static Mira.Core.Lang;

namespace Mira.Desktop;

/// <summary>
/// What sets Films and Séries apart from a plain grid: their genres as chips under the tools, and on top the title to
/// pick up (Catalog.PickUp), the film last started or the episode a show is at.
/// </summary>
public partial class MainWindow
{
    private MediaItem? _pickUp;
    private void RenderPickUp(string search)
    {
        var item = _catalog.Loading && _catalog.Items.Count == 0 ? null : _catalog.PickUp(search, CurrentQuery());
        PickUp.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (item is null || item.Id == _pickUp?.Id && item.UserData.PlaybackPositionTicks == _pickUp.UserData.PlaybackPositionTicks) { _pickUp = item; return; }
        var image = item.Id != _pickUp?.Id; _pickUp = item;
        PickUpLabel.Text = Catalog.PickUpLabel(item);
        PickUpTitle.Text = item.Type == "Episode" ? item.SeriesName ?? item.Name : item.Name;
        PickUpLine.Text = Catalog.PickUpLine(item);
        PickUpPlay.Content = ActionLabel("play", item.Progress > 0 ? L("Reprendre", "Resume") : L("Regarder", "Watch"));
        if (image) { PickUpImage.ImageSource = null; _ = LoadPickUpImageAsync(item); }
    }
    private async Task LoadPickUpImageAsync(MediaItem item)
    {
        if (_images is not { } cache) return;
        var image = await cache.GetAsync(item, true);
        if (_pickUp?.Id == item.Id && image is not null) { PickUpImage.ImageSource = image; Motion.Fade(PickUp, 1, 200, 0.85, Motion.Soft); }
    }
    private async void PickUpPlay_Click(object sender, RoutedEventArgs e) { if (_pickUp is { } item) await PlayAsync(item, chosen: true); }
    private async void PickUpDetails_Click(object sender, RoutedEventArgs e) { if (_pickUp is { } item) await ShowDetailsAsync(await PageItemAsync(item)); }

    /// <summary>« Tous », then each genre of the page: the one chosen lit like the rail's page.</summary>
    private void RenderGenreChips()
    {
        var shown = _catalog.View is "Movie" or "Series" && GenreFilter.Items.Count > 1 && _catalog.Person is null && _catalog.Studio is null && _catalog.Saga is null;
        GenreChipsScroll.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown) return;
        var chosen = Choice(GenreFilter);
        var genres = GenreFilter.Items.OfType<ComboBoxItem>().Select(x => (Tag: x.Tag?.ToString() ?? "", Name: x.Content?.ToString() ?? "")).ToList();
        if (!GenreChips.Children.OfType<Button>().Select(x => x.Tag as string).SequenceEqual(genres.Select(x => x.Tag)))
        {
            GenreChips.Children.Clear();
            foreach (var (tag, name) in genres)
            {
                var chip = new Button { Content = name, Tag = tag, Style = (Style)FindResource("Quiet"), Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0), FontSize = 13 };
                Motion.SetRadius(chip, new CornerRadius(16));
                chip.Click += (_, _) => { if (GenreFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (x.Tag?.ToString() ?? "") == tag) is { } item) GenreFilter.SelectedItem = item; };
                GenreChips.Children.Add(chip);
            }
        }
        foreach (var chip in GenreChips.Children.OfType<Button>())
        {
            var on = (chip.Tag as string) == chosen;
            if (on) { chip.Background = Palette.Get("Text"); chip.Foreground = Palette.Get("OnAccent"); } else { chip.ClearValue(BackgroundProperty); chip.ClearValue(ForegroundProperty); }
            System.Windows.Automation.AutomationProperties.SetName(chip, on ? L($"{chip.Content}, affiché", $"{chip.Content}, shown") : chip.Content as string);
        }
    }
}
