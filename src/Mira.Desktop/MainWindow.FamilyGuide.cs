using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Mira.Core;
using Mira.Core.Family;
using Mira.Core.TorLink;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// Réglages → Foyer → « Away from home »: its row (not set up, ready, or what is off with one button that fixes it) and
/// the guide that sets it up once (maquette A « Le guide », ogsas 2026-11-20): one step per screen in place of the
/// section, the console's page one click away with a drawing of what to click there, the secret found on the clipboard,
/// then Mira's check and the rules put right on a click, undone for 10 minutes. Words and drawings: <see cref="HomeGuide"/>.
/// </summary>
public partial class MainWindow
{
    /// <summary>The guide's step, 1 to 4; 0 when it is closed.</summary>
    private int _familyGuide;
    /// <summary>Step 3: Tailscale did not take the ID read from the secret, so the ID's box is shown too.</summary>
    private bool _familyGuideNeedsId;
    /// <summary>Step 3: why the last secret was refused (too broad, missing a scope), as the extension said it.</summary>
    private HomeTailnetRefusedException? _familyGuideRefused;
    private bool _familyGuideLines;

    private void FamilyGuideGo(int step)
    {
        _familyGuide = step;
        if (step != 3) { _familyGuideRefused = null; _familyGuideNeedsId = false; }
        if (step == 0) _familyGuideLines = false;
        _ = ShowFamilySettingsAsync();
    }

    // ---- The row ----

    private Border FamilyTailnetRow(JellyfinClient client, HomeTailnetState? state)
    {
        var status = FamilyNote("");
        if (state is null)
        {
            var old = FamilyCard(HomeGuide.EntryTitle, L("Le serveur ne dit rien de Tailscale : mets à jour l’extension Mira.", "The server says nothing about Tailscale: update the Mira extension."));
            return FamilyRow(old);
        }
        if (!state.Connected)
        {
            var body = FamilyCard(HomeGuide.EntryTitle, HomeGuide.EntryText);
            body.Children.Add(FamilyPill(HomeGuide.NotSetUp, "TextSecondary"));
            var start = FamilyButton(HomeGuide.SetUp, () => FamilyGuideGo(1), primary: true);
            start.Margin = new Thickness(0, 14, 0, 0); start.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(start);
            return FamilyRow(body);
        }
        var trouble = HomeGuide.Trouble(state.Policy == "ok" ? null : state.Policy, state.Missing, state.Broad, state.Fix?.Count ?? 0, state.Server);
        if (trouble is null)
        {
            var body = FamilyCard(HomeGuide.ReadyTitle, HomeGuide.ReadyText);
            body.Children.Add(FamilyPill(HomeGuide.Ready, "Success"));
            if (state.Devices.Count > 0) body.Children.Add(FamilyNote(Count(state.Devices.Count, "appareil du foyer sur ton réseau", "appareils du foyer sur ton réseau", "home device on your network", "home devices on your network")));
            else body.Children.Add(FamilyNote(HomeGuide.FirstLinkText));
            var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            if (state.UndoUntil is { } until && until > DateTimeOffset.UtcNow)
            {
                body.Children.Add(FamilyNote(HomeGuide.Changed(until)));
                buttons.Children.Add(FamilyButton(HomeGuide.Undo, () => _ = UndoAsync(), quiet: true));
            }
            buttons.Children.Add(FamilyButton(HomeGuide.Disconnect, () => _ = DisconnectAsync(), quiet: true));
            body.Children.Add(buttons); body.Children.Add(status);
            return FamilyRow(body);
        }
        var problem = FamilyCard(trouble.Title, trouble.Text);
        ((TextBlock)problem.Children[0]).Foreground = Palette.Get("Danger");
        var lines = FamilyGuideLines(state);
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var first = true;
        foreach (var remedy in trouble.Remedies)
        {
            actions.Children.Add(FamilyButton(remedy == HomeGuide.Remedy.Fix && state.Fix is { Count: > 0 } fix ? HomeGuide.MakeChanges(fix.Count) : HomeGuide.Label(remedy), () => _ = FamilyRemedyAsync(client, remedy, status, lines), primary: first, quiet: !first));
            first = false;
        }
        actions.Children.Add(FamilyButton(HomeGuide.Disconnect, () => _ = DisconnectAsync(), quiet: true));
        problem.Children.Add(actions); problem.Children.Add(lines); problem.Children.Add(status);
        return FamilyRow(problem);

        async Task UndoAsync()
        {
            try { await client.HomeUndoPolicyAsync(_connectionLifetime.Token); SetNotice(HomeGuide.Undone); await ShowFamilySettingsAsync(); }
            catch (Exception ex) when (IsExpected(ex) || ex is RelayRefusedException) { FamilySay(status, ex is RelayRefusedException ? ex.Message : Friendly(ex), true); }
        }
        async Task DisconnectAsync()
        {
            if (!await ConfirmAsync(L("Déconnecter Tailscale du foyer ?", "Disconnect Tailscale from the home?"), L("Plus aucun lien du foyer. Les appareils déjà loin de chez toi gardent leur accès tant que tu ne les retires pas.", "No more home links. Devices already away from home keep their access until you remove them."), L("Déconnecter", "Disconnect"), L("Annuler", "Cancel"))) return;
            try { await client.HomeConnectTailnetAsync(null, null, _connectionLifetime.Token); _familyLink = null; await ShowFamilySettingsAsync(); }
            catch (Exception ex) when (IsExpected(ex) || ex is RelayRefusedException) { FamilySay(status, ex is RelayRefusedException ? ex.Message : Friendly(ex), true); }
        }
    }

