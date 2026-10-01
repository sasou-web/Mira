using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    private readonly CarouselTimeline _carouselTime = new(6.5);
    private readonly SolidColorBrush _heroAccent = new(Color.FromRgb(245, 245, 247));
    private readonly List<CarouselProgress> _heroSegments = [];
    private readonly Dictionary<string, Task<MediaItem?>> _heroSeries = [];
    private Task _heroReady = Task.CompletedTask;
    private bool _heroBusy, _heroClockAttached;
    private int _heroVersion;
    private long _heroLastFrame;
    private string? _heroRequestedId;
    private void RenderHero(MediaItem item) => _heroReady = SelectHeroAsync(item);
    private async Task SelectHeroAsync(MediaItem item)
    {
        var version = ++_heroVersion; _heroRequestedId = item.Id; _heroBusy = true;
        try
        {
            var changed = _hero?.Id != item.Id; var first = _hero is null;
            ImageSource? image = _demo ? DemoLibrary.Artwork(Math.Max(0, _items.FindIndex(x => x.Id == item.Id)), true) : null;
            ImageSource? logo = null; MediaItem? info = null;
            if (_hero is null) UpdateHeroCopy(item, null, null);
            if (_images is { } cache)
            {
                var imageTask = cache.GetAsync(item, true); var logoTask = cache.LogoAsync(item);
                var infoTask = item.SeriesId is { } id && _client is { } client ? LoadHeroSeriesAsync(client, id) : Task.FromResult<MediaItem?>(null);
                await Task.WhenAll(imageTask, logoTask, infoTask);
                image = imageTask.Result; logo = logoTask.Result; info = infoTask.Result;
            }
            // The accent sampling decodes a thumbnail: do it off the UI thread so the crossfade starts on time.
            var accent = image is Freezable { IsFrozen: false } ? ArtworkAccent.From(image) : await Task.Run(() => ArtworkAccent.From(image));
            if (version != _heroVersion) return;
            _hero = item; _heroSelectedId = item.Id;
            HeroPlay.Background = _heroAccent;
            _heroAccent.BeginAnimation(SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation(accent, TimeSpan.FromMilliseconds(Motion.Reduced ? 0 : 650)));
            if (changed)
            {
                HeroPreviousImage.BeginAnimation(OpacityProperty, null); HeroPreviousImage.Opacity = 1;
                HeroPreviousImage.Source = HeroImage.Source; HeroImage.Source = image;
                if (image is not null) Motion.Fade(HeroImage, 1, 650, HeroPreviousImage.Source is null ? .2 : 0, Motion.Soft);
                // No artwork for this title: let the previous one fade away rather than sit under the new name.
                else Motion.Fade(HeroPreviousImage, 0, 500);
                _carouselTime.Reset(); BuildHeroDots();
                // Text leaves briefly, then the new title rises in: no hard cut between two titles.
                if (HeroCopy.Opacity > .01 && !Motion.Reduced && !first)
                {
                    Motion.Fade(HeroCopy, 0, 140); Motion.Fade(HeroSynopsis, 0, 140);
                    await Task.Delay(140); if (version != _heroVersion) return;
                }
                UpdateHeroCopy(item, info, logo); Motion.Reveal(HeroCopy, 360, 9); Motion.Reveal(HeroSynopsis, 400, 6);
                if (logo is not null) Motion.Fade(HeroLogo, 1, 420, 0, Motion.Soft);
                HeroSynopsis.Visibility = ActualWidth >= 1380 ? Visibility.Visible : Visibility.Collapsed;
            }
            else UpdateHeroCopy(item, info, logo);
            foreach (var candidate in _heroCandidates) _ = WarmHeroAsync(candidate);
        }
        finally { if (version == _heroVersion) { _heroBusy = false; UpdateHeroClock(); } }
    }
    private void UpdateHeroCopy(MediaItem item, MediaItem? series, ImageSource? logo)
    {
        HeroTitle.Text = item.DisplayTitle; HeroTitle.Visibility = logo is null ? Visibility.Visible : Visibility.Collapsed;
        HeroLogo.Source = logo; HeroLogo.Visibility = logo is null ? Visibility.Collapsed : Visibility.Visible;
        HeroOverview.Text = HeroSmallOverview.Text = PlainText(string.IsNullOrWhiteSpace(item.Overview) ? series?.Overview ?? "" : item.Overview);
        HeroPlayText.Text = item.Progress > 0 && !item.UserData.Played && _settings.RememberPosition ? "Reprendre" : "Regarder";
        HeroMetaPills.Children.Clear();
        if (item.ProductionYear is { } year) AddPill(HeroMetaPills, year.ToString());
        AddPill(HeroMetaPills, item.Type switch { "Series" => "Série", "Episode" => $"S{item.ParentIndexNumber ?? 1:00} · E{item.IndexNumber ?? 1:00}", _ => "Film" });
        if (item.DurationLabel.Length > 0) AddPill(HeroMetaPills, item.DurationLabel);
        if (item.Type != "Episode" && item.CommunityRating is > 0) AddPill(HeroMetaPills, $"★  {item.CommunityRating:0.0}", "#A8D5B1");
        HeroGenrePills.Children.Clear(); foreach (var genre in (series ?? item).Genres.Take(3)) AddPill(HeroGenrePills, genre);
        foreach (var pill in HeroMetaPills.Children.OfType<Border>().Concat(HeroGenrePills.Children.OfType<Border>())) if (pill.Child is TextBlock label) label.Foreground = _heroAccent;
        var favorite = (series ?? item).UserData.IsFavorite;
        HeroFavorite.Foreground = favorite ? _heroAccent : Brushes.White;
        HeroFavorite.ToolTip = favorite ? "Retirer des favoris" : "Ajouter aux favoris";
        System.Windows.Automation.AutomationProperties.SetName(HeroFavorite, (string)HeroFavorite.ToolTip);
    }
    private void BuildHeroDots()
    {
        HeroDots.Children.Clear(); _heroSegments.Clear();
        for (var i = 0; i < _heroCandidates.Count; i++)
        {
            var candidate = _heroCandidates[i];
            var track = new CarouselProgress { Width = 42, Height = 3, Accent = _heroAccent }; _heroSegments.Add(track);
            var button = new Button { Content = track, Style = (Style)FindResource("Quiet"), Width = 52, Height = 30, Padding = new Thickness(5, 10, 5, 10), BorderThickness = new Thickness(0), ToolTip = candidate.DisplayTitle };
            Motion.SetRadius(button, new CornerRadius(15));
            System.Windows.Automation.AutomationProperties.SetName(button, "À l’affiche : " + candidate.DisplayTitle);
            button.Click += (_, _) => { _heroReady = SelectHeroAsync(candidate); }; HeroDots.Children.Add(button);
        }
        UpdateHeroSegments();
    }
    private void UpdateHeroSegments()
    {
        var active = _heroCandidates.FindIndex(x => x.Id == _hero?.Id);
        var animated = _settings.HeroAutoPlay && !Motion.Reduced && _heroCandidates.Count > 1;
        for (var i = 0; i < _heroSegments.Count; i++) _heroSegments[i].Progress = animated ? i < active ? 1 : i == active ? _carouselTime.Progress : 0 : i == active ? 1 : 0;
    }
    private void UpdateHeroClock()
    {
        UpdateHeroSegments();
        var enabled = !_closing && IsActive && _view == "home" && LibraryShell.IsVisible && DetailOverlay.Visibility != Visibility.Visible && SettingsOverlay.Visibility != Visibility.Visible && TorLinkOverlay.Visibility != Visibility.Visible && LoginOverlay.Visibility != Visibility.Visible
            && GuideOverlay.Visibility != Visibility.Visible && WelcomeOverlay.Visibility != Visibility.Visible && WhatsNewOverlay.Visibility != Visibility.Visible && !_playing && _settings.HeroAutoPlay && !Motion.Reduced && _heroCandidates.Count > 1;
        if (enabled == _heroClockAttached) return;
        _heroClockAttached = enabled;
        if (enabled) { _heroLastFrame = Stopwatch.GetTimestamp(); CompositionTarget.Rendering += HeroFrame; }
        else CompositionTarget.Rendering -= HeroFrame;
    }
    private async void HeroFrame(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp(); var elapsed = Math.Min(.1, (now - _heroLastFrame) / (double)Stopwatch.Frequency); _heroLastFrame = now;
        var paused = _heroBusy || HeroCopy.IsMouseOver || HeroSynopsis.IsMouseOver || HeroDots.IsMouseOver || (_keyboardNavigation && Hero.IsKeyboardFocusWithin) || LibraryScroll.VerticalOffset > Hero.ActualHeight * .65;
        var next = _carouselTime.Advance(elapsed, paused); UpdateHeroSegments();
        if (next && !_heroBusy && _heroCandidates.Count > 1)
        {
            var index = _heroCandidates.FindIndex(x => x.Id == _hero?.Id);
            _heroReady = SelectHeroAsync(_heroCandidates[(index + 1) % _heroCandidates.Count]); await _heroReady;
        }
    }
    private async Task WarmHeroAsync(MediaItem item)
    {
        if (_images is not { } cache) return;
        await Task.WhenAll(cache.GetAsync(item, true), cache.LogoAsync(item));
    }
    private Task<MediaItem?> LoadHeroSeriesAsync(JellyfinClient client, string id)
    {
        if (_heroSeries.TryGetValue(id, out var task)) return task;
        async Task<MediaItem?> Fetch() { try { return _metadata is { } cache ? await cache.ItemAsync(id) : await client.ItemAsync(id); } catch (Exception ex) when (IsExpected(ex)) { return null; } }
        return _heroSeries[id] = Fetch();
    }
    private Task LoadHeroImageAsync(MediaItem item) => _heroReady;
}
