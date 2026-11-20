using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Core.Family;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// Réglages → Loin de chez toi, the home's Tailscale card and its guide, as on Windows (MainWindow.FamilyGuide.cs there): its card (not set up, ready,
/// or what is off with one button that fixes it) and the guide that sets it up once (maquette A « Le guide »): one step
/// per screen in place of the section, Tailscale's page one click away with a drawing of what to click, the secret found
/// on the clipboard, then Mira's check and the rules put right on a click, undone for 10 minutes.
/// </summary>
public sealed partial class FamilySection
{
    private int _guide;
    private bool _guideNeedsId;
    private HomeTailnetRefusedException? _guideRefused;
    private bool _guideLines;
    private Func<Task>? _fixPolicy;
    /// <summary>The trouble's « Make these changes », as its button does, for the self-check.</summary>
    public Task FixPolicyForCheck() => _fixPolicy?.Invoke() ?? Task.CompletedTask;
    /// <summary>The guide's step on screen (0: closed), for the self-check.</summary>
    public int GuideStep => _guide;
    /// <summary>The guide opened at a step (0 closes it), as its buttons do, for the self-check.</summary>
    public void GuideForCheck(int step) => Go(step);

    private static readonly IBrush Accent = Ui.Ink;

    private void Go(int step)
    {
        _guide = step;
        if (step != 3) { _guideRefused = null; _guideNeedsId = false; }
        if (step == 0) _guideLines = false;
        _ = LoadAsync();
    }

    // —— The card ——