    /// <summary>What a trouble's button does: the guide's step, Tailscale's page, the fix, the exact lines, a new check.</summary>
    private async Task FamilyRemedyAsync(JellyfinClient client, HomeGuide.Remedy remedy, TextBlock status, FrameworkElement lines)
    {
        switch (remedy)
        {
            case HomeGuide.Remedy.RedoKey: FamilyGuideGo(2); break;
            case HomeGuide.Remedy.SetUpAgain: FamilyGuideGo(2); break;
            case HomeGuide.Remedy.OpenCredentials: OpenWebPage(HomeGuide.CredentialsPage); break;
            case HomeGuide.Remedy.OpenPolicy: OpenWebPage(HomeGuide.PolicyPage); break;
            case HomeGuide.Remedy.SeeLines: lines.Visibility = lines.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; break;
            case HomeGuide.Remedy.CheckAgain: await ShowFamilySettingsAsync(); break;
            case HomeGuide.Remedy.Fix:
                FamilySay(status, HomeGuide.Fixing, false);
                try { await client.HomeFixPolicyAsync(_connectionLifetime.Token); _familyGuide = 0; await ShowFamilySettingsAsync(); }
                catch (Exception ex) when (IsExpected(ex) || ex is RelayRefusedException) { FamilySay(status, FamilyRefusal(ex), true); }
                break;
        }
    }

    /// <summary>A refusal said as the guide says it (one sentence), else the extension's words.</summary>
    private string FamilyRefusal(Exception ex) => ex switch
    {
        HomeTailnetRefusedException refused when HomeGuide.Trouble(refused.Policy, refused.Missing) is { } trouble => trouble.Title + ". " + trouble.Text,
        RelayRefusedException refused => refused.Message,
        _ => Friendly(ex),
    };

    // ---- The guide ----

