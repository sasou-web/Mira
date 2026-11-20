using System.Text.Json;
using Mira.Core;
using Mira.Core.Family;
using Mira.Jellyfin;
using static TestSupport;

/// <summary>
/// « Security » of Réglages: an account without a password told from the hash Jellyfin keeps (its API says
/// « HasPassword » for one made without), your own short password, devices not used for months, the order of « Fix all… ».
/// </summary>
internal static class SecurityTests
{
    private const string Salt = "00112233445566778899AABBCCDDEEFF";

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;

        await Test("Sécurité : un compte sans mot de passe se lit dans l’empreinte que garde Jellyfin", () =>
        {
            Assert(AccountPasswords.Open(null) && AccountPasswords.Open(""), "None stored");
            Assert(AccountPasswords.Open($"$PBKDF2-SHA512$iterations=1000${Salt}$874FA6E07A5E35789E1D2861A7784E58E515582D43A98CB3C7B2EFB0360D291CE0FB49722E1E49D17FF56D0DACD6077E05D557CCE0FCADFB20F7DDF23DA3DE48"), "The hash of an empty password");
            Assert(!AccountPasswords.Open($"$PBKDF2-SHA512$iterations=1000${Salt}$4C780202A137DC2B56E722987AFD50614EBFA804154C0086B8A6C5BC3069AC5F29EA3BF09F467A5A98EB840028F881CAD36E30E7DCAEF2388B5EB0864FBD1C51"), "A real password");
            Assert(AccountPasswords.Open($"$PBKDF2$iterations=1000${Salt}$529C87CD9D7D4DC2FF80E58B84CBF9CDBD0748AE"), "The older SHA-1 hash of an empty password");
            foreach (var odd in new[] { "secret", "$SHA1$abc", $"$PBKDF2-SHA512$iterations=x${Salt}$00", $"$PBKDF2-SHA512$iterations=1000$zz$00", $"$PBKDF2-SHA512$iterations=1000${Salt}$" })
                Assert(!AccountPasswords.Open(odd), "A form it does not know is said safe: " + odd);
            return Task.CompletedTask;
        });

        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var you = new Connection("http://192.168.1.42:8096", "aaaa0000aaaa0000aaaa0000aaaa0000", "Alex", "t", "mira-pc") { PasswordLength = 6 };
        SecurityReport Report(int? length = 6, IReadOnlyCollection<string>? open = null) => AccountSecurity.Read(Parse("""
            [{"Name":"Alex","Id":"aaaa0000aaaa0000aaaa0000aaaa0000","HasPassword":true,"Policy":{"IsAdministrator":true}},
             {"Name":"Sam","Id":"bbbb0000-bbbb-0000-bbbb-0000bbbb0000","HasPassword":true,"Policy":{"IsAdministrator":false}},
             {"Name":"Noa","Id":"cccc0000cccc0000cccc0000cccc0000","HasPassword":false,"Policy":{}},
             {"Name":"Robin","Id":"dddd0000dddd0000dddd0000dddd0000","HasPassword":true,"Policy":{}},
             {"Name":"Invité","Id":"eeee0000eeee0000eeee0000eeee0000","HasPassword":true,"Policy":{}},
             {"Name":"Vieux","Id":"ffff0000ffff0000ffff0000ffff0000","HasPassword":true,"Policy":{"IsDisabled":true}}]
            """), Parse("""
            {"Items":[{"Id":"mira-pc","Name":"MAISON-PC","AppName":"Mira for Windows","AppVersion":"0.9.16","LastUserId":"aaaa0000aaaa0000aaaa0000aaaa0000","LastUserName":"Alex","DateLastActivity":"2026-11-20T11:59:00Z"},
             {"Id":"mira-home-0123","Name":"Sam’s iPhone","AppName":"Mira for iPhone","LastUserId":"bbbb0000bbbb0000bbbb0000bbbb0000","LastUserName":"Sam","DateLastActivity":"2026-11-20T11:00:00Z"},
             {"Id":"chrome-1","Name":"Chrome","AppName":"Jellyfin Web","AppVersion":"12.4","LastUserName":"Alex","DateLastActivity":"2026-07-14T10:00:00Z"},
             {"Id":"mira-friend-77","Name":"Robin’s iPhone","AppName":"Mira for iPhone","LastUserId":"dddd0000dddd0000dddd0000dddd0000","DateLastActivity":"2026-11-18T10:00:00Z"},
             {"Id":"guest-tv","Name":"Télé","LastUserId":"eeee0000eeee0000eeee0000eeee0000","DateLastActivity":"2026-11-20T11:00:00Z"}]}
            """), Parse("""[{"DeviceId":"mira-home-0123","LastActivityDate":"2026-11-20T11:58:00Z","NowPlayingItem":{"Name":"Film"}}]"""),
            open ?? ["bbbb0000bbbb0000bbbb0000bbbb0000", "cccc0000cccc0000cccc0000cccc0000"], you with { PasswordLength = length },
            ["bbbb0000bbbb0000bbbb0000bbbb0000"], ["dddd0000dddd0000dddd0000dddd0000"], ["eeee0000eeee0000eeee0000eeee0000"], now);

