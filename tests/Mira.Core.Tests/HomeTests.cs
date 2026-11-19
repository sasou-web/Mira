using System.IO;
using Mira.Jellyfin;
using static TestSupport;

/// <summary>
/// « Le foyer » (HomeFamily, on a clock of its own): the home's password kept by its PBKDF2 only, joining it (wrong
/// password, too many tries, the same device again), the profiles offered and their PIN (five wrong in a row, then a
/// wait), a device removed, a new password putting every device out, all of it read back after a restart of Jellyfin.
/// </summary>
internal static class HomeTests
{
    private const string Sam = "5a05b95f81fb47f1981e0123456789ab", Lina = "90892981f28a0ccb86a6e55a3a0039c6";

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Foyer : le mot de passe, les appareils, les profils et leur code", () =>
        {
            var now = DateTimeOffset.Parse("2026-11-19T20:00:00Z");
            var file = Path.Combine(run.Root, "home", "Mira.home.json");
            // A small count for the tests: the real one is a third of a second a try.
            var home = new HomeFamily(file, () => now, iterations: 1000);
            Assert(!home.On && home.Join("famille-ok", "device-a1", "Salon", "1.2.3.4").Result == HomeFamily.Joining.NoHome, "No home before a password");
            Assert(home.SetPassword("court") is null && home.SetPassword("        ") is null && !home.On, "A password of 8 characters at least");
            Assert(home.SetPassword("famille-ok") is { Count: 0 } && home.On && home.Changed == now, "Home on");
            Assert(!File.ReadAllText(file).Contains("famille-ok", StringComparison.Ordinal), "The password kept by its hash only");

            Assert(home.Join("pas-le-bon", "device-a1", "Salon", "1.2.3.4").Result == HomeFamily.Joining.Wrong, "Wrong password");
            var (result, token, device, replaced) = home.Join("famille-ok", "device-a1", "Salon", "1.2.3.4");
            Assert(result == HomeFamily.Joining.Joined && token is { Length: >= 32 } && device is { Name: "Salon" } && replaced is null, "Joined");
            Assert(home.ByToken(token) is { Name: "Salon" } && home.ByToken("autre") is null && home.ByToken(null) is null, "Its token");
            Assert(!File.ReadAllText(file).Contains(token!, StringComparison.Ordinal), "The token kept by its hash only");
            Assert(HomeFamily.JellyfinDevice(device!) == "mira-home-" + device!.Id && device.Id.Length == 32, "Its Jellyfin device id");

            // The same device again: its new token only, its earlier entry returned (its profile's session ends).
            var again = home.Join("famille-ok", "device-a1", "Salon", "1.2.3.4");
            Assert(again.Replaced?.Id == device.Id && home.ByToken(token) is null && home.ByToken(again.Token) is not null && home.Devices().Count == 1, "Joined again");

            // Profiles: offered, their PIN, wrong ones counted, a wait after five.
            Assert(home.Offer(Sam, true).Done && home.Offer(Lina.ToUpperInvariant(), true).Done && home.Offer("pas-un-id", true) is (false, _), "Offered");
            Assert(home.Profiles().Count == 2 && home.Profile("5a05b95f-81fb-47f1-981e-0123456789ab") is not null, "Ids in either form");
            Assert(home.CheckPin(Sam, null).Result == HomeFamily.Pin.Open, "No PIN: open");
            Assert(!home.SetPin(Sam, "12a4") && !home.SetPin(Sam, "123") && home.SetPin(Sam, "2468") && !home.SetPin("ffffffffffffffffffffffffffffffff", "1111"), "PINs of four digits, on a profile offered");
            Assert(home.CheckPin(Sam, null) is (HomeFamily.Pin.Asked, 5, _) && home.CheckPin(Sam, "0000") is (HomeFamily.Pin.Wrong, 4, _) && home.CheckPin(Sam, "2468").Result == HomeFamily.Pin.Open, "Asked, wrong, right");
            for (var i = 1; i <= 4; i++)
                Assert(home.CheckPin(Sam, "0000") is (HomeFamily.Pin.Wrong, var left, _) && left == 5 - i, "Wrong " + i);
            Assert(home.CheckPin(Sam, "0000") is (HomeFamily.Pin.Wait, 0, var wait) && wait == HomeFamily.PinWait, "The fifth: a wait");
            Assert(home.CheckPin(Sam, "2468").Result == HomeFamily.Pin.Wait && home.CheckPin(Lina, null).Result == HomeFamily.Pin.Open, "Even the right one waits; another profile not");
            now += HomeFamily.PinWait;
            Assert(home.CheckPin(Sam, "0000") is (HomeFamily.Pin.Wrong, 4, _) && home.CheckPin(Sam, "2468").Result == HomeFamily.Pin.Open, "After the wait, five tries again");

            // The profile a device has open; taken out of the home, the device loses it.
            var id = home.Devices().Single().Id;
            Assert(home.Opened(id, Lina) && home.ByToken(again.Token)?.Profile == Lina && !home.Opened("autre", Lina), "Opened");
            Assert(home.Offer(Lina, false) is (true, [{ Profile: Lina }]) && home.ByToken(again.Token)?.Profile is null && home.Profiles().Count == 1, "Taken out");

            // Read back after a restart of Jellyfin.
            var read = new HomeFamily(file, () => now, iterations: 1000);
            Assert(read.On && read.ByToken(again.Token) is { Name: "Salon" } && read.CheckPin(Sam, "2468").Result == HomeFamily.Pin.Open && read.CheckPin(Sam, "1111").Result == HomeFamily.Pin.Wrong, "Read back");

            // Removed, then a new password: every device out.
            var phone = read.Join("famille-ok", "device-b2", "Téléphone", "1.2.3.5");
            Assert(read.Remove(id)?.Id == id && read.ByToken(again.Token) is null && read.Remove(id) is null, "Removed");
            Assert(read.SetPassword("famille-neuf") is [{ Name: "Téléphone" }] && read.ByToken(phone.Token) is null && read.Devices().Count == 0, "A new password: every device out");
            Assert(read.Join("famille-ok", "device-b2", "Téléphone", "1.2.3.5").Result == HomeFamily.Joining.Wrong && read.Join("famille-neuf", "device-b2", "Téléphone", "1.2.3.5").Result == HomeFamily.Joining.Joined, "Only the new one");
            Assert(read.SetPassword(null) is [_] && !read.On && read.Profiles().Count == 1, "Home off: devices out, profiles kept");

            // A damaged file: put aside, the home off (never left open).
            File.WriteAllText(file, "{ pas du json");
            Assert(!new HomeFamily(file, () => now, iterations: 1000).On && Directory.GetFiles(Path.GetDirectoryName(file)!, "*.bad-*").Length == 1, "Damaged file");
            return Task.CompletedTask;
        });

        await Test("Foyer : essais du mot de passe limités par adresse et en tout", () =>
        {
            var now = DateTimeOffset.Parse("2026-11-19T20:00:00Z");
            var home = new HomeFamily(null, () => now, iterations: 1000);
            home.SetPassword("famille-ok");
            for (var i = 0; i < HomeFamily.TriesPerAddress; i++)
                Assert(home.Join("faux-faux", "device-a1", "A", "1.2.3.4").Result == HomeFamily.Joining.Wrong, "Try " + i);
            Assert(home.Join("famille-ok", "device-a1", "A", "1.2.3.4").Result == HomeFamily.Joining.TooMany, "Too many from one address, even the right one");
            Assert(home.Join("famille-ok", "device-b1", "B", "1.2.3.5").Result == HomeFamily.Joining.Joined, "Another address");
            for (var i = 0; i < HomeFamily.TriesInAll; i++)
                home.Join("faux-faux", "device-c1", "C", "10.0.0." + i);
            Assert(home.Join("famille-ok", "device-d1", "D", "10.0.1.1").Result == HomeFamily.Joining.TooMany, "Too many in all");
            now += HomeFamily.TryWindow;
            Assert(home.Join("famille-ok", "device-a1", "A", "1.2.3.4").Result == HomeFamily.Joining.Joined, "Again after the window");
            return Task.CompletedTask;
        });
    }
}
