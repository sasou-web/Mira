using System.Diagnostics;
using System.Globalization;
using System.IO;
using Mira.Core;
using Mira.Core.Tailscale;
using static TestSupport;

/// <summary>TailnetProcess, the app's side of mira-tailscale, against its stand-in (FakeTailnet): no Go, no network.</summary>
internal static class TailnetProcessTests
{
    public static async Task RunAsync(TestRun run)
    {
        TailnetProcess.Options Options(string folder, params string[] fake)
        {
            var (executable, arguments) = FakeTailnet.Command(fake);
            Directory.CreateDirectory(folder);
            return new()
            {
                Executable = executable, Arguments = arguments, Directory = Path.Combine(folder, "tailscale"), Hostname = "Mira-PC", AppVersion = "test",
                LogFile = Path.Combine(folder, "tailscale.log"), StateKey = () => new byte[32],
            };
        }

        await run.Test("Tailscale intégré : l’assistant démarré, connecté, sondé puis arrêté", async () =>
        {
            var folder = Path.Combine(run.Root, "tailnet-helper");
            Process? started = null;
            var statuses = new List<string>();
            var helper = new TailnetProcess(Options(folder) with { Started = process => started = process });
            helper.StatusChanged += status => { lock (statuses) statuses.Add(status.State); };
            Assert(await helper.ProbeAsync("100.64.0.2:8096", TimeSpan.FromSeconds(1)) == TailnetCodes.Helper, "A probe without a helper is not said so");
            await helper.StartAsync();
            Assert(helper.Helper == TailnetHelper.Running && started is { HasExited: false }, "The helper does not run, or was not handed to the app");
            Assert(helper.Proxy is { Address: FakeTailnet.ProxyAddress, User: "mira" } proxy && proxy.Password.Length == 32, "The proxy is not known with its credentials");
            Assert(helper.Status?.State == TailnetStates.NeedsLogin && helper.Status.AuthUrl == FakeTailnet.Page, "The status before the login is not the helper's");
            Assert(helper.TailscaleVersion == "1.104.4", "Tailscale's version is not read from the hello");

            Assert(await helper.LoginAsync() == FakeTailnet.Page, "The login page is not given");
            await WaitUntil(() => helper.Status?.State == TailnetStates.Running);
            var status = helper.Status!;
            Assert(status.User == "ogsas@example.com" && status.MagicDnsSuffix == "tail1234.ts.net" && status.Self?.Ips.SequenceEqual(["100.64.0.9"]) == true, "The running status is misread");
            Assert(status.Peer("desktop-jellyfin")?.Id == "n1" && status.Peer("DESKTOP-JELLYFIN.tail1234.ts.net.")?.Id == "n1" && status.Peer("[fd7a:115c:a1e0::2]")?.Id == "n1", "A device is not found by its names and addresses");
            Assert(status.Peer("100.64.0.3") is { Online: false, LastSeen: { } seen } && seen == new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero), "An offline device and when it was last seen are misread");
            Assert(status.Peer("100.64.0.7") is null, "An address outside the tailnet leads to a device");

            Assert(await helper.ProbeAsync("100.64.0.2:8096", TimeSpan.FromSeconds(5)) is null, "Jellyfin's PC does not answer the probe");
            Assert(await helper.ProbeAsync("100.64.0.3:8096", TimeSpan.FromSeconds(5)) == TailnetCodes.PeerOffline, "An offline device is not said so");
            Assert(await helper.ProbeAsync("192.168.1.20:8096", TimeSpan.FromSeconds(5)) == TailnetCodes.Refused, "An address outside the tailnet is not refused");
            await helper.RebindAsync();
            await helper.LogoutAsync();
            await WaitUntil(() => helper.Status?.State == TailnetStates.NeedsLogin);

            var process = started!;
            await helper.DisposeAsync();
            Assert(helper.Helper == TailnetHelper.Stopped && helper.Proxy is null && helper.Status is null, "The stopped helper still shows a proxy or a status");
            await WaitUntil(() => process.HasExited);
            Assert(process.ExitCode == 0, "The helper did not end by itself on « quit »");
            try { await helper.LoginAsync(); throw new Exception("A login was asked of a stopped helper"); }
            catch (TailnetException ex) when (ex.Code == TailnetCodes.Helper) { }
            lock (statuses) Assert(statuses.Contains(TailnetStates.Running), "No status change was told");
            var log = await File.ReadAllTextAsync(Path.Combine(folder, "tailscale.log"));
            Assert(log.Contains("<login page>") && !log.Contains("1a2b3c4d5e"), "A login page reached the log");
        });

