using System.IO;
using System.Windows;
using Mira.Core;
using Mira.Desktop.Services;

namespace Mira.Desktop;

public partial class App : Application
{
    private Mutex? _instance;
    private InstanceActivation? _activation;
    private MainWindow? _window;
    // Also read by the activation pipe's thread.
    private volatile bool _shown, _activationPending;
    /// <summary>A mira:// link given to a launch while this one was starting: opened once the window is there.</summary>
    private volatile string? _pendingLink;
    internal static string ShellAppId { get; private set; } = WindowsIdentity.AppId;
    internal static bool ShellValidation { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // A mira:// link (« Ouvrir dans Mira » of Mira web, registered by WindowsIdentity): a web page wrote it, so nothing
        // else of that command line is read, and the link itself only ever offers to join a group (MainWindow.OpenLink).
        var args = e.Args; string? link = null;
        if (args.FirstOrDefault() == "--open") { link = args.Length == 2 ? args[1] : null; args = []; }
#if !MIRA_VALIDATION
        // A package: the validation harnesses are not in it (Mira.Desktop.csproj), and their options open nothing.
        if (args.Any(x => x is "--visual-check" or "--player-check" or "--windows-check" or "--torlink-check" or "--public-gallery"
            or "--readme-gallery" or "--qa-window" or "--perf-probe" or "--test-media" or "--loop-test-media")) { Shutdown(2); return; }
#endif
        // The downloaded version replacing the old files: no window, no profile, no single-instance lock.
        if (args.FirstOrDefault() == "--apply-update") { Shutdown(UpdateApplier.Run(args)); return; }
        if (args.Contains("--register-windows"))
        {
            WindowsIdentity.SetProcessIdentity(WindowsIdentity.AppId);
            WindowsIdentity.Register();
            var shortcutArg = Array.IndexOf(args, "--shortcut");
            if (shortcutArg >= 0 && shortcutArg + 1 < args.Length) WindowsIdentity.WriteShortcut(args[shortcutArg + 1], Environment.ProcessPath!);
            Shutdown(WindowsIdentity.RegistrationError is null ? 0 : 1); return;
        }
        // Reopened by a closing Mira ("Redémarrer" when no installation could start): let that process end first.
        var afterArg = Array.IndexOf(args, "--after");
        if (afterArg >= 0 && afterArg + 1 < args.Length && int.TryParse(args[afterArg + 1], out var previous) && previous != Environment.ProcessId)
            try { using var closing = System.Diagnostics.Process.GetProcessById(previous); closing.WaitForExit(30_000); } catch (ArgumentException) { } catch (InvalidOperationException) { }
        var dataArg = Array.IndexOf(args, "--data");
        if (args.Contains("--public-gallery") &&
            (!args.Contains("--demo") || dataArg < 0 || dataArg + 1 >= args.Length || Array.IndexOf(args, "--public-gallery") + 1 >= args.Length))
        { Shutdown(2); return; }
        var directory = dataArg >= 0 && dataArg + 1 < args.Length ? args[dataArg + 1] : Path.Combine(AppContext.BaseDirectory, "data");
        if (args.Contains("--public-gallery") && Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        { Shutdown(2); return; }
        AppFiles.ProfileDirectory = Path.GetFullPath(directory);
        // Setup's "Télécharger le moteur vidéo mpv" task: no window, and an engine already on this PC is kept.
        if (args.Contains("--install-engine")) { Shutdown(InstallEngine(directory)); return; }
        var installed = Updater.Detect(AppContext.BaseDirectory, !File.Exists(Path.Combine(AppContext.BaseDirectory, "Mira.dll"))) == InstallKind.Installer;
        // Same key as the update lock of this profile.
        var key = UpdateLock.Key(directory); var instance = "Local\\Mira-" + key;
        ShellValidation = dataArg >= 0 || args.Any(x => x is "--demo" or "--visual-check" or "--player-check" or "--windows-check" or "--qa-window");
        ShellAppId = ShellValidation ? WindowsIdentity.AppId + ".Validation." + key : WindowsIdentity.AppId;
        WindowsIdentity.SetProcessIdentity(ShellAppId);
        // A second round only after a Mira of this profile that was closing has ended: this launch then opens as usual.
        for (var attempt = 0; ; attempt++)
        {
            // The files of this copy are being replaced by an update, or its installer runs: wait rather than start half-replaced.
            if (!WaitForUpdate(directory, installed, TimeSpan.FromSeconds(30)))
            { MessageBox.Show("Mira est en cours de mise à jour. Relance-le dans un instant.", "Mira"); Shutdown(); return; }
            // An update of this folder cut short (power loss): its previous files come back and Mira starts again on them.
            if (UpdateApplier.RecoverAtStartup(directory, args, instance)) { Shutdown(); return; }
            _instance = new Mutex(true, instance, out var created);
            if (created) break;
            var answer = InstanceActivation.ShowExisting(key, link);
            // Its window comes forward, or shows by itself once it has started.
            if (answer is ActivationAnswer.Shown or ActivationAnswer.Starting) { _instance.Dispose(); _instance = null; Shutdown(); return; }
            // Closing (its last progress being sent, an update starting), or past its window and about to end: it releases
            // the lock as it ends. A Mira that does not answer at all is open but busy.
            var released = attempt == 0 && WaitReleased(_instance, TimeSpan.FromSeconds(answer == ActivationAnswer.Closing ? 30 : 3));
            _instance.Dispose(); _instance = null;
            if (released) continue;
            MessageBox.Show(answer == ActivationAnswer.Closing ? "Mira est encore en train de se fermer. Relance-le dans un instant." : "Mira est déjà ouvert. Retrouve sa fenêtre dans la barre des tâches.", "Mira");
            Shutdown(); return;
        }
        // Listening from now on: a launch while this one starts is answered « starting » instead of « déjà ouvert ».
        _activation = new InstanceActivation(key, Activate);
        // --windows-check: the whole failure, stack included, for the harness that reads its output folder.
        bool CheckFailed(Exception exception)
        {
            if (!args.Contains("--windows-check") || !args.Contains("--demo") || dataArg < 0) return false;
            var output = args[Array.IndexOf(args, "--windows-check") + 1]; Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "failure.txt"), exception.ToString());
            return true;
        }
        // The only Mira of this profile, before any engine is loaded: a newer engine prepared last time replaces Mira's own.
        MpvInstaller.ActivatePrepared(AppFiles.ProfileDirectory);
        DispatcherUnhandledException += (_, args) =>
        {
            if (CheckFailed(args.Exception)) { args.Handled = true; Shutdown(1); return; }
            ErrorLog.Append(AppFiles.ProfileDirectory, args.Exception);
            args.Handled = true;
            // Closing, or before the window showed: nothing is left to retry from, only a process without a window that
            // keeps the single-instance lock (every relaunch would say "déjà ouvert"). Mira ends instead.
            if (!_shown || _window is not { IsClosing: false } window) { Shutdown(1); return; }
            // In Mira's own message, at most once a minute, with the log at hand (a system box, the same each time, was).
            try { window.ReportUnexpected(); } catch (Exception ex) { ErrorLog.Append(AppFiles.ProfileDirectory, ex); }
        };
        try
        {
            var window = _window = new MainWindow(args);
            if (args.Contains("--visual-check") || args.Contains("--player-check") || args.Contains("--public-gallery") || args.Contains("--readme-gallery")) { window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -30000; window.Top = -30000; window.ShowInTaskbar = false; }
            // --offscreen: validation runs with an isolated profile (update checks) stay off the user's screen, closable as usual.
            else if (args.Contains("--offscreen") && dataArg >= 0) { window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -30000; window.Top = -30000; }
            if (args.Contains("--qa-window")) window.Title = "Mira · Validation du lecteur";
            window.Show(); _shown = true;
            // Launched again while this one was starting: brought forward now that it is there.
            if (_activationPending) window.RestoreFromWindows();
            if ((link ?? _pendingLink) is { } opened) window.OpenLink(opened);
        }
        // No window to retry from (the data folder read-only on a first start, a full disk): say why and end.
        catch (Exception ex)
        {
            if (CheckFailed(ex)) { Shutdown(1); return; }
            ErrorLog.Append(AppFiles.ProfileDirectory, ex);
            if (!ShellValidation) MessageBox.Show(ex is IOException or UnauthorizedAccessException
                ? $"Mira ne peut pas écrire dans son dossier de données :\n{AppFiles.ProfileDirectory}\n\nVérifie qu’il reste de la place sur le disque et que ce dossier n’est pas en lecture seule, ou place Mira dans un dossier à toi (Documents, par exemple), puis relance-le."
                : "Mira n’a pas pu démarrer. Relance-le dans un instant.", "Mira", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }
        // The installed copy keeps the Start menu's shortcut; another copy only fills in a missing or broken one.
        if (!ShellValidation) Dispatcher.BeginInvoke(() => { if (WindowsIdentity.ShouldRegister(installed)) WindowsIdentity.Register(); }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
    /// <summary>
    /// What a second launch of this profile is told, from the pipe's thread (with the mira:// link it was given, if any): « starting » before the window shows (it
    /// is brought forward once shown), « closing » once closing has begun, else the window comes forward.
    /// </summary>
    private ActivationAnswer Activate(string? link)
    {
        if (!_shown) { _activationPending = true; if (link is not null) _pendingLink = link; return ActivationAnswer.Starting; }
        if (_window is not { IsClosing: false } window) return ActivationAnswer.Closing;
        Dispatcher.BeginInvoke(() => { window.RestoreFromWindows(); if (link is not null) window.OpenLink(link); });
        return ActivationAnswer.Shown;
    }
    /// <summary>
    /// Waits while this copy is being updated, or its installer runs, with a small window that says so (instead of a
    /// launch that seems to do nothing for up to 30 s) and without blocking the UI thread. False when it still runs
    /// after <paramref name="timeout"/>, or when the window is closed.
    /// </summary>
    private bool WaitForUpdate(string directory, bool installed, TimeSpan timeout)
    {
        if (!UpdateLock.Busy(directory, installed)) return true;
        // Closing the waiting window must not end Mira as its last window.
        var mode = ShutdownMode; ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var frame = new System.Windows.Threading.DispatcherFrame(); var idle = false; var waiting = true;
        var window = new Window
        {
            Title = "Mira", Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 16, 18)),
            Content = new System.Windows.Controls.TextBlock { Text = "Mira termine une mise à jour : il s’ouvre dès qu’elle est finie.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24), FontSize = 14, Foreground = System.Windows.Media.Brushes.WhiteSmoke }
        };
        window.Closed += (_, _) => { waiting = false; frame.Continue = false; };
        _ = UpdateLock.WaitIdleAsync(directory, installed, timeout).ContinueWith(done => Dispatcher.BeginInvoke(() =>
        { idle = done.IsCompletedSuccessfully && done.Result; waiting = false; frame.Continue = false; }), TaskScheduler.Default);
        try
        {
            window.Show();
            if (waiting) System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        finally { if (window.IsVisible) window.Close(); ShutdownMode = mode; }
        return idle;
    }
    /// <summary>Whether the Mira holding the lock released it (or ended without releasing it) within <paramref name="timeout"/>.</summary>
    private static bool WaitReleased(Mutex mutex, TimeSpan timeout)
    {
        try { if (!mutex.WaitOne(timeout)) return false; }
        catch (AbandonedMutexException) { }
        mutex.ReleaseMutex(); return true;
    }
    private static int InstallEngine(string directory)
    {
        if (Playback.MpvEngine.FindLibrary(new LocalProfile(directory).LoadSettings().MpvPath) is not null) return 0;
        try { Task.Run(() => Mira.Core.MpvInstaller.InstallAsync(AppFiles.ProfileDirectory)).GetAwaiter().GetResult(); return 0; }
        // Setup goes on: Mira offers the engine again before the first playback.
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException) { return 1; }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _activation?.Dispose(); _instance?.ReleaseMutex(); _instance?.Dispose(); base.OnExit(e);
    }
}
