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
        if (e.Args.Contains("--register-windows"))
        {
            WindowsIdentity.SetProcessIdentity(WindowsIdentity.AppId);
            WindowsIdentity.Register();
            var shortcutArg = Array.IndexOf(e.Args, "--shortcut");
            if (shortcutArg >= 0 && shortcutArg + 1 < e.Args.Length) WindowsIdentity.WriteShortcut(e.Args[shortcutArg + 1], Environment.ProcessPath!);
            Shutdown(WindowsIdentity.RegistrationError is null ? 0 : 1); return;
        }
        var dataArg = Array.IndexOf(e.Args, "--data");
        if (e.Args.Contains("--public-gallery") &&
            (!e.Args.Contains("--demo") || dataArg < 0 || dataArg + 1 >= e.Args.Length || Array.IndexOf(e.Args, "--public-gallery") + 1 >= e.Args.Length))
        { Shutdown(2); return; }
        var directory = dataArg >= 0 && dataArg + 1 < e.Args.Length ? e.Args[dataArg + 1] : Path.Combine(AppContext.BaseDirectory, "data");
        if (e.Args.Contains("--public-gallery") && Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        { Shutdown(2); return; }
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())))[..24];
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
            var path = Path.Combine(AppContext.BaseDirectory, "data"); Directory.CreateDirectory(path);
            File.AppendAllText(Path.Combine(path, "errors.log"), $"{DateTimeOffset.Now:O} {args.Exception.GetType().Name}\n");
            MessageBox.Show("Une opération n’a pas abouti. Tu peux réessayer ; la progression déjà enregistrée reste conservée.", "Mira", MessageBoxButton.OK, MessageBoxImage.Information);
            args.Handled = true;
        };
        var window = new MainWindow(e.Args);
        if (e.Args.Contains("--visual-check") || e.Args.Contains("--player-check") || e.Args.Contains("--public-gallery")) { window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -30000; window.Top = -30000; window.ShowInTaskbar = false; }
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