        await run.Test("Tailscale intégré : un assistant d’une autre version, ou qui pourrait envoyer des journaux, ne tourne pas", async () =>
        {
            foreach (var fake in new[] { new[] { "--version", "0.0.1" }, new[] { "--log-upload" } })
            {
                await using var helper = new TailnetProcess(Options(Path.Combine(run.Root, "tailnet-refused"), fake));
                try { await helper.StartAsync(); throw new Exception($"A helper started with {string.Join(' ', fake)}"); }
                catch (TailnetException ex) when (ex.Code == TailnetCodes.Version) { }
                Assert(helper.Helper == TailnetHelper.Failed && helper.NextAttempt is null && helper.Proxy is null, $"A refused helper ({string.Join(' ', fake)}) is tried again or keeps a proxy");
            }
        });

        await run.Test("Tailscale intégré : l’assistant qui s’arrête repart tout seul, avec un autre mot de passe de proxy", async () =>
        {
            var folder = Path.Combine(run.Root, "tailnet-crash");
            await using var helper = new TailnetProcess(Options(folder, "--crash-once", Path.Combine(folder, "crashed")));
            var states = new List<TailnetHelper>();
            helper.HelperChanged += () => { lock (states) states.Add(helper.Helper); };
            await helper.StartAsync();
            var first = helper.Proxy?.Password;
            await WaitUntil(() => { lock (states) return states.Contains(TailnetHelper.Restarting); });
            Assert(helper.Failure?.Code == TailnetCodes.Helper, "Why the helper stopped is not kept");
            await WaitUntil(() => helper.Helper == TailnetHelper.Running && helper.Proxy is not null, seconds: 10);
            Assert(helper.Proxy!.Password != first && helper.Failure is null, "The helper started again with the same proxy password, or still shows its failure");
            Assert(await helper.ProbeAsync("100.64.0.2:8096", TimeSpan.FromSeconds(5)) is null, "The helper started again does not answer");
        });

        await run.Test("Tailscale intégré : un assistant arrêté avant son premier mot, deux fois de suite, n’est plus relancé tout seul", async () =>
        {
            await using var helper = new TailnetProcess(Options(Path.Combine(run.Root, "tailnet-silent"), "--silent"));
            try { await helper.StartAsync(); throw new Exception("A silent helper started"); }
            catch (TailnetException ex) when (ex.Code == TailnetCodes.Helper) { }
            Assert(helper.Helper == TailnetHelper.Restarting && helper.NextAttempt is not null, "A helper that ended at once is not tried again");
            await WaitUntil(() => helper.Helper == TailnetHelper.Failed, seconds: 10);
            Assert(helper.NextAttempt is null && helper.Failure?.Code == TailnetCodes.Helper, "A helper ending at once again and again is still tried again by itself");
            try { await helper.StartAsync(); throw new Exception("A silent helper started when asked again"); }
            catch (TailnetException ex) when (ex.Code == TailnetCodes.Helper) { }
            Assert(helper.Helper == TailnetHelper.Failed && helper.NextAttempt is null, "Asked again by the person, it is tried once and left");
        });

