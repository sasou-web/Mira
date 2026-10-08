using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Mira.Mac.Services;
using Mira.Mac.Views;

namespace Mira.Mac;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    private static MainWindow? Main => (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;
    private void About_Click(object? sender, EventArgs e) => Updates.Open("https://github.com/sasou-web/Mira");
    private void Settings_Click(object? sender, EventArgs e) => Main?.Go("settings");
    private void Updates_Click(object? sender, EventArgs e) { if (Main is { } window) _ = Updates.CheckAsync(window, asked: true); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            if (Array.IndexOf(args, "--export-icon") is var icon and >= 0 && icon + 1 < args.Length)
            { var file = Path.GetFullPath(args[icon + 1]); desktop.Startup += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Checks.IconExport.Run(desktop, file)); base.OnFrameworkInitializationCompleted(); return; }
            if (Array.IndexOf(args, "--video-probe") is var probe and >= 0 && probe + 1 < args.Length)
            { Checks.VideoProbe.Start(desktop, args[probe + 1], args.Contains("--software")); base.OnFrameworkInitializationCompleted(); return; }
            // --data <folder>: another profile (the self-check uses a fresh one).
            var data = Array.IndexOf(args, "--data") is var index and >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
            desktop.MainWindow = new MainWindow(args, new Profile(data));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
