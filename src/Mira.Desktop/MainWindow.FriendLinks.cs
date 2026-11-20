using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Mira.Core;
using Mira.Core.Family;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// The administrator's side of « Inviter un ami » by a link (ogsas, 2026-11-20: maquette A « Le lien »): in the guide
/// of an account just made, « Make {name}'s invite » (a link and its QR code) when the home is on Tailscale; above it, a
/// line per friend (their devices, the one waiting to come in with « Let in » and « Refuse », the link still waiting),
/// read again every few seconds while something waits.
/// </summary>
public partial class MainWindow
{
    private FriendsState? _friends;
    private DispatcherTimer? _friendsWatch;
    private bool _friendsReading;

    /// <summary>The guide of an account just made: one link instead of Tailscale's sharing, when the extension can make them.</summary>
    private async Task OfferFriendLinkAsync(JellyfinClient client, FriendGuide? guide)
    {
        if (guide is null) return;
        FriendsState? state;
        try { state = await client.FriendsAsync(_connectionLifetime.Token); }
        catch (Exception ex) when (IsExpected(ex)) { return; }
        if (!ReferenceEquals(guide, _friendGuide) || state is not { Ready: true }) return;
        guide.Links = true;
        RenderFriendGuide(guide);
    }

    private async Task MakeFriendLinkAsync(FriendGuide guide)
    {
        if (_client is not { } client || guide.UserId is not { } user || _friendBusy) return;
        _friendBusy = true; FriendStatus.Text = L("Création du lien…", "Making the link…");
        try
        {
            guide.Link = await client.FriendInviteAsync(user, _connectionLifetime.Token);
            FriendStatus.Text = "";
            RenderFriendGuide(guide);
            await ShowFriendLinesAsync();
        }
        catch (FriendRefusedException ex) { FriendStatus.Text = ex.Message; }
        catch (Exception ex) when (IsExpected(ex)) { FriendStatus.Text = Friendly(ex); }
        finally { _friendBusy = false; }
    }

