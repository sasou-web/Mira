using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Services;
using Mira.Mac.Views;
using static Mira.Core.Lang;

namespace Mira.Mac;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    private static MainWindow? Main => (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;
    private void About_Click(object? sender, EventArgs e) => Updates.Open(Updates.Project);
    private void Settings_Click(object? sender, EventArgs e) => Main?.Go("settings");
    private void Updates_Click(object? sender, EventArgs e) { if (Main is { } window) _ = Updates.CheckAsync(window, asked: true); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            if (Array.IndexOf(args, "--video-probe") is var probe and >= 0 && probe + 1 < args.Length)
            { Checks.VideoProbe.Start(desktop, args[probe + 1], args.Contains("--software")); base.OnFrameworkInitializationCompleted(); return; }
            // --data <folder>: another profile (the self-check uses a fresh one).
            var data = Array.IndexOf(args, "--data") is var index and >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
            // The session's token goes to the login keychain. The checks keep it in their profile's file, so that no keychain
            // question can hold their sign-in on a Mac nobody watches (GitHub's); the self-check checks the keychain apart,
            // with a time limit. The key of « Tailscale intégré » goes the same way (Profile.TailnetKey), in the same item:
            // one question from macOS after an update rather than one per secret.
            var tokens = OperatingSystem.IsMacOS() && !args.Contains("--self-check") && !args.Contains("--tailnet-check") ? new KeychainVault(new Keychain(), data ?? Profile.DefaultDirectory()) : null;
            var window = new MainWindow(args, new Profile(data, tokens));
            desktop.MainWindow = window;
            NameMenu();
            // mira:// links (« Ouvrir dans Mira » of Mira web, CFBundleURLTypes in tools/mac/package.sh), given by macOS
            // to the Mira already open, or to the one it starts for them.
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activation)
                activation.Activated += (_, e) => { if (e is ProtocolActivatedEventArgs { Kind: ActivationKind.OpenUri } link) Dispatcher.UIThread.Post(() => window.OpenLink(link.Uri.OriginalString)); };
            Dispatcher.UIThread.UnhandledException += (_, e) => Unhandled(desktop, e);
        }
        base.OnFrameworkInitializationCompleted();
    }
    /// <summary>The Mira menu of the menu bar (App.axaml), in Mira's language: set by the window as it opens.</summary>
    private void NameMenu()
    {
        if (NativeMenu.GetMenu(this)?.Items.OfType<NativeMenuItem>().ToList() is not [var about, var settings, var updates]) return;
        about.Header = L("À propos de Mira", "About Mira");
        settings.Header = L("Réglages…", "Settings…");
        updates.Header = L("Rechercher une mise à jour…", "Check for updates…");
    }
    /// <summary>As on Windows: an unexpected error is logged (errors.log) and said, rather than closing Mira without a word.</summary>
    private static void Unhandled(IClassicDesktopStyleApplicationLifetime desktop, DispatcherUnhandledExceptionEventArgs e)
    {
        if (desktop.MainWindow is not MainWindow window) return;
        ErrorLog.Append(window.Profile.DirectoryPath, e.Exception);
        // The checks fail on it, with the stack in their log.
        if (window.Args.Contains("--self-check") || window.Args.Contains("--tailnet-check")) return;
        e.Handled = true;
        // Closing, or not open yet: nothing is left to retry from, and a hidden window would keep Mira running. It ends.
        if (window.IsClosing || !window.IsVisible) { desktop.Shutdown(1); return; }
        window.Notice(L("Une opération n’a pas abouti. Tu peux réessayer ; la progression déjà enregistrée reste conservée.", "Something didn’t work. You can try again; the progress already saved is kept."));
    }
}
