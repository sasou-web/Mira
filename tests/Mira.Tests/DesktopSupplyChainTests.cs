using System.IO;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Desktop.TorLink;

/// <summary>What Mira installs beside itself, the part that needs the Windows app (TorLink found and trusted).</summary>
internal static class DesktopSupplyChainTests
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test, string root)
    {
        await test("TorLink : celui choisi dans les réglages ou installé par Mira se lance sans confirmation", () =>
        {
            var profile = Path.Combine(root, "torlink-trust", "data"); var chosen = Path.Combine(root, "torlink-trust", "chosen");
            foreach (var folder in new[] { TorLinkPackage.PackageRoot(profile), chosen })
            {
                Directory.CreateDirectory(Path.Combine(folder, "dist"));
                File.WriteAllText(Path.Combine(folder, "package.json"), $$"""{"name":"torlnk","version":"{{TorLinkPackage.Current.Version}}"}""");
                File.WriteAllText(Path.Combine(folder, "dist", "cli.cjs"), "");
            }
            Assert(TorLinkInstallation.Locate(chosen, profile) is { Detected: false, Managed: false } found && found.Root == Path.GetFullPath(chosen), "The TorLink chosen in the settings asks for a confirmation");
            Assert(TorLinkInstallation.Locate(null, profile) is { Detected: false, Managed: true }, "Mira's own TorLink asks for a confirmation");
            Assert(TorLinkInstallation.Validate(chosen) is { Detected: false }, "A folder checked on its own counts as found by searching");
            return Task.CompletedTask;
        });
    }
}