    private Border FamilyGuide(JellyfinClient client, HomeTailnetState state)
    {
        var step = Math.Clamp(_familyGuide, 1, HomeGuide.Steps);
        // Connected already (a key given, the guide reopened from a trouble at step 2 is the only way back): step 4 shows the check.
        if (state.Connected && step == 3) step = _familyGuide = 4;
        var body = new StackPanel();

        // The header: where we are, the four bars, the close button.
        var header = new DockPanel();
        var close = new Button { Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Style = (Style)FindResource("IconButton"), Width = 32, Height = 32, ToolTip = HomeGuide.Close };
        AutomationProperties.SetName(close, HomeGuide.Close);
        close.Click += (_, _) => FamilyGuideGo(0);
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = HomeGuide.Header(step), Style = (Style)FindResource("SettingDescription"), VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(header);
        var bars = new UniformGrid { Rows = 1, Margin = new Thickness(0, 8, 0, 0) };
        for (var i = 1; i <= HomeGuide.Steps; i++)
            bars.Children.Add(new Rectangle { Height = 3, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(0, 0, i < HomeGuide.Steps ? 6 : 0, 0), Fill = Palette.Get(i <= step ? "Accent" : "LineStrong") });
        body.Children.Add(bars);

        var status = FamilyNote("");
        HomeGuide.Step page = step switch { 1 => HomeGuide.Tag, 2 => HomeGuide.Key, 3 => HomeGuide.Secret, _ => HomeGuide.Rules(state.Fix?.Count ?? 0) };
        var heading = new TextBlock { Text = page.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 22, 30, 0) };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
        body.Children.Add(heading);
        body.Children.Add(FamilyNote(page.Why, margin: new Thickness(0, 6, 30, 0)));
        var number = 0;
        foreach (var line in page.Do) body.Children.Add(FamilyStep(++number, line));
        if (step == 1)
        {
            var copy = FamilyNote("");
            var copyButton = FamilyButton(HomeGuide.Copy + " « " + HomeGuide.TagName + " »", () =>
            {
                try { Clipboard.SetText(HomeGuide.TagName); FamilySay(copy, HomeGuide.Copied, false); }
                catch (System.Runtime.InteropServices.ExternalException) { FamilySay(copy, L("Le presse-papiers est occupé : réessaie.", "The clipboard is busy: try again."), true); }
            }, quiet: true);
            copyButton.Margin = new Thickness(30, 10, 0, 0); copyButton.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(copyButton); body.Children.Add(copy);
        }
        if (page.Open is { } open)
        {
            var go = FamilyButton(page.OpenLabel ?? open, () => OpenWebPage(open), primary: true);
            go.Margin = new Thickness(0, 16, 0, 0); go.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(go);
        }
        if (step <= 3) body.Children.Add(FamilySketch(step));

        Func<Task>? next = null;
        if (step == 3) next = FamilyGuideSecret(client, body, status);
        if (step == 4) next = FamilyGuideCheck(client, body, state, status);

        if (page.FootNote is { } foot) body.Children.Add(FamilyNote(foot, margin: new Thickness(0, 14, 30, 0)));
        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(FamilyButton(step == 1 ? HomeGuide.NotNow : HomeGuide.Back, () => FamilyGuideGo(step == 1 ? 0 : step == 4 ? 2 : step - 1), quiet: true));
        Button forward = null!;
        forward = FamilyButton(page.Next, () => _ = ForwardAsync(), primary: true);
        if (step < 4 || state.Policy == "ok" || state.Fix is { Count: > 0 }) buttons.Children.Add(forward);
        body.Children.Add(buttons); body.Children.Add(status);
        return FamilyRow(body);