        await run.Test("Tailscale intégré : chaque état du nœud et chaque raison d’échec dits en mots, en français et en anglais", () =>
        {
            // The relay's (iPhone) and the protocol's say no more than « no answer »: Mira's own words then.
            string[] plain = [TailnetCodes.Certificate, TailnetCodes.Timeout, TailnetCodes.Protocol];
            foreach (var code in TailnetCodes.All)
                Assert(plain.Contains(code) ? TailnetText.Problem(code) is null : TailnetText.Problem(code) is { Length: > 0 }, $"« {code} » said as: {TailnetText.Problem(code) ?? "nothing"}");
            var nas = new TailnetDevice { HostName = "nas", LastSeen = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };
            Assert(TailnetText.Problem(TailnetCodes.PeerOffline, nas)!.StartsWith("nas n’est pas connecté à Tailscale (vu pour la dernière fois le 1 octobre à ", StringComparison.Ordinal), "An offline device: " + TailnetText.Problem(TailnetCodes.PeerOffline, nas));
            Assert(TailnetText.Problem(TailnetCodes.PeerOffline)!.StartsWith("Le PC de Jellyfin n’est pas connecté", StringComparison.Ordinal), "An offline device Tailscale does not name");
            Assert(TailnetText.Problem(TailnetCodes.Expired) == "La connexion de Tailscale intégré a expiré : « Se connecter à Tailscale » dans les Réglages." && TailnetText.Problem(TailnetCodes.NeedsLogin) != TailnetText.Problem(TailnetCodes.Expired),
                "An expired sign-in said as one never made, or the button not named as it is: " + TailnetText.Problem(TailnetCodes.Expired));
            Assert(TailnetText.State(TailnetHelper.Off, null) == "Pas connecté." && TailnetText.State(TailnetHelper.Failed, null, "Bloqué.") == "Bloqué.", "Not started, or why it cannot be");
            Assert(TailnetText.State(TailnetHelper.Running, new() { State = TailnetStates.Running, User = "ogsas@example.com" }) == "Connecté : ogsas@example.com.", "Signed in");
            Assert(TailnetText.State(TailnetHelper.Running, new() { State = TailnetStates.Running, Offline = true }).Contains("sans réseau"), "Signed in without a network");
            Assert(TailnetText.State(TailnetHelper.Running, new() { State = TailnetStates.MachineAuth }) == TailnetText.WaitingApproval + ".", "Waiting for the console");
            var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
            TailnetStatus Expiring(TimeSpan left) => new() { State = TailnetStates.Running, Self = new() { HostName = "mira-pc", KeyExpiry = now + left } };
            Assert(TailnetText.Expiry(Expiring(TimeSpan.FromDays(5)), now) is { } soon && soon.Contains("expire le 3 novembre") && soon.Contains("« mira-pc »"), "An expiry in 5 days: " + TailnetText.Expiry(Expiring(TimeSpan.FromDays(5)), now));
            Assert(TailnetText.Expiry(Expiring(TimeSpan.FromDays(30)), now) is null && TailnetText.Expiry(Expiring(TimeSpan.FromDays(-1)), now) is null && TailnetText.Expiry(null, now) is null
                && TailnetText.Expiry(new() { State = TailnetStates.Running, Self = new() { HostName = "mira-pc" } }, now) is null, "An expiry said too early, when past, or for a key that never expires");
            var french = TailnetText.Problem(TailnetCodes.Closed);
            Lang.Use("en", CultureInfo.GetCultureInfo("en-US"));
            Assert(TailnetText.Problem(TailnetCodes.Closed) == "Jellyfin’s PC answers through Tailscale, but Jellyfin isn’t running on it." && french != TailnetText.Problem(TailnetCodes.Closed), "Not in English");
            Assert(TailnetText.Name == "Built-in Tailscale" && TailnetText.SignIn == "Sign in to Tailscale" && TailnetText.ThroughNode == "Through Built-in Tailscale", "The words shared with the iPhone app");
            return Task.CompletedTask;
        });

        await run.Test("Tailscale intégré : sans clé d’état lisible, rien ne démarre et Mira réessaie", async () =>
        {
            await using var helper = new TailnetProcess(Options(Path.Combine(run.Root, "tailnet-key")) with { StateKey = () => null });
            try { await helper.StartAsync(); throw new Exception("The helper started without a state key"); }
            catch (TailnetException ex) when (ex.Code == TailnetCodes.Key) { }
            Assert(helper.Helper == TailnetHelper.Restarting && helper.NextAttempt is not null, "An unreadable key is not tried again later");
            Assert(TailnetProcess.Redact("visit https://login.tailscale.com/a/1a2b3c and http://127.0.0.1:9911/auth/x_y-z then") == "visit <login page> and <login page> then", "A login page is not hidden");
        });
    }
}