    /// <summary>The link's QR code, for a phone beside this PC, and « Make a new link ».</summary>
    private FrameworkElement FriendLinkQr(string link)
    {
        var (size, path) = PhoneLink.Qr(link);
        var module = Math.Max(2, Math.Round(120.0 / size));
        var geometry = Geometry.Parse(path); geometry.Freeze();
        var code = new Path { Data = geometry, Width = module * size, Height = module * size, Stretch = Stretch.Uniform, Fill = Palette.Get("Bg"), SnapsToDevicePixels = true };
        RenderOptions.SetEdgeMode(code, EdgeMode.Aliased);
        var qr = new Border { Background = Palette.Get("Text"), CornerRadius = new CornerRadius(10), Padding = new Thickness(8), Child = code, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetName(qr, L("QR code du lien, à scanner avec un téléphone", "QR code of the link, to scan with a phone"));
        var stack = new StackPanel();
        stack.Children.Add(qr);
        var again = FriendButton(FriendText.NewLink, "refresh", (_, _) => { if (_friendGuide is { } guide) { guide.Link = null; _ = MakeFriendLinkAsync(guide); } }, primary: false);
        again.HorizontalAlignment = HorizontalAlignment.Left; again.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(again);
        return stack;
    }

    // ---- Friends: a line each ----

    /// <summary>Réglages opened: the friends' lines, for an administrator whose extension has friends' links.</summary>
    private async Task ShowFriendLinesAsync()
    {
        if (_demo || _client is not { } client || _friendsReading) { if (_client is null) FriendLinesRow.Visibility = Visibility.Collapsed; return; }
        _friendsReading = true;
        try
        {
            FriendsState? state;
            try { state = await client.FriendsAsync(_connectionLifetime.Token); }
            catch (Exception ex) when (IsExpected(ex)) { state = null; }
            if (!ReferenceEquals(client, _client)) return;
            _friends = state;
            var any = state is { } s && ((s.Devices?.Count ?? 0) > 0 || (s.Invites ?? []).Any(x => x.State == "open"));
            FriendLinesRow.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (any) RenderFriendLines(state!);
            WatchFriendLines(state);
        }
        finally { _friendsReading = false; }
    }

    /// <summary>Read again every 5 s while a link or a device waits and the lines show: « Let in » comes up by itself.</summary>
    private void WatchFriendLines(FriendsState? state)
    {
        var waits = state is { } s && ((s.Devices ?? []).Any(x => x.Waiting) || (s.Invites ?? []).Any(x => x.State == "open"));
        if (!waits) { _friendsWatch?.Stop(); return; }
        if (_friendsWatch is null)
        {
            _friendsWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _friendsWatch.Tick += (_, _) =>
            {
                if (!FriendLinesRow.IsVisible && !FriendSteps.IsVisible) { _friendsWatch.Stop(); return; }
                _ = ShowFriendLinesAsync();
            };
        }
        if (!_friendsWatch.IsEnabled) _friendsWatch.Start();
    }

    private void RenderFriendLines(FriendsState state)
    {
        var now = DateTimeOffset.UtcNow;
        var names = (state.Invites ?? []).GroupBy(x => x.User).ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.Created).First().Name);
        var users = (state.Devices ?? []).Select(x => x.User).Concat((state.Invites ?? []).Where(x => x.State == "open").Select(x => x.User)).Distinct().ToList();
        FriendLines.Children.Clear();
        foreach (var user in users)
        {
            var name = names.TryGetValue(user, out var known) ? known : L("Ton ami", "Your friend");
            var (devices, waiting) = state.Of(user, now);
            var card = new Border { Background = Palette.Get("Raised"), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 8, 0, 0) };
            var stack = new StackPanel();
            var head = new DockPanel();
            var letter = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Background = Palette.Get("Raised2"), Margin = new Thickness(0, 0, 12, 0),
                Child = new TextBlock { Text = name[..1].ToUpper(Lang.Culture), FontSize = 15, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            DockPanel.SetDock(letter, Dock.Left); head.Children.Add(letter);
            var add = FriendButton(FriendText.AddDevice, "plus", (_, _) => _ = AddFriendDeviceAsync(user, name), primary: false);
            add.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(add, Dock.Right); head.Children.Add(add);
            var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
            nameLine.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold });
            if (devices.Any(x => !x.Waiting)) nameLine.Children.Add(new TextBlock { Text = "✓ " + FriendText.In, FontSize = 11.5, Foreground = Palette.Get("Success"), Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(nameLine);
            head.Children.Add(title);
            stack.Children.Add(head);
            foreach (var device in devices) stack.Children.Add(FriendDeviceLine(device, now));
            if (waiting is not null) stack.Children.Add(FriendInviteLine(waiting, name));
            card.Child = stack;
            FriendLines.Children.Add(card);
        }
    }

    private DockPanel FriendDeviceLine(FriendDeviceRow device, DateTimeOffset now)
    {
        var line = new DockPanel { Margin = new Thickness(46, 10, 0, 0) };
        if (device.Waiting)
        {
            var refuse = FriendButton(FriendText.Refuse, "close", (_, _) => _ = RemoveFriendDeviceAsync(device, refusing: true), primary: false);
            DockPanel.SetDock(refuse, Dock.Right); refuse.Margin = new Thickness(8, 0, 0, 0); line.Children.Add(refuse);
            var letIn = FriendButton(FriendText.LetIn, "check", (_, _) => _ = LetFriendInAsync(device), primary: true);
            DockPanel.SetDock(letIn, Dock.Right); line.Children.Add(letIn);
            line.Children.Add(new TextBlock { Text = FriendText.Knocking(device.Name), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("Warning"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            return line;
        }
        var remove = FriendButton(FriendText.RemoveDevice, "trash", (_, _) => _ = RemoveFriendDeviceAsync(device, refusing: false), primary: false);
        DockPanel.SetDock(remove, Dock.Right); line.Children.Add(remove);
        var seen = device.LastSeen is { } at ? " · " + FriendText.Seen(at, now) : "";
        line.Children.Add(new TextBlock { Text = FriendText.Joined(device.Name, device.Joined, now) + seen, Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
        return line;
    }

    private DockPanel FriendInviteLine(FriendInviteRow invite, string name)
    {
        var line = new DockPanel { Margin = new Thickness(46, 10, 0, 0) };
        var cancel = FriendButton(FriendText.Cancel, "close", (_, _) => _ = CancelFriendInviteAsync(invite), primary: false);
        DockPanel.SetDock(cancel, Dock.Right); line.Children.Add(cancel);
        line.Children.Add(new TextBlock { Text = FriendText.Waiting(name), Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
        return line;
    }

    private async Task LetFriendInAsync(FriendDeviceRow device)
    {
        if (_client is not { } client) return;
        try { await client.FriendApproveAsync(device.Id, _connectionLifetime.Token); FriendLinesStatus.Text = L($"{device.Name} peut entrer.", $"{device.Name} can come in."); }
        catch (Exception ex) when (IsExpected(ex)) { FriendLinesStatus.Text = Friendly(ex); }
        await ShowFriendLinesAsync();
    }

    private async Task RemoveFriendDeviceAsync(FriendDeviceRow device, bool refusing)
    {
        if (_client is not { } client) return;
        if (!refusing && !await ConfirmAsync(FriendText.RemoveDevice + " ?", L($"{device.Name} perd aussitôt sa ligne vers ce PC. Pour revenir, il faudra une nouvelle invitation.", $"{device.Name} loses its line to this PC at once. Coming back will take a new invite."), FriendText.RemoveDevice, L("Annuler", "Cancel"))) return;
        try { await client.FriendRemoveDeviceAsync(device.Id, _connectionLifetime.Token); FriendLinesStatus.Text = ""; }
        catch (Exception ex) when (IsExpected(ex)) { FriendLinesStatus.Text = Friendly(ex); }
        await ShowFriendLinesAsync();
    }

    private async Task CancelFriendInviteAsync(FriendInviteRow invite)
    {
        if (_client is not { } client) return;
        try { await client.FriendCancelAsync(invite.Id, _connectionLifetime.Token); FriendLinesStatus.Text = ""; }
        catch (Exception ex) when (IsExpected(ex)) { FriendLinesStatus.Text = Friendly(ex); }
        if (_friendGuide is { Link: { } made } guide && made.Id == invite.Id) { guide.Link = null; RenderFriendGuide(guide); }
        await ShowFriendLinesAsync();
    }

    /// <summary>« Add a device »: a new link for the same account, copied at once.</summary>
    private async Task AddFriendDeviceAsync(string user, string name)
    {
        if (_client is not { } client) return;
        try
        {
            var made = await client.FriendInviteAsync(user, _connectionLifetime.Token);
            try { Clipboard.SetText(made.Link); FriendLinesStatus.Text = FriendText.Copied; }
            catch (System.Runtime.InteropServices.ExternalException) { FriendLinesStatus.Text = made.Link; }
        }
        catch (FriendRefusedException ex) { FriendLinesStatus.Text = ex.Message; }
        catch (Exception ex) when (IsExpected(ex)) { FriendLinesStatus.Text = Friendly(ex); }
        await ShowFriendLinesAsync();
    }
}
