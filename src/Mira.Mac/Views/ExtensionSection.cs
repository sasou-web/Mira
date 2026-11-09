using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Mira.Core;
using Mira.Core.Updates;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Extension Mira sur Jellyfin » (Réglages → Compte et serveur), for an administrator of a Jellyfin that does not
/// serve it: what Mira for Windows' guide does, from a Mac. This version's release is checked first (signed manifest,
/// the plugin's size and SHA-256, <see cref="MiraWebRelease"/>), then Jellyfin installs it from that repository only,
/// then restarts to load it, a few seconds, and only once nobody watches anything on it (this Mac included). Never
/// done without the click, and said at each step. Hidden for any other account, and once Jellyfin serves it.
/// </summary>
public sealed class ExtensionSection
{
    private readonly MainWindow _shell;
    private readonly TextBlock _status = new() { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _install;
    private bool _busy;

    public Control Section { get; }
    /// <summary>The self-check's stand-in for GitHub: the repository the release check would give.</summary>
    public static Func<Task<string>>? Verified { get; set; }
    /// <summary>For the self-check: where the last install stands.</summary>
    public string Status => _status.Text ?? "";
    /// <summary>How often Jellyfin is asked whether someone still watches, before it restarts.</summary>
    public static TimeSpan Wait { get; set; } = TimeSpan.FromMinutes(1);

    public ExtensionSection(MainWindow shell, Func<string?, Control[], Control> section)
    {
        _shell = shell;
        _install = Ui.Action(L("Installer", "Install"), "download", "primary", () => _ = InstallAsync());
        Avalonia.Automation.AutomationProperties.SetName(_install, L("Installer l’extension Mira sur Jellyfin", "Install Mira’s extension on Jellyfin"));
        var row = new DockPanel();
        DockPanel.SetDock(_install, Dock.Right); _install.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top; _install.Margin = new Thickness(18, 0, 0, 0);
        row.Children.Add(_install);
        row.Children.Add(Ui.Column(6,
            new TextBlock { Text = L("Ton Jellyfin n’a pas l’extension Mira", "Your Jellyfin doesn’t have Mira’s extension"), FontSize = 14.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = About, FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap }));
        Section = section(null, [Ui.Column(14, row, _status)]);
        Section.IsVisible = false;
        // Asked each time it can be seen: the extension may have come (or gone) since.
        Page.WhileShown(Section, shown => { if (shown) _ = LoadAsync(); });
    }
    /// <summary>The self-check's click on « Installer ».</summary>
    public void InstallForCheck() => _ = InstallAsync();
    private static string About => L("Elle ajoute le lien de party, les invités sans compte, les sous-titres automatiques et Téléchargements. Mira vérifie la signature de cette version, puis Jellyfin la télécharge depuis GitHub et redémarre quelques secondes, seulement quand personne ne regarde.",
        "It adds the party link, guests without an account, automatic subtitles and Downloads. Mira checks this version’s signature, then Jellyfin downloads it from GitHub and restarts for a few seconds, only when nobody is watching.");

    /// <summary>Shown to an administrator of a Jellyfin that answers but serves no /Mira; asked again at each opening of the settings.</summary>
    private async Task LoadAsync()
    {
        if (_shell.Session is not { } session) return;
        try
        {
            if (!await session.Client.PingAsync(session.Lifetime) || await session.Client.IsAdministratorAsync(session.Lifetime) != true) return;
            // Offered only when Jellyfin says it has no such page (404), never on a mere timeout.
            var served = await session.Client.HasExtensionAsync(session.Lifetime);
            if (served is { } known && ReferenceEquals(session, _shell.Session)) Section.IsVisible = !known || _busy;
        }
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }

    private void Say(string text) { _status.IsVisible = true; _status.Text = text; }

    private async Task InstallAsync()
    {
        if (_busy || _shell.Session is not { } session) return;
        _busy = true; _install.IsEnabled = false;
        var ct = session.Lifetime;
        try
        {
            Say(L("Vérification de l’extension Mira…", "Checking Mira’s extension…"));
            string repository;
            try { repository = await (Verified ?? VerifyAsync)(); }
            catch (UpdateException ex) { Say(ex.Message); return; }
            Say(L("Jellyfin télécharge l’extension Mira…", "Jellyfin is downloading Mira’s extension…"));
            if (await session.Client.AddWebAppAsync(repository, ct) is { } refused) { Say(MiraWeb.Problem(refused)); return; }
            // Jellyfin loads a plugin only when it starts again: never under someone's film.
            for (var said = false; ; said = true)
            {
                var watching = (await session.Client.ViewersAsync(ct)).Count;
                if (watching == 0) break;
                if (!said) Say(L($"L’extension Mira est installée. {Lang.Count(watching, "lecture est en cours", "lectures sont en cours", "playback is on", "playbacks are on")} sur Jellyfin : il redémarre pour la charger dès que personne ne regarde.",
                    $"Mira’s extension is installed. {Lang.Count(watching, "lecture est en cours", "lectures sont en cours", "playback is on", "playbacks are on")} on Jellyfin: it restarts to load it as soon as nobody is watching."));
                await Task.Delay(Wait, ct);
                if (!ReferenceEquals(session, _shell.Session)) return;
            }
            Say(L("Jellyfin redémarre quelques secondes, personne ne regarde…", "Jellyfin is restarting for a few seconds, nobody is watching…"));
            _shell.Notice(L("Extension Mira : Jellyfin redémarre quelques secondes, personne ne regarde…", "Mira’s extension: Jellyfin is restarting for a few seconds, nobody is watching…"));
            var result = await session.Client.RestartForWebAppAsync(ct: ct);
            if (!ReferenceEquals(session, _shell.Session)) return;
            if (result == WebAppInstall.Ready)
            {
                Section.IsVisible = false;
                _shell.Notice(L("L’extension Mira est installée : ta party, les invités et les sous-titres automatiques sont prêts.", "Mira’s extension is installed: your party, guests and automatic subtitles are ready."), tone: NoticeTone.Done);
                _shell.ExtensionInstalled();
            }
            else Say(L("L’extension Mira est installée ; Jellyfin finit de redémarrer et la sert dès qu’il répond.", "Mira’s extension is installed; Jellyfin is finishing its restart and serves it as soon as it answers."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (Errors.Expected(ex)) { Say(Errors.Friendly(ex)); }
        finally { _busy = false; _install.IsEnabled = true; }
    }

    /// <summary>This version's release, checked on GitHub as an update would be: the repository Jellyfin may use.</summary>
    private static async Task<string> VerifyAsync()
    {
        using var github = new UpdateClient(Updates.Current, UpdateKeys.Trusted);
        return await MiraWebRelease.VerifyAsync(github, Updates.Current);
    }
}