        async Task ForwardAsync()
        {
            if (next is null) { FamilyGuideGo(step + 1); return; }
            forward.IsEnabled = false;
            try { await next(); }
            finally { forward.IsEnabled = true; }
        }
    }

    /// <summary>Step 3: the secret found on the clipboard, or pasted; « Check it » gives it to the extension.</summary>
    private Func<Task> FamilyGuideSecret(JellyfinClient client, StackPanel body, TextBlock status)
    {
        string? found = null;
        // Read once, when the step opens, and only to offer it: nothing is sent before « Use it » or « Check it ».
        try { if (Clipboard.ContainsText() && HomeGuide.LooksLikeSecret(Clipboard.GetText())) found = Clipboard.GetText().Trim(); }
        catch (System.Runtime.InteropServices.ExternalException) { found = null; }
        var secret = new PasswordBox { Width = 340, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(secret, HomeGuide.SecretBox);
        var id = new TextBox { Width = 340, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(id, HomeGuide.IdBox);
        if (_familyGuideRefused is { } refused && HomeGuide.Trouble(refused.Policy, refused.Missing) is { } trouble)
        {
            var box = FamilyCallout(trouble.Title, trouble.Text, "Danger");
            var fixes = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var remedy in trouble.Remedies)
                fixes.Children.Add(FamilyButton(HomeGuide.Label(remedy), () => _ = FamilyRemedyAsync(client, remedy, status, new Border()), quiet: true));
            ((StackPanel)box.Child).Children.Add(fixes);
            body.Children.Add(box);
        }
        if (found is not null)
        {
            var offer = FamilyCallout(HomeGuide.FoundSecret, HomeGuide.Masked(found), "Accent");
            var use = FamilyButton(HomeGuide.UseIt, () => { secret.Password = found; _ = CheckAsync(); }, primary: true);
            use.Margin = new Thickness(0, 10, 0, 0); use.HorizontalAlignment = HorizontalAlignment.Left;
            ((StackPanel)offer.Child).Children.Add(use);
            body.Children.Add(offer);
            body.Children.Add(FamilyNote(HomeGuide.PasteYourself, margin: new Thickness(0, 14, 30, 0)));
        }
        body.Children.Add(FamilyLabeled(HomeGuide.SecretBox, secret));
        if (_familyGuideNeedsId)
        {
            body.Children.Add(FamilyNote(HomeGuide.IdNeeded, margin: new Thickness(0, 12, 30, 0)));
            body.Children.Add(FamilyLabeled(HomeGuide.IdBox, id));
        }
        return CheckAsync;

        async Task CheckAsync()
        {
            if (secret.Password.Trim().Length == 0) { FamilySay(status, L("Colle le secret du client.", "Paste the client secret."), true); return; }
            if (_familyGuideNeedsId && id.Text.Trim().Length == 0) { FamilySay(status, HomeGuide.IdNeeded, true); return; }
            FamilySay(status, HomeGuide.Checking, false);
            try
            {
                await client.HomeConnectTailnetAsync(_familyGuideNeedsId ? id.Text.Trim() : null, secret.Password.Trim(), _connectionLifetime.Token);
                secret.Clear();
                // The secret leaves the clipboard once Jellyfin has it: nothing here keeps it.
                try { if (found is not null && Clipboard.ContainsText() && Clipboard.GetText().Trim() == found) Clipboard.Clear(); }
                catch (System.Runtime.InteropServices.ExternalException) { }
                FamilyGuideGo(4);
            }
            catch (HomeTailnetRefusedException ex)
            {
                secret.Clear();
                if (ex.NeedsId && !_familyGuideNeedsId) { _familyGuideNeedsId = true; FamilyGuideGo(3); return; }
                if (HomeGuide.Trouble(ex.Policy, ex.Missing) is not null) { _familyGuideRefused = ex; _ = ShowFamilySettingsAsync(); return; }
                FamilySay(status, ex.Message, true);
            }
            catch (Exception ex) when (IsExpected(ex) || ex is RelayRefusedException) { FamilySay(status, FamilyRefusal(ex), true); }
        }
    }

    /// <summary>Step 4: what Mira checked with Tailscale, the changes and their lines; « Make these changes » writes them.</summary>
    private Func<Task> FamilyGuideCheck(JellyfinClient client, StackPanel body, HomeTailnetState state, TextBlock status)
    {
        var list = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        foreach (var (mark, text) in HomeGuide.Checks(state)) list.Children.Add(FamilyCheckLine(mark, text));
        body.Children.Add(list);
        var trouble = HomeGuide.Trouble(state.Policy == "ok" ? null : state.Policy, state.Missing, state.Broad, state.Fix?.Count ?? 0, state.Server);
        if (state.Fix is { Count: > 0 } fix)
        {
            var changes = new StackPanel();
            foreach (var change in fix) changes.Children.Add(FamilyNote("•  " + HomeGuide.Change(change), margin: new Thickness(4, 6, 30, 0)));
            var box = FamilyCallout(HomeGuide.MakeChanges(fix.Count), "", "Line");
            ((StackPanel)box.Child).Children.Add(changes);
            var lines = FamilyGuideLines(state);
            lines.Visibility = _familyGuideLines ? Visibility.Visible : Visibility.Collapsed;
            Button see = null!;
            see = FamilyButton(_familyGuideLines ? HomeGuide.HideLines : HomeGuide.SeeLines, () =>
            {
                _familyGuideLines = !_familyGuideLines;
                lines.Visibility = _familyGuideLines ? Visibility.Visible : Visibility.Collapsed;
                see.Content = _familyGuideLines ? HomeGuide.HideLines : HomeGuide.SeeLines;
            }, quiet: true);
            see.Margin = new Thickness(0, 10, 0, 0); see.HorizontalAlignment = HorizontalAlignment.Left;
            ((StackPanel)box.Child).Children.Add(see);
            ((StackPanel)box.Child).Children.Add(lines);
            body.Children.Add(box);
        }
        else if (trouble is not null)
        {
            var box = FamilyCallout(trouble.Title, trouble.Text, "Danger");
            var fixes = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var remedy in trouble.Remedies)
                fixes.Children.Add(FamilyButton(HomeGuide.Label(remedy), () => _ = FamilyRemedyAsync(client, remedy, status, new Border()), quiet: true));
            ((StackPanel)box.Child).Children.Add(fixes);
            body.Children.Add(box);
        }
        return async () =>
        {
            if (state.Fix is not { Count: > 0 }) { FamilyGuideGo(0); return; }
            FamilySay(status, HomeGuide.Fixing, false);
            try { await client.HomeFixPolicyAsync(_connectionLifetime.Token); FamilyGuideGo(0); }
            catch (Exception ex) when (IsExpected(ex) || ex is RelayRefusedException) { FamilySay(status, FamilyRefusal(ex), true); }
        };
    }

    // ---- Pieces of the guide ----

    /// <summary>The policy's lines Mira's fix adds or changes, as they will be written (hidden until asked for).</summary>
    private Border FamilyGuideLines(HomeTailnetState state)
    {
        var text = new TextBlock { Text = string.Join("\n", state.Lines ?? []), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Get("TextBody") };
        AutomationProperties.SetName(text, HomeGuide.SeeLines);
        return new Border { Background = Palette.Get("Inset"), BorderBrush = Palette.Get("Line"), BorderThickness = new Thickness(1), CornerRadius = (CornerRadius)FindResource("RadiusSmall"), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 10, 0, 0), Child = text, Visibility = Visibility.Collapsed };
    }

    private Border FamilyCallout(string title, string text, string tone)
    {
        var stack = new StackPanel();
        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        if (tone == "Danger") heading.Foreground = Palette.Get("Danger");
        stack.Children.Add(heading);
        if (text.Length > 0) stack.Children.Add(FamilyNote(text, margin: new Thickness(0, 4, 0, 0)));
        return new Border { BorderBrush = Palette.Get(tone == "Danger" ? "DangerLine" : tone == "Accent" ? "Accent" : "Line"), Background = Palette.Get(tone == "Danger" ? "DangerSurface" : "Raised"), BorderThickness = new Thickness(1), CornerRadius = (CornerRadius)FindResource("RadiusCard"), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 16, 30, 0), Child = stack };
    }

    private StackPanel FamilyLabeled(string label, Control field)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        stack.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(2, 0, 0, 0) });
        stack.Children.Add(field);
        return stack;
    }

    private Border FamilyPill(string text, string tone) => new()
    {
        Background = Palette.Get(tone == "Success" ? "SuccessSurface" : "Raised"), BorderBrush = Palette.Get(tone == "Success" ? "SuccessLine" : "Line"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get(tone == "Success" ? "Success" : "TextSecondary") },
    };

    private DockPanel FamilyCheckLine(HomeGuide.Mark mark, string text)
    {
        var line = new DockPanel { Margin = new Thickness(0, 6, 30, 0) };
        var (glyph, tone) = mark switch { HomeGuide.Mark.Ok => ("", "Success"), HomeGuide.Mark.Warn => ("", "Warning"), _ => ("", "Danger") };
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, Foreground = Palette.Get(tone), Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
        line.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        return line;
    }

    /// <summary>A drawing of the console's page at a step: what to click, numbered as the step's lines (a schematic, not a capture).</summary>
    private Border FamilySketch(int step)
    {
        var (address, lines) = HomeGuide.Drawing(step);
        var stack = new StackPanel();
        var bar = new Border { Background = Palette.Get("Field"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 10, 5), Margin = new Thickness(0, 0, 0, 10), Child = new TextBlock { Text = address, FontSize = 11.5, Foreground = Palette.Get("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis } };
        stack.Children.Add(bar);
        foreach (var line in lines) stack.Children.Add(SketchLine(line));
        var frame = new Border { Background = Palette.Get("Surface"), BorderBrush = Palette.Get("Line"), BorderThickness = new Thickness(1), CornerRadius = (CornerRadius)FindResource("RadiusCard"), Padding = new Thickness(14, 12, 14, 14), Margin = new Thickness(0, 18, 30, 0), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, Child = stack };
        AutomationProperties.SetName(frame, L("Dessin de la page de Tailscale", "Drawing of Tailscale’s page"));
        return frame;

        FrameworkElement SketchLine(HomeGuide.Sketch line)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), Opacity = line.Dim ? 0.45 : 1 };
            if (line.Marker > 0)
            {
                var badge = new Squircle { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Palette.Get("Accent"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = line.Marker.ToString(Lang.Culture), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Palette.Get("OnAccent"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                DockPanel.SetDock(badge, Dock.Right); row.Children.Add(badge);
            }
            switch (line.Kind)
            {
                case HomeGuide.SketchKind.Heading:
                    row.Children.Add(new TextBlock { Text = line.Label, FontWeight = FontWeights.SemiBold, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
                    break;
                case HomeGuide.SketchKind.Button:
                {
                    var button = new Border { Background = Palette.Get("Text"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 10, 5), HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = line.Label, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("Bg") } };
                    row.Children.Add(button);
                    break;
                }
                case HomeGuide.SketchKind.Field:
                {
                    var field = new StackPanel();
                    field.Children.Add(new TextBlock { Text = line.Label, FontSize = 11.5, Foreground = Palette.Get("TextSecondary") });
                    field.Children.Add(new Border { BorderBrush = Palette.Get("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 3, 8, 4), Margin = new Thickness(0, 3, 0, 0), Child = new TextBlock { Text = line.Value ?? "", FontSize = 12, FontFamily = new FontFamily("Cascadia Mono, Consolas") } });
                    row.Children.Add(field);
                    break;
                }
                case HomeGuide.SketchKind.Choice:
                {
                    var choice = new StackPanel { Orientation = Orientation.Horizontal };
                    choice.Children.Add(new Ellipse { Width = 12, Height = 12, Stroke = Palette.Get(line.On ? "Accent" : "LineStrong"), StrokeThickness = line.On ? 4 : 1.5, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                    var label = new TextBlock { Text = line.Label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
                    if (line.Wrong) label.TextDecorations = TextDecorations.Strikethrough;
                    choice.Children.Add(label);
                    if (line.Value is { } value) choice.Children.Add(new TextBlock { Text = "  " + value, FontSize = 12, Foreground = Palette.Get(line.Wrong ? "Danger" : "TextSecondary"), VerticalAlignment = VerticalAlignment.Center });
                    row.Children.Add(choice);
                    break;
                }
                case HomeGuide.SketchKind.Scope:
                {
                    var scope = new DockPanel();
                    var ticks = new StackPanel { Orientation = Orientation.Horizontal };
                    foreach (var word in new[] { "Read", "Write" })
                    {
                        ticks.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Margin = new Thickness(10, 0, 4, 0), Background = line.On ? Palette.Get("Accent") : Brushes.Transparent, BorderBrush = Palette.Get(line.On ? "Accent" : "LineStrong"), BorderThickness = new Thickness(1.5), VerticalAlignment = VerticalAlignment.Center });
                        ticks.Children.Add(new TextBlock { Text = word, FontSize = 11.5, Foreground = Palette.Get("TextSecondary"), VerticalAlignment = VerticalAlignment.Center });
                    }
                    DockPanel.SetDock(ticks, Dock.Right); scope.Children.Add(ticks);
                    var name = new StackPanel { Orientation = Orientation.Horizontal };
                    name.Children.Add(new TextBlock { Text = line.Label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
                    if (line.Tag is { } tag) name.Children.Add(new Border { Background = Palette.Get("Raised"), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 2), Margin = new Thickness(8, 0, 0, 0), Child = new TextBlock { Text = tag, FontSize = 11, FontFamily = new FontFamily("Cascadia Mono, Consolas") } });
                    scope.Children.Add(name);
                    row.Children.Add(scope);
                    break;
                }
            }
            return row;
        }
    }
}