        await Test("Sécurité : les comptes, du plus urgent au plus sûr, sans les invités d’une party", () =>
        {
            var report = Report();
            Assert(string.Join(",", report.Accounts.Select(x => x.Name)) == "Noa,Sam,Alex,Robin,Vieux", "Order: " + string.Join(",", report.Accounts.Select(x => x.Name)));
            var sam = report.Accounts.Single(x => x.Name == "Sam");
            Assert(sam is { NoPassword: true, Way: AccountWay.Home, Weak: true } && report.Accounts.Single(x => x.Name == "Robin").Way == AccountWay.Friend, "Sam in the home without a password, Robin a friend");
            Assert(report.Accounts.Single(x => x.Name == "Alex") is { You: true, Short: true, Length: 6 }, "Your short password");
            Assert(!Report(length: 12).Accounts.Single(x => x.You).Short && !Report(length: null).Accounts.Single(x => x.You).Short, "Long, or unknown: not short");
            Assert(!report.Accounts.Single(x => x.Name == "Vieux").Weak, "A disabled account is nothing to fix");
            // Without the extension's word: Jellyfin's own (false for Noa only).
            var older = AccountSecurity.Read(Parse("""[{"Name":"Noa","Id":"cccc0000cccc0000cccc0000cccc0000","HasPassword":false},{"Name":"Sam","Id":"bbbb0000bbbb0000bbbb0000bbbb0000","HasPassword":true}]"""),
                Parse("{}"), Parse("[]"), null, you, [], [], [], now);
            Assert(!older.Known && older.Accounts.Single(x => x.Name == "Noa").NoPassword && !older.Accounts.Single(x => x.Name == "Sam").NoPassword, "Jellyfin's word without the extension");
            return Task.CompletedTask;
        });

        await Test("Sécurité : les appareils, celui-ci d’abord, ceux qui ne servent plus depuis trois mois", () =>
        {
            var report = Report();
            Assert(string.Join(",", report.Devices.Select(x => x.Name)) == "MAISON-PC,Sam’s iPhone,Robin’s iPhone,Chrome", "Order: " + string.Join(",", report.Devices.Select(x => x.Name)));
            var phone = report.Devices[1];
            Assert(report.Devices[0].This && phone is { Watching: true, Way: DeviceWay.Home, WayId: "0123" } && report.Devices[2] is { Way: DeviceWay.Friend, WayId: "77" }, "This PC, the home's phone watching, the friend's");
            Assert(report.Devices[3].Stale(now) && !report.Devices[2].Stale(now) && !report.Devices[0].Stale(now.AddYears(1)), "Chrome not used since July");
            Assert(SecurityText.Seen(report.Devices[3], now).Contains('4'), "Four months: " + SecurityText.Seen(report.Devices[3], now));
            return Task.CompletedTask;
        });

        await Test("Sécurité : « Tout régler… » demande les comptes, puis ton mot de passe, puis les appareils ; « Passer » se tait 30 jours", () =>
        {
            var report = Report();
            var issues = AccountSecurity.Issues(report);
            Assert(string.Join(",", issues.Select(x => x.Account?.Name ?? x.Device!.Name)) == "Noa,Sam,Alex,Chrome", "Steps: " + string.Join(",", issues.Select(x => x.Key)));
            var marks = new Dictionary<string, DateTimeOffset> { [AccountSecurity.AccountKey("cccc0000cccc0000cccc0000cccc0000")] = now.AddDays(-29), [AccountSecurity.DeviceKey("chrome-1")] = now.AddDays(-31) };
            var quiet = AccountSecurity.Issues(report, key => AccountSecurity.Quiet(marks, key, now));
            Assert(string.Join(",", quiet.Select(x => x.Account?.Name ?? x.Device!.Name)) == "Sam,Alex,Chrome", "Noa skipped yesterday, Chrome a month ago");
            AccountSecurity.Forget(marks, now);
            Assert(marks.Count == 1, "The old mark dropped");
            Assert(AccountSecurity.Issues(Report(length: 20, open: [])).Count == 1, "Only Chrome left");
            Assert(AccountSecurity.LockPassword() is { Length: 32 } a && a != AccountSecurity.LockPassword(), "A long password drawn each time");
            return Task.CompletedTask;
        });

        await Test("Sécurité : le bandeau et l’alerte de « L’essentiel » en une phrase", () =>
        {
            var issues = AccountSecurity.Issues(Report());
            Assert(SecurityText.Summary(issues) == "2 comptes n’ont pas de mot de passe, ton propre mot de passe est court et 1 appareil ne sert plus depuis des mois.", SecurityText.Summary(issues));
            Assert(SecurityText.AlertLine(issues) == "Noa et Sam n’ont pas de mot de passe · ton mot de passe est court · 1 appareil inutilisé depuis des mois", SecurityText.AlertLine(issues));
            Assert(SecurityText.Headline(issues.Count) == "4 choses à régler" && SecurityText.Headline(0) == "Tout va bien", "Headline");
            return Task.CompletedTask;
        });
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
