using System.IO;
using System.Windows;
using Mira.Desktop.Services;

namespace Mira.Desktop;

public partial class App : Application
{
    private Mutex? _instance;
    private InstanceActivation? _activation;
    internal static string ShellAppId { get; private set; } = WindowsIdentity.AppId;
    internal static bool ShellValidation { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The downloaded version replacing the old files: no window, no profile, no single-instance lock.
        if (e.Args.FirstOrDefault() == "--apply-update") { Shutdown(UpdateApplier.Run(e.Args)); return; }
        if (e.Args.Contains("--register-windows"))
        {
            WindowsIdentity.SetProcessIdentity(WindowsIdentity.AppId);
            WindowsIdentity.Register();
            var shortcutArg = Array.IndexOf(e.Args, "--shortcut");
            if (shortcutArg >= 0 && shortcutArg + 1 < e.Args.Length) WindowsIdentity.WriteShortcut(e.Args[shortcutArg + 1], Environment.ProcessPath!);
            Shutdown(WindowsIdentity.RegistrationError is null ? 0 : 1); return;
        }
        // Reopened by a closing Mira ("Redémarrer" when no installation could start): let that process end first.
        var afterArg = Array.IndexOf(e.Args, "--after");
        if (afterArg >= 0 && afterArg + 1 < e.Args.Length && int.TryParse(e.Args[afterArg + 1], out var previous) && previous != Environment.ProcessId)
            try { using var closing = System.Diagnostics.Process.GetProcessById(previous); closing.WaitForExit(30_000); } catch (ArgumentException) { } catch (InvalidOperationException) { }
        var dataArg = Array.IndexOf(e.Args, "--data");
        if (e.Args.Contains("--public-gallery") &&
            (!e.Args.Contains("--demo") || dataArg < 0 || dataArg + 1 >= e.Args.Length || Array.IndexOf(e.Args, "--public-gallery") + 1 >= e.Args.Length))
        { Shutdown(2); return; }
        var directory = dataArg >= 0 && dataArg + 1 < e.Args.Length ? e.Args[dataArg + 1] : Path.Combine(AppContext.BaseDirectory, "data");
        if (e.Args.Contains("--public-gallery") && Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        { Shutdown(2); return; }
        AppFiles.ProfileDirectory = Path.GetFullPath(directory);
        // The files of this copy are being replaced by an update, or its installer runs: wait rather than start half-replaced.
        var installed = Updater.Detect(AppContext.BaseDirectory, !File.Exists(Path.Combine(AppContext.BaseDirectory, "Mira.dll"))) == InstallKind.Installer;
        if (!UpdateLock.WaitIdle(directory, installed, TimeSpan.FromSeconds(30)))
        { MessageBox.Show("Mira est en cours de mise à jour. Relance-le dans un instant.", "Mira"); Shutdown(); return; }
        // Same key as the update lock of this profile.
        var key = UpdateLock.Key(directory);
        ShellValidation = dataArg >= 0 || e.Args.Any(x => x is "--demo" or "--visual-check" or "--player-check" or "--windows-check" or "--qa-window");
        ShellAppId = ShellValidation ? WindowsIdentity.AppId + ".Validation." + key : WindowsIdentity.AppId;
        WindowsIdentity.SetProcessIdentity(ShellAppId);
        _instance = new Mutex(true, "Local\\Mira-" + key, out var created);
        if (!created)
        {
            _instance.Dispose(); _instance = null;
            if (!InstanceActivation.ShowExisting(key)) MessageBox.Show("Mira est déjà ouvert. Retrouve sa fenêtre dans la barre des tâches.", "Mira");
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            if (e.Args.Contains("--windows-check") && e.Args.Contains("--demo") && dataArg >= 0)
            {
                var output = e.Args[Array.IndexOf(e.Args, "--windows-check") + 1]; Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "failure.txt"), args.Exception.ToString());
                args.Handled = true; Shutdown(1); return;
            }
            // Do not log exception messages: HTTP errors can contain credentials or private media paths.
            // The type and the method that failed are enough to find the fault; kept in this run's profile, 256 KB at most.
            try
            {
                Directory.CreateDirectory(AppFiles.ProfileDirectory);
                var log = Path.Combine(AppFiles.ProfileDirectory, "errors.log");
                if (File.Exists(log) && new FileInfo(log).Length > 256 * 1024) File.Move(log, log + ".old", true);
                var site = args.Exception.TargetSite is { } method ? $" {method.DeclaringType?.FullName}.{method.Name}" : "";
                File.AppendAllText(log, $"{DateTimeOffset.Now:O} {args.Exception.GetType().Name}{site}\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            MessageBox.Show("Une opération n’a pas abouti. Tu peux réessayer ; la progression déjà enregistrée reste conservée.", "Mira", MessageBoxButton.OK, MessageBoxImage.Information);
            args.Handled = true;
        };
        var window = new MainWindow(e.Args);
        if (e.Args.Contains("--visual-check") || e.Args.Contains("--player-check") || e.Args.Contains("--public-gallery")) { window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -30000; window.Top = -30000; window.ShowInTaskbar = false; }
        // --offscreen: validation runs with an isolated profile (update checks) stay off the user's screen, closable as usual.
        else if (e.Args.Contains("--offscreen") && dataArg >= 0) { window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -30000; window.Top = -30000; }
        if (e.Args.Contains("--qa-window")) window.Title = "Mira · Validation du lecteur";
        _activation = new InstanceActivation(key, () => Dispatcher.BeginInvoke(window.RestoreFromWindows));
        window.Show();
        if (!ShellValidation) Dispatcher.BeginInvoke(WindowsIdentity.Register, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _activation?.Dispose(); _instance?.ReleaseMutex(); _instance?.Dispose(); base.OnExit(e);
    }
}