    private Control TailnetCard(JellyfinClient client, HomeTailnetState? state)
    {
        var status = Note("");
        if (state is null) return Card(HomeGuide.EntryTitle, L("Le serveur ne dit rien de Tailscale : mets à jour l’extension Mira.", "The server says nothing about Tailscale: update the Mira extension."));
        if (!state.Connected)
        {
            var start = Button(HomeGuide.SetUp, () => Go(1), "primary");
            start.Margin = new Thickness(0, 14, 0, 0); start.HorizontalAlignment = HorizontalAlignment.Left;
            return Card(HomeGuide.EntryTitle, HomeGuide.EntryText, Pill(HomeGuide.NotSetUp, false), start);
        }
        var trouble = HomeGuide.Trouble(state.Policy == "ok" ? null : state.Policy, state.Missing, state.Broad, state.Fix?.Count ?? 0, state.Server);
        if (trouble is null)
        {
            _fixPolicy = null;
            var rows = new List<Control> { Pill(HomeGuide.Ready, true) };
            rows.Add(Note(state.Devices.Count > 0 ? Count(state.Devices.Count, "appareil du foyer sur ton réseau", "appareils du foyer sur ton réseau", "home device on your network", "home devices on your network") : HomeGuide.FirstLinkText));
            var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0), ItemSpacing = 10, LineSpacing = 8 };
            if (state.UndoUntil is { } until && until > DateTimeOffset.UtcNow)
            {
                rows.Add(Note(HomeGuide.Changed(until)));
                buttons.Children.Add(Button(HomeGuide.Undo, () => _ = UndoAsync(), "quiet"));
            }
            buttons.Children.Add(Button(HomeGuide.Disconnect, () => _ = DisconnectAsync(), "quiet"));
            rows.Add(buttons); rows.Add(status);
            return Card(HomeGuide.ReadyTitle, HomeGuide.ReadyText, [.. rows]);
        }
        var lines = Lines(state);
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0), ItemSpacing = 10, LineSpacing = 8 };
        var first = true;
        _fixPolicy = null;
        foreach (var remedy in trouble.Remedies)
        {
            Func<Task> act = () => RemedyAsync(client, remedy, status, lines);
            if (remedy == HomeGuide.Remedy.Fix) _fixPolicy = act;
            actions.Children.Add(Button(remedy == HomeGuide.Remedy.Fix && state.Fix is { Count: > 0 } fix ? HomeGuide.MakeChanges(fix.Count) : HomeGuide.Label(remedy), () => _ = act(), first ? "primary" : "quiet"));
            first = false;
        }
        actions.Children.Add(Button(HomeGuide.Disconnect, () => _ = DisconnectAsync(), "quiet"));
        var title = new TextBlock { Text = trouble.Title, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = trouble.Warn ? Ui.Warning : Ui.Danger, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 30, 0) };
        return Card(HomeGuide.EntryTitle, trouble.Text, title, actions, lines, status);

        async Task UndoAsync()
        {
            try { await client.HomeUndoPolicyAsync(); _shell.Notice(HomeGuide.Undone); await LoadAsync(); }
            catch (Exception ex) when (Expected(ex)) { Say(status, Words(ex), true); }
        }
        async Task DisconnectAsync()
        {
            if (!await _shell.ConfirmAsync(L("Déconnecter Tailscale du foyer ?", "Disconnect Tailscale from the home?"), L("Plus aucun lien du foyer. Les appareils déjà loin de chez toi gardent leur accès tant que tu ne les retires pas.", "No more home links. Devices already away from home keep their access until you remove them."), L("Déconnecter", "Disconnect"), L("Annuler", "Cancel"))) return;
            try { await client.HomeConnectTailnetAsync(null, null); _link = null; await LoadAsync(); }
            catch (Exception ex) when (Expected(ex)) { Say(status, Words(ex), true); }
        }
    }

    private async Task RemedyAsync(JellyfinClient client, HomeGuide.Remedy remedy, TextBlock status, Control lines)
    {
        switch (remedy)
        {
            case HomeGuide.Remedy.RedoKey: Go(2); break;
            case HomeGuide.Remedy.SetUpAgain: Go(2); break;
            case HomeGuide.Remedy.OpenCredentials: Updates.Open(new Uri(HomeGuide.CredentialsPage)); break;
            case HomeGuide.Remedy.OpenPolicy: Updates.Open(new Uri(HomeGuide.PolicyPage)); break;
            case HomeGuide.Remedy.SeeLines: lines.IsVisible = !lines.IsVisible; break;
            case HomeGuide.Remedy.CheckAgain: await LoadAsync(); break;
            case HomeGuide.Remedy.Fix:
                Say(status, HomeGuide.Fixing, false);
                try { await client.HomeFixPolicyAsync(); _guide = 0; await LoadAsync(); }
                catch (Exception ex) when (Expected(ex)) { Say(status, Refusal(ex), true); }
                break;
        }
    }

    private static string Refusal(Exception ex) => ex switch
    {
        HomeTailnetRefusedException refused when HomeGuide.Trouble(refused.Policy, refused.Missing) is { } trouble => trouble.Title + ". " + trouble.Text,
        _ => Words(ex),
    };

    // —— The guide ——

    private Control Guide(JellyfinClient client, HomeTailnetState state)
    {
        var step = Math.Clamp(_guide, 1, HomeGuide.Steps);
        if (state.Connected && step == 3) step = _guide = 4;
        var body = new StackPanel();
        var header = new DockPanel();
        var close = Ui.IconButton("close", HomeGuide.Close, () => Go(0), "icon", 14);
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = HomeGuide.Header(step), FontSize = 12.5, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(header);
        var bars = new UniformGrid { Rows = 1, Margin = new Thickness(0, 8, 0, 0) };
        for (var i = 1; i <= HomeGuide.Steps; i++)
            bars.Children.Add(new Rectangle { Height = 3, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(0, 0, i < HomeGuide.Steps ? 6 : 0, 0), Fill = i <= step ? Accent : Ui.LineStrong });
        body.Children.Add(bars);

        var status = Note("");
        var page = step switch { 1 => HomeGuide.Tag, 2 => HomeGuide.Key, 3 => HomeGuide.Secret, _ => HomeGuide.Rules(state.Fix?.Count ?? 0, state.Policy == "harden") };
        var heading = Ui.Heading(page.Title, 20, 3, FontWeight.SemiBold);
        heading.Margin = new Thickness(0, 22, 30, 0); heading.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(heading);
        body.Children.Add(Note(page.Why));
        var number = 0;
        foreach (var line in page.Do) body.Children.Add(Step(++number, line));
        if (step == 1)
        {
            var copied = Note("");
            var copy = Button(HomeGuide.Copy + " « " + HomeGuide.TagName + " »", () => _ = CopyTagAsync(), "quiet");
            copy.Margin = new Thickness(32, 10, 0, 0); copy.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(copy); body.Children.Add(copied);
            async Task CopyTagAsync()
            {
                if (TopLevel.GetTopLevel(_shell)?.Clipboard is not { } clipboard) { Say(copied, L("Copie impossible ici : tape-le.", "Can’t copy here: type it."), true); return; }
                await clipboard.SetTextAsync(HomeGuide.TagName);
                Say(copied, HomeGuide.Copied, false);
            }
        }
        if (page.Open is { } open)
        {
            var go = Button(page.OpenLabel ?? open, () => Updates.Open(new Uri(open)), "primary");
            go.Margin = new Thickness(0, 16, 0, 0); go.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(go);
        }
        if (step <= 3) body.Children.Add(Sketch(step));

        Func<Task>? next = step switch { 3 => GuideSecret(client, body, status), 4 => GuideCheck(client, body, state, status), _ => null };
        if (page.FootNote is { } foot) body.Children.Add(Note(foot, top: 14));
        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0), ItemSpacing = 10, LineSpacing = 8 };
        buttons.Children.Add(Button(step == 1 ? HomeGuide.NotNow : HomeGuide.Back, () => Go(step == 1 ? 0 : step == 4 ? 2 : step - 1), "quiet"));
        Button forward = null!;
        forward = Button(page.Next, () => _ = ForwardAsync(), "primary");
        if (step < 4 || state.Policy == "ok" || state.Fix is { Count: > 0 }) buttons.Children.Add(forward);
        body.Children.Add(buttons); body.Children.Add(status);
        if (step == 4 && state.Fix is { Count: > 0 }) _fixPolicy = ForwardAsync;
        return _section(HomeGuide.EntryTitle, [body]);

        async Task ForwardAsync()
        {
            if (next is null) { Go(step + 1); return; }
            forward.IsEnabled = false;
            try { await next(); }
            finally { forward.IsEnabled = true; }
        }
    }

    private Func<Task> GuideSecret(JellyfinClient client, StackPanel body, TextBlock status)
    {
        var secret = new TextBox { Width = 340, PasswordChar = '•', HorizontalAlignment = HorizontalAlignment.Left };
        Avalonia.Automation.AutomationProperties.SetName(secret, HomeGuide.SecretBox);
        var id = new TextBox { Width = 340, HorizontalAlignment = HorizontalAlignment.Left };
        Avalonia.Automation.AutomationProperties.SetName(id, HomeGuide.IdBox);
        string? found = null;
        if (_guideRefused is { } refused && HomeGuide.Trouble(refused.Policy, refused.Missing) is { } trouble)
        {
            var fixes = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), ItemSpacing = 10, LineSpacing = 8 };
            foreach (var remedy in trouble.Remedies) fixes.Children.Add(Button(HomeGuide.Label(remedy), () => _ = RemedyAsync(client, remedy, status, new Border()), "quiet"));
            body.Children.Add(Callout(trouble.Title, trouble.Text, true, fixes));
        }
        // The clipboard, read once when the step opens and only to offer it: nothing is sent before « Use it » or « Check it ».
        var offer = new StackPanel { IsVisible = false };
        body.Children.Add(offer);
        _ = OfferAsync();
        body.Children.Add(Labeled(HomeGuide.SecretBox, secret));
        if (_guideNeedsId)
        {
            body.Children.Add(Note(HomeGuide.IdNeeded, top: 12));
            body.Children.Add(Labeled(HomeGuide.IdBox, id));
        }
        return CheckAsync;

        async Task OfferAsync()
        {
            string? text = null;
            try { if (TopLevel.GetTopLevel(_shell)?.Clipboard is { } clipboard) text = await clipboard.TryGetTextAsync(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { text = null; }
            if (!HomeGuide.LooksLikeSecret(text)) return;
            found = text!.Trim();
            var use = Button(HomeGuide.UseIt, () => { secret.Text = found; _ = CheckAsync(); }, "primary");
            use.Margin = new Thickness(0, 10, 0, 0); use.HorizontalAlignment = HorizontalAlignment.Left;
            offer.Children.Add(Callout(HomeGuide.FoundSecret, HomeGuide.Masked(found), false, use));
            offer.Children.Add(Note(HomeGuide.PasteYourself, top: 14));
            offer.IsVisible = true;
        }
        async Task CheckAsync()
        {
            var given = (secret.Text ?? "").Trim();
            if (given.Length == 0) { Say(status, L("Colle le secret du client.", "Paste the client secret."), true); return; }
            if (_guideNeedsId && (id.Text ?? "").Trim().Length == 0) { Say(status, HomeGuide.IdNeeded, true); return; }
            Say(status, HomeGuide.Checking, false);
            try
            {
                await client.HomeConnectTailnetAsync(_guideNeedsId ? id.Text!.Trim() : null, given);
                secret.Text = "";
                if (found is not null && TopLevel.GetTopLevel(_shell)?.Clipboard is { } clipboard && (await clipboard.TryGetTextAsync())?.Trim() == found) await clipboard.ClearAsync();
                Go(4);
            }
            catch (HomeTailnetRefusedException ex)
            {
                secret.Text = "";
                if (ex.NeedsId && !_guideNeedsId) { _guideNeedsId = true; Go(3); return; }
                if (HomeGuide.Trouble(ex.Policy, ex.Missing) is not null) { _guideRefused = ex; _ = LoadAsync(); return; }
                Say(status, ex.Message, true);
            }
            catch (Exception ex) when (Expected(ex)) { Say(status, Refusal(ex), true); }
        }
    }

    private Func<Task> GuideCheck(JellyfinClient client, StackPanel body, HomeTailnetState state, TextBlock status)
    {
        var list = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        foreach (var (mark, text) in HomeGuide.Checks(state)) list.Children.Add(CheckLine(mark, text));
        body.Children.Add(list);
        var trouble = HomeGuide.Trouble(state.Policy == "ok" ? null : state.Policy, state.Missing, state.Broad, state.Fix?.Count ?? 0, state.Server);
        if (state.Fix is { Count: > 0 } fix)
        {
            var inside = new StackPanel();
            foreach (var change in fix) { var line = Note("•  " + HomeGuide.Change(change)); line.Margin = new Thickness(4, 6, 30, 0); inside.Children.Add(line); }
            var lines = Lines(state);
            lines.IsVisible = _guideLines;
            Button see = null!;
            see = Button(_guideLines ? HomeGuide.HideLines : HomeGuide.SeeLines, () =>
            {
                _guideLines = !_guideLines; lines.IsVisible = _guideLines;
                see.Content = _guideLines ? HomeGuide.HideLines : HomeGuide.SeeLines;
            }, "quiet");
            see.Margin = new Thickness(0, 10, 0, 0); see.HorizontalAlignment = HorizontalAlignment.Left;
            inside.Children.Add(see); inside.Children.Add(lines);
            body.Children.Add(Callout(HomeGuide.MakeChanges(fix.Count), "", false, inside));
        }
        else if (trouble is not null)
        {
            var fixes = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), ItemSpacing = 10, LineSpacing = 8 };
            foreach (var remedy in trouble.Remedies) fixes.Children.Add(Button(HomeGuide.Label(remedy), () => _ = RemedyAsync(client, remedy, status, new Border()), "quiet"));
            body.Children.Add(Callout(trouble.Title, trouble.Text, true, fixes));
        }
        return async () =>
        {
            if (state.Fix is not { Count: > 0 }) { Go(0); return; }
            Say(status, HomeGuide.Fixing, false);
            try { await client.HomeFixPolicyAsync(); Go(0); }
            catch (Exception ex) when (Expected(ex)) { Say(status, Refusal(ex), true); }
        };
    }

    // —— Pieces ——

    private static Border Lines(HomeTailnetState state) => new()
    {
        Background = Ui.Inset, BorderBrush = Ui.MenuLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10), Margin = new Thickness(0, 10, 30, 0), IsVisible = false,
        Child = new SelectableTextBlock { Text = string.Join("\n", state.Lines ?? []), FontFamily = new FontFamily("Menlo, monospace"), FontSize = 12, Foreground = Ui.TextBody, TextWrapping = TextWrapping.Wrap },
    };

    private static Border Callout(string title, string text, bool danger, Control? after = null)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = danger ? Ui.Danger : Ui.Ink, TextWrapping = TextWrapping.Wrap });
        if (text.Length > 0) stack.Children.Add(Note(text, top: 4));
        if (after is not null) stack.Children.Add(after);
        return new Border { Background = Ui.Raised, BorderBrush = danger ? Ui.Danger : Ui.MenuLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12), Margin = new Thickness(0, 16, 30, 0), Child = stack };
    }

    private static Control Labeled(string label, Control field) => Ui.Column(4, new TextBlock { Text = label, FontSize = 12, Foreground = Ui.Muted, Margin = new Thickness(2, 12, 0, 0) }, field);

    private static Border Pill(string text, bool ready) => new()
    {
        Background = Ui.Raised, BorderBrush = ready ? Ui.Success : Ui.MenuLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3),
        Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = ready ? Ui.Success : Ui.TextSecondary },
    };

    private static DockPanel CheckLine(HomeGuide.Mark mark, string text)
    {
        var line = new DockPanel { Margin = new Thickness(0, 6, 30, 0) };
        var (icon, color) = mark switch { HomeGuide.Mark.Ok => ("check", Ui.Success), HomeGuide.Mark.Warn => ("warning", Ui.Warning), _ => ("close", Ui.Danger) };
        var glyph = new Icon(icon, 14) { Foreground = color, Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(glyph, Dock.Left); line.Children.Add(glyph);
        line.Children.Add(new TextBlock { Text = text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
        return line;
    }

    /// <summary>A drawing of Tailscale's page at a step, as on Windows: what to click, numbered as the step's lines.</summary>
    private static Border Sketch(int step)
    {
        var (address, lines) = HomeGuide.Drawing(step);
        var stack = new StackPanel();
        stack.Children.Add(new Border { Background = Ui.Raised2, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 10, 5), Margin = new Thickness(0, 0, 0, 10), Child = new TextBlock { Text = address, FontSize = 11.5, Foreground = Ui.TextSecondary, TextTrimming = TextTrimming.CharacterEllipsis } });
        foreach (var line in lines) stack.Children.Add(Line(line));
        var frame = new Border { Background = Ui.Surface, BorderBrush = Ui.MenuLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 14), Margin = new Thickness(0, 18, 30, 0), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, Child = stack };
        Avalonia.Automation.AutomationProperties.SetName(frame, L("Dessin de la page de Tailscale", "Drawing of Tailscale’s page"));
        return frame;

        static Control Line(HomeGuide.Sketch line)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4), Opacity = line.Dim ? 0.45 : 1 };
            if (line.Marker > 0)
            {
                var badge = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Accent, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = line.Marker.ToString(Lang.Culture), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Ui.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                DockPanel.SetDock(badge, Dock.Right); row.Children.Add(badge);
            }
            var mono = new FontFamily("Menlo, monospace");
            switch (line.Kind)
            {
                case HomeGuide.SketchKind.Heading:
                    row.Children.Add(new TextBlock { Text = line.Label, FontWeight = FontWeight.SemiBold, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
                    break;
                case HomeGuide.SketchKind.Button:
                    row.Children.Add(new Border { Background = Ui.Ink, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 10, 5), HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = line.Label, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.Black } });
                    break;
                case HomeGuide.SketchKind.Field:
                    row.Children.Add(Ui.Column(3, new TextBlock { Text = line.Label, FontSize = 11.5, Foreground = Ui.TextSecondary },
                        new Border { BorderBrush = Ui.LineStrong, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 3, 8, 4), Child = new TextBlock { Text = line.Value ?? "", FontSize = 12, FontFamily = mono } }));
                    break;
                case HomeGuide.SketchKind.Choice:
                {
                    var choice = new StackPanel { Orientation = Orientation.Horizontal };
                    choice.Children.Add(new Ellipse { Width = 12, Height = 12, Stroke = line.On ? Accent : Ui.LineStrong, StrokeThickness = line.On ? 4 : 1.5, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                    choice.Children.Add(new TextBlock { Text = line.Label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextDecorations = line.Wrong ? TextDecorations.Strikethrough : null });
                    if (line.Value is { } value) choice.Children.Add(new TextBlock { Text = "  " + value, FontSize = 12, Foreground = line.Wrong ? Ui.Danger : Ui.TextSecondary, VerticalAlignment = VerticalAlignment.Center });
                    row.Children.Add(choice);
                    break;
                }
                case HomeGuide.SketchKind.Scope:
                {
                    var scope = new DockPanel();
                    var ticks = new StackPanel { Orientation = Orientation.Horizontal };
                    foreach (var word in new[] { "Read", "Write" })
                    {
                        ticks.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Margin = new Thickness(10, 0, 4, 0), Background = line.On ? Accent : Brushes.Transparent, BorderBrush = line.On ? Accent : Ui.LineStrong, BorderThickness = new Thickness(1.5), VerticalAlignment = VerticalAlignment.Center });
                        ticks.Children.Add(new TextBlock { Text = word, FontSize = 11.5, Foreground = Ui.TextSecondary, VerticalAlignment = VerticalAlignment.Center });
                    }
                    DockPanel.SetDock(ticks, Dock.Right); scope.Children.Add(ticks);
                    var name = new StackPanel { Orientation = Orientation.Horizontal };
                    name.Children.Add(new TextBlock { Text = line.Label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
                    if (line.Tag is { } tag) name.Children.Add(new Border { Background = Ui.Raised2, CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 2), Margin = new Thickness(8, 0, 0, 0), Child = new TextBlock { Text = tag, FontSize = 11, FontFamily = mono } });
                    scope.Children.Add(name);
                    row.Children.Add(scope);
                    break;
                }
            }
            return row;
        }
    }
}
