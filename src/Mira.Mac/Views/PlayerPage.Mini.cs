using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// The mini-player, as on Windows (MainWindow.MiniPlayer.cs): « Retour », Échap or I shrink the player to a corner of the
// window, the library usable around it; dragged, it settles in the nearest corner. Its title, « Agrandir » and « Fermer »
// show while the pointer is on it; a double click or I brings the whole player back.
public sealed partial class PlayerPage
{
    private const double MiniWidth = 384, MiniHeight = 216, MiniRadius = 12, MiniGap = 20;
    private Border _frame = null!;
    private Grid _miniChrome = null!;
    private readonly TextBlock _miniTitle = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Icon _miniPauseIcon = new("pause", 19);
    private readonly ProgressLine _miniProgress = new() { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
    private bool _mini, _miniRight = true, _miniBottom = true, _miniDragging;
    private Point? _miniPress;
    /// <summary>In a corner of the window, the library usable around it.</summary>
    public bool IsMini => _mini;

    /// <summary>The frame that rounds the mini-player, and its own controls over the picture.</summary>
    private Control BuildMini(Control content)
    {
        var expand = Ui.IconButton("expand", L("Agrandir le lecteur · I", "Expand the player · I"), () => SetMini(false), "player", 17);
        var close = Ui.IconButton("close", L("Fermer le mini-lecteur", "Close the mini player"), () => _ = _shell.StopPlaybackAsync(), "player", 17);
        foreach (var button in new[] { expand, close }) { button.Width = button.Height = 32; button.CornerRadius = new CornerRadius(16); }
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), VerticalAlignment = VerticalAlignment.Top, Height = 52 };
        top.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#B0000000"), 0), new GradientStop(Color.Parse("#00000000"), 1) }
        };
        _miniTitle.Margin = new Thickness(14, 0, 8, 6);
        top.Children.Add(_miniTitle);
        expand.Margin = new Thickness(0, 0, 0, 6); close.Margin = new Thickness(2, 0, 8, 6);
        Grid.SetColumn(expand, 1); top.Children.Add(expand); Grid.SetColumn(close, 2); top.Children.Add(close);
        var play = new Button { Content = _miniPauseIcon, Classes = { "player" }, Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Background = Ui.Brush("#80000000") };
        play.Click += (_, _) => TogglePause();
        Speak(play, _miniPauseIcon, kind => kind == "play" ? L("Lecture", "Play") : L("Pause", "Pause"), L("Espace", "Space"));
        _pauseIcon.PropertyChanged += (_, e) => { if (e.Property == Icon.KindProperty) _miniPauseIcon.Kind = _pauseIcon.Kind; };
        _miniChrome = new Grid { IsVisible = false, Opacity = 0, Children = { top, play } };
        _miniChrome.Transitions = [new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) }];
        var frame = new Grid { Children = { content, _miniChrome, _miniProgress } };
        _miniProgress.IsVisible = false;
        _frame = new Border { Child = frame, ClipToBounds = true };
        return _frame;
    }
    /// <summary>Shrinks the player to its corner, or brings it back over the whole window.</summary>
    public void SetMini(bool mini)
    {
        if (mini == _mini || _stopped && mini) return;
        if (mini && _shell.WindowState == WindowState.FullScreen) ToggleFullscreen();
        foreach (var button in new[] { _tracks, _more, _episodesButton }) (button.Flyout as Avalonia.Controls.Flyout)?.Hide();
        _mini = mini; _miniDragging = false; _miniPress = null; RenderTransform = null;
        _chrome.IsVisible = _skipHost.IsVisible = !mini;
        _miniChrome.IsVisible = _miniProgress.IsVisible = mini;
        _frame.CornerRadius = new CornerRadius(mini ? MiniRadius : 0);
        _frame.BoxShadow = mini ? BoxShadows.Parse("0 12 36 0 #99000000") : default;
        _frame.BorderThickness = new Thickness(mini ? 1 : 0); _frame.BorderBrush = Ui.Brush("#30FFFFFF");
        if (mini) PlaceMini(); else { Width = Height = double.NaN; Margin = default; HorizontalAlignment = HorizontalAlignment.Stretch; VerticalAlignment = VerticalAlignment.Stretch; }
        _miniTitle.Text = _session.Item is { } item ? item.Type == "Episode" ? $"{item.DisplayTitle} · {_subtitle.Text}" : item.DisplayTitle : "";
        Cursor = Cursor.Default;
        ShowGroup(); UpdateSkip(); ShowControls();
        _shell.MiniChanged(this);
    }
    /// <summary>Clear of the rail and the title bar, in the corner it was left in.</summary>
    private void PlaceMini()
    {
        Width = MiniWidth; Height = MiniHeight;
        HorizontalAlignment = _miniRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        VerticalAlignment = _miniBottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Margin = new Thickness(MainWindow.RailWidth + MiniGap, _shell.TitleBarInset + 12, MiniGap, MiniGap);
    }
    private void ShowMiniControls(bool shown) => _miniChrome.Opacity = shown ? 1 : 0;
    private void MiniProgress()
    {
        if (!_mini) return;
        _miniProgress.Value = _session.Duration > 0 ? _session.Position / _session.Duration : 0;
        _miniProgress.InvalidateVisual();
    }
    /// <summary>A press on the picture: a click pauses, a double click expands, a drag moves it to another corner.</summary>
    private void MiniPressed(PointerPressedEventArgs e)
    {
        if (e.ClickCount >= 2) { _click.Stop(); _miniPress = null; SetMini(false); return; }
        _miniPress = e.GetPosition(_shell); e.Pointer.Capture(_videoHost);
    }
    private void MiniMoved(PointerEventArgs e)
    {
        if (_miniPress is not { } press) return;
        var point = e.GetPosition(_shell);
        if (!_miniDragging && Math.Abs(point.X - press.X) + Math.Abs(point.Y - press.Y) < 6) return;
        if (!_miniDragging) { _miniDragging = true; _click.Stop(); }
        RenderTransform = new TranslateTransform(point.X - press.X, point.Y - press.Y);
    }
    private void MiniReleased(PointerReleasedEventArgs e)
    {
        if (_miniPress is null) return;
        _miniPress = null; e.Pointer.Capture(null);
        if (!_miniDragging) { _click.Start(); return; }
        _miniDragging = false;
        // The corner nearest to where it was let go.
        var middle = this.TranslatePoint(new Point(Bounds.Width / 2, Bounds.Height / 2), _shell) ?? default;
        _miniRight = middle.X > (MainWindow.RailWidth + _shell.Bounds.Width) / 2; _miniBottom = middle.Y > _shell.Bounds.Height / 2;
        RenderTransform = null; PlaceMini();
    }
}
