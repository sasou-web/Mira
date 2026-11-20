using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// The next episode during the countdown, as on Windows (maquette A, 2026-11-14): its picture (blurred while « Cacher les
/// spoilers » hides it) with a ring that fills around Play, its number, name and length, « Lire maintenant » and
/// « Annuler »; after « Annuler », the pill alone.
/// </summary>
public sealed partial class PlayerPage
{
    private Border _nextCard = null!;
    private readonly Image _nextImage = new() { Stretch = Stretch.UniformToFill };
    private readonly NextRing _nextRing = new() { Width = 48, Height = 48 };
    private readonly TextBlock _nextCount = new() { FontSize = 13, Foreground = Ui.Muted, FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") } };
    private readonly TextBlock _nextTitle = new() { FontSize = 15.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.Ink, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) };
    private readonly TextBlock _nextDetails = new() { FontSize = 12.5, Foreground = Ui.Faint, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
    private Button _nextPicture = null!;
    private string? _nextCardFor;

    private Control MakeNextCard()
    {
        var play = new Grid { Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { _nextRing, new Icon("play", 18) { Margin = new Thickness(3, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } } };
        var picture = new Border { Width = 192, Height = 108, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = Ui.Raised2, Child = new Grid { Children = { _nextImage, play } } };
        _nextPicture = new Button { Content = picture, Classes = { "card" }, Padding = default };
        _nextPicture.Click += (_, _) => PlayNextNow();
        Avalonia.Automation.AutomationProperties.SetName(_nextPicture, L("Lire l’épisode suivant maintenant", "Play the next episode now"));
        var now = new Button
        {
            Classes = { "primary" }, Padding = new Thickness(14, 6, 16, 6), CornerRadius = new CornerRadius(17), FontSize = 14, FontWeight = FontWeight.SemiBold,
            Content = Ui.Row(8, new Icon("play", 13) { Foreground = Ui.Brush("#0B0B0B"), VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = L("Lire maintenant", "Play now"), VerticalAlignment = VerticalAlignment.Center })
        };
        now.Click += (_, _) => PlayNextNow();
        var cancel = new Button { Classes = { "quiet" }, Content = L("Annuler", "Cancel"), Padding = new Thickness(14, 6), CornerRadius = new CornerRadius(17), FontSize = 14, FontWeight = FontWeight.SemiBold, BorderBrush = Ui.LineStrong, BorderThickness = new Thickness(1), Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => StayForAll();
        Avalonia.Automation.AutomationProperties.SetName(cancel, L("Annuler l’épisode suivant automatique", "Cancel the automatic next episode"));
        var buttons = Ui.Row(0, now, cancel); buttons.Margin = new Thickness(0, 9, 0, 0);
        var text = new StackPanel { Margin = new Thickness(18, 2, 4, 0), Children = { _nextCount, _nextTitle, _nextDetails, buttons } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("192,*") };
        grid.Children.Add(_nextPicture); Grid.SetColumn(text, 1); grid.Children.Add(text);
        _nextCard = new Border { Width = 452, Height = 128, Padding = new Thickness(10), CornerRadius = new CornerRadius(16), Background = Ui.Surface, BorderBrush = Ui.MenuLine, BorderThickness = new Thickness(1), Child = grid, IsVisible = false };
        return _nextCard;
    }
    private void PlayNextNow()
    {
        if (_skips.Next is not { } next || _mpv is null || !_loaded) return;
        _ = PlayNextAsync(next, completed: true);
    }
    private void UpdateNextCard(bool show)
    {
        if (show && _skips.Next is { } next)
        {
            if (_nextCardFor != next.Id) FillNextCard(next);
            if (_skips.CountdownText(_session.Position) is { } count && _nextCount.Text != count) _nextCount.Text = count;
            _nextRing.Value = _skips.CountdownDone(_session.Position);
        }
        _nextCard.IsVisible = show;
    }
    private void FillNextCard(MediaItem next)
    {
        _nextCardFor = next.Id;
        _nextTitle.Inlines = [];
        if (next.IndexNumber is { } number) { _nextTitle.Inlines.Add(new Run(number.ToString(Culture)) { Foreground = Ui.Faint }); _nextTitle.Inlines.Add(new Run("  ")); }
        _nextTitle.Inlines.Add(new Run(next.Name));
        _nextDetails.Text = next.DurationLabel;
        Avalonia.Automation.AutomationProperties.SetName(_nextPicture, L("Lire maintenant : ", "Play now: ") + PlayerText.Subtitle(next));
        _nextImage.Source = null;
        if (Spoilers.Hides(next, _shell.Settings.HideSpoilers)) _nextImage.Source = BlurHash.Of(next, Spoilers.Still(next)) is { } hash ? Cards.Blur(hash, 16 / 9d) : null;
        else if (_shell.Session is { } session) _ = LoadNextPictureAsync(session, next);
    }
    private async Task LoadNextPictureAsync(Services.Session session, MediaItem next)
    {
        var picture = await session.Images.EpisodeAsync(next, 384);
        if (_nextCardFor == next.Id) _nextImage.Source = picture;
    }
    /// <summary>For the self-check: the card as the credits of an episode with a next one show it, then « Annuler ».</summary>
    internal (bool Shown, string Count, string Title, double Ring) NextCardForCheck(MediaItem next)
    {
        var at = _session.Position;
        // Credits that run to the end, so that they lead to the next episode and count down.
        var end = Math.Max(_session.Duration, at + 60);
        _skips.Next = next; _skips.Segments = [new(SkipKind.Outro, at, end)]; _skips.Active = null; _skips.Stay = false;
        _skips.Offer(at, end, offering: true, autoNext: true); UpdateSkip();
        return (_nextCard.IsVisible && !_skip.IsVisible && !_skipCancel.IsVisible, _nextCount.Text ?? "", _nextTitle.Inlines?.Text ?? "", _nextRing.Value);
    }
    internal (bool Card, bool Pill) CancelNextForCheck()
    {
        _skipText.Text = _skips.Cancel(); ShowControls(); UpdateSkip();
        var seen = (_nextCard.IsVisible, _skip.IsVisible);
        _skips.Next = null; _skips.Segments = []; _skips.Active = null; _skips.Stay = false; UpdateSkip();
        return seen;
    }

    /// <summary>The ring around Play: its track, then how much of the countdown has gone, clockwise from the top.</summary>
    private sealed class NextRing : Control
    {
        private static readonly IBrush Fill = Ui.Brush("#73000000");
        private static readonly IPen Track = new Pen(Ui.Brush("#4DFFFFFF"), 2.5), Done = new Pen(Ui.Ink, 2.5, lineCap: PenLineCap.Round);
        private double _value;
        public double Value { get => _value; set { if (Math.Abs(value - _value) < .001) return; _value = value; InvalidateVisual(); } }
        public override void Render(DrawingContext context)
        {
            const double centre = 24, radius = 24 - 1.25;
            context.DrawEllipse(Fill, Track, new Point(centre, centre), radius, radius);
            if (_value <= .001) return;
            if (_value >= .999) { context.DrawEllipse(null, Done, new Point(centre, centre), radius, radius); return; }
            var angle = _value * 2 * Math.PI;
            var geometry = new StreamGeometry();
            using (var draw = geometry.Open())
            {
                draw.BeginFigure(new Point(centre, centre - radius), false);
                draw.ArcTo(new Point(centre + radius * Math.Sin(angle), centre - radius * Math.Cos(angle)), new Size(radius, radius), 0, _value > .5, SweepDirection.Clockwise);
                draw.EndFigure(false);
            }
            context.DrawGeometry(null, Done, geometry);
        }
    }
}
