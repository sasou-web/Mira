using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Mira.Core.Tailscale;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Tailscale intégré » in the settings, as in Réglages → Jellyfin on Windows: where the node is and how Mira reaches
/// Jellyfin, what it is for and what Tailscale sees of it, then the buttons that fit (sign in or its page again, try
/// again, approve in the console, sign out), and the command that lets a node Gatekeeper holds run. Drawn again each
/// time the node changes while it shows.
/// </summary>
public sealed class TailnetSection
{
    private readonly BuiltInTailscale _tailscale;
    private readonly Mira.Core.PlayerSettings _settings;
    private readonly TextBlock _state = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _way = Note("");
    private readonly TextBlock _said = Note("");
    private readonly TextBlock _outage = new() { Text = TailnetText.Outage, FontSize = 13, Foreground = Ui.Brush("#D5B787"), TextWrapping = TextWrapping.Wrap };
    private readonly Button _signIn, _signOut, _console, _copy;
    private readonly WrapPanel _outageButtons;

    public Control Section { get; }

    public TailnetSection(MainWindow shell, Func<string, Control[], Control> section)
    {
        _tailscale = shell.Tailnet; _settings = shell.Settings;
        _signIn = Ui.Action(TailnetText.SignIn, null, "primary", () => _ = _tailscale.SignInAsync());
        _signOut = Ui.Action(TailnetText.SignOut, "exit", "", () => _ = _tailscale.SignOutAsync());
        _console = Ui.Action(TailnetText.Console, "globe", "", BuiltInTailscale.OpenConsole);
        _copy = Ui.Action(L("Copier la commande", "Copy the command"), "link", "", () => _ = CopyAsync(shell, TailnetExecutable.Command,
            L("Commande copiée : colle-la dans le Terminal, puis relance Mira.", "Command copied: paste it in Terminal, then open Mira again.")));
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_said, Avalonia.Automation.AutomationLiveSetting.Polite);
        var buttons = new WrapPanel { Children = { _signIn, _signOut, _console, _copy } };
        foreach (var button in buttons.Children) button.Margin = new Thickness(0, 4, 10, 4);
        // On a tailnet that doesn't let it start during an outage of Tailscale: the setting, gone once the policy has it.
        var copyOutage = Ui.Action(TailnetText.CopyOutage, "link", "", () => _ = CopyAsync(shell, TailnetText.OutageSetting, TailnetText.OutageCopied));
        var policy = Ui.Action(TailnetText.OpenPolicy, "globe", "", () => Updates.Open(new Uri(Mira.Core.TailscaleNode.Policy)));
        _outageButtons = new WrapPanel { Children = { copyOutage, policy } };
        foreach (var button in _outageButtons.Children) button.Margin = new Thickness(0, 4, 10, 4);
        Section = section(TailnetText.Name, [Ui.Column(10, _state, _way, Note(TailnetText.Purpose + " " + TailnetText.Privacy), _outage, _outageButtons, buttons, _said)]);
        Section.IsVisible = _tailscale.Offered;
        if (!_tailscale.Offered) return;
        Render();
        Section.AttachedToVisualTree += (_, _) => { _tailscale.Changed += Render; _ = _tailscale.ReadAppAsync(); Render(); };
        Section.DetachedFromVisualTree += (_, _) => _tailscale.Changed -= Render;
    }

    private static TextBlock Note(string text) => new() { Text = text, FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
    /// <summary>The button's label, which VoiceOver reads too.</summary>
    private static void Label(Button button, string text)
    {
        if (button.Content is StackPanel content && content.Children.OfType<TextBlock>().FirstOrDefault() is { } label) label.Text = text;
        Avalonia.Automation.AutomationProperties.SetName(button, text);
    }

    /// <summary>The node as it is now: its line, the way to Jellyfin, what was last said, and the buttons that fit.</summary>
    private void Render()
    {
        var tailnet = _tailscale.Current; var helper = tailnet?.Helper ?? TailnetHelper.Off; var state = tailnet?.State;
        var signedIn = _settings.Tailnet; var signingIn = tailnet?.SigningIn == true; var page = tailnet?.Page;
        // The Tailscale app connected here (often Jellyfin's Mac): said, rather than « Pas connecté ».
        _state.Text = _tailscale.AppHere && !signedIn && !signingIn ? TailnetText.AppHere : _tailscale.Line();
        _way.Text = _tailscale.Way() is { } way ? TailnetText.Path(way) : "";
        _way.IsVisible = _way.Text.Length > 0;
        _said.Text = _tailscale.Said; _said.IsVisible = _said.Text.Length > 0 && _said.Text != _state.Text;
        var stopped = signedIn && helper is TailnetHelper.Off or TailnetHelper.Stopped or TailnetHelper.Failed or TailnetHelper.Restarting;
        var needsLogin = state is TailnetStates.NeedsLogin or TailnetStates.Expired;
        Label(_signIn, signingIn && page is not null ? TailnetText.ReopenPage : stopped ? TailnetText.Retry : TailnetText.SignIn);
        _signIn.IsEnabled = !signingIn || page is not null;
        _signIn.IsVisible = tailnet?.Problem is null && (!signedIn || signingIn || needsLogin || stopped) && state != TailnetStates.MachineAuth;
        _signOut.IsVisible = signedIn;
        _signOut.IsEnabled = !_tailscale.SigningOut;
        _console.IsVisible = signedIn || state == TailnetStates.MachineAuth;
        // Held by Gatekeeper: its command, to paste in Terminal (TailnetExecutable.Blocked says it in the line above).
        _copy.IsVisible = helper == TailnetHelper.Failed && TailnetExecutable.Blocked() is not null;
        _outage.IsVisible = _outageButtons.IsVisible = tailnet?.AsksOutageCache == true;
    }

    /// <summary>What the section shows (the checks): its line, the way to Jellyfin, and which of its buttons.</summary>
    public (bool Visible, string State, string Way, bool SignIn, bool SignOut) Shown =>
        (Section.IsVisible, _state.Text ?? "", _way.IsVisible ? _way.Text ?? "" : "", _signIn.IsVisible, _signOut.IsVisible);

    private async Task CopyAsync(Visual visual, string text, string copied)
    {
        if (TopLevel.GetTopLevel(visual)?.Clipboard is not { } clipboard) { _tailscale.Show(L("Copie impossible ici.", "Can’t copy here.")); return; }
        await clipboard.SetTextAsync(text);
        _tailscale.Show(copied);
    }
}
