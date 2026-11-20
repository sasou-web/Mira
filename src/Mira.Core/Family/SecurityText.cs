namespace Mira.Core.Family;

/// <summary>The words of « Security », the same on Windows and Mac.</summary>
public static class SecurityText
{
    public static string Title => L("Sécurité", "Security");
    public static string About => L("Qui peut entrer sur ton serveur, et depuis quels appareils.", "Who can get into your server, and from which devices.");
    public static string Accounts => L("Comptes", "Accounts");
    public static string Devices => L("Appareils connectés", "Connected devices");
    public static string FixAll => L("Tout régler…", "Fix all…");
    public static string CheckAgain => L("Vérifier à nouveau", "Check again");
    public static string Review => L("Voir", "Review");
    public static string Reading => L("Lecture des comptes et des appareils…", "Reading the accounts and devices…");
    public static string SignOutNote => L("Déconnecter un appareil ne supprime rien : il redemande simplement le mot de passe, ou celui du foyer, sur cet appareil.",
        "Signing a device out deletes nothing: it just asks for the password, or the home password, again on that device.");
    public static string Unknown => L("L’extension Mira de ce Jellyfin est trop ancienne pour dire quels comptes n’ont pas de mot de passe : mets-la à jour dans « Serveur Jellyfin ».",
        "This Jellyfin’s Mira extension is too old to tell which accounts have no password: update it in « Jellyfin server ».");
    public static string NotAdmin => L("Seul un administrateur de Jellyfin voit les comptes et les appareils.", "Only a Jellyfin administrator sees the accounts and devices.");

    // ---- The summary ----

    /// <summary>The banner's title: how many things to fix, or that all is well.</summary>
    public static string Headline(int issues) => issues == 0 ? L("Tout va bien", "Everything looks good")
        : Count(issues, "chose à régler", "choses à régler", "thing to fix", "things to fix");

    /// <summary>« 2 accounts have no password, and your own password is short. »</summary>
    public static string Summary(IReadOnlyList<SecurityIssue> issues)
    {
        var parts = new List<string>();
        var internet = issues.Any(x => x.Server == ServerOpening.Internet); var wifi = issues.Any(x => x.Server == ServerOpening.PublicWifi);
        if (internet || wifi) parts.Add(internet && wifi ? L("Jellyfin est ouvert à Internet et aux Wi-Fi publics", "Jellyfin is open to the Internet and to public Wi-Fi")
            : internet ? L("Jellyfin est ouvert à tout Internet", "Jellyfin is open to the whole Internet") : L("Jellyfin est ouvert aux Wi-Fi publics", "Jellyfin is open to public Wi-Fi"));
        var open = issues.Count(x => x.Account is { NoPassword: true, You: false });
        if (open > 0) parts.Add(open == 1 ? L("1 compte n’a pas de mot de passe", "1 account has no password") : L($"{open} comptes n’ont pas de mot de passe", $"{open} accounts have no password"));
        if (issues.Any(x => x.Account is { NoPassword: true, You: true })) parts.Add(L("ton propre compte n’a pas de mot de passe", "your own account has no password"));
        if (issues.Any(x => x.Account is { Short: true })) parts.Add(L("ton propre mot de passe est court", "your own password is short"));
        var stale = issues.Count(x => x.Device is not null);
        if (stale > 0) parts.Add(stale == 1 ? L("1 appareil ne sert plus depuis des mois", "1 device unused for months") : L($"{stale} appareils ne servent plus depuis des mois", $"{stale} devices unused for months"));
        return parts.Count == 0 ? "" : Capital(Join(parts)) + ".";
    }

    /// <summary>« Every account has a password · 5 devices connected · checked at 2:14 »</summary>
    public static string Good(SecurityReport report) =>
        L("Chaque compte a un mot de passe", "Every account has a password") + " · " + Count(report.Devices.Count, "appareil connecté", "appareils connectés", "device connected", "devices connected")
        + " · " + L($"vérifié à {Time(report.Checked.ToLocalTime())}", $"checked at {Time(report.Checked.ToLocalTime())}");

    /// <summary>« Essentials »: the alert's title and its line (« Sam and Noa have no password · your own password is short »).</summary>
    public static string Alert(int issues) => Count(issues, "chose à régler", "choses à régler", "thing to fix", "things to fix") + L(" pour garder ton serveur sûr", " to keep your server safe");
    public static string AlertLine(IReadOnlyList<SecurityIssue> issues)
    {
        var parts = new List<string>();
        if (issues.Any(x => x.Server == ServerOpening.Internet)) parts.Add(L("Jellyfin est ouvert à tout Internet", "Jellyfin is open to the whole Internet"));
        if (issues.Any(x => x.Server == ServerOpening.PublicWifi)) parts.Add(L("Jellyfin est ouvert aux Wi-Fi publics", "Jellyfin is open to public Wi-Fi"));
        var names = issues.Where(x => x.Account is { NoPassword: true, You: false }).Select(x => x.Account!.Name).ToList();
        if (names.Count > 0) parts.Add(Names(names) + (names.Count == 1 ? L(" n’a pas de mot de passe", " has no password") : L(" n’ont pas de mot de passe", " have no password")));
        if (issues.Any(x => x.Account is { NoPassword: true, You: true })) parts.Add(L("ton compte n’a pas de mot de passe", "your account has no password"));
        if (issues.Any(x => x.Account is { Short: true })) parts.Add(L("ton mot de passe est court", "your own password is short"));
        var stale = issues.Count(x => x.Device is not null);
        if (stale > 0) parts.Add(Count(stale, "appareil inutilisé depuis des mois", "appareils inutilisés depuis des mois", "device unused for months", "devices unused for months"));
        return string.Join(" · ", parts);
    }

    // ---- Your server ----

    public static string Server => L("Ton serveur", "Your server");
    public static string TooOpen => L("Trop ouvert", "Too open");
    public static string CloseIt => L("Fermer…", "Close it…");
    public static string CloseItNow => L("Fermer", "Close it");
    public static string KeepOpen => L("Laisser ouvert", "Keep it open");
    public static string InternetTitle(bool open) => open ? L("Ouvert à tout Internet", "Open to the whole Internet") : L("Ta maison et Tailscale seulement", "Your home and Tailscale only");
    public static string InternetLine(bool open) => open
        ? L("Jellyfin laisse entrer n’importe quelle adresse, pas seulement ta maison et Tailscale. Qui le trouve peut essayer des mots de passe.", "Jellyfin lets in any address, not just your home and Tailscale. Anyone who finds it can try passwords.")
        : L("Le reste d’Internet est refusé. Les liens de party marchent toujours.", "The rest of the Internet is turned away. Party links still work.");
    public static string PublicWifiTitle(bool open) => open ? L("Ouvert sur les Wi-Fi publics", "Open on public Wi-Fi") : L("Réseaux privés seulement", "Private networks only");
    public static string PublicWifiLine(bool open) => open
        ? L("Quand ce PC rejoint le Wi-Fi d’un café ou d’un hôtel, les gens qui y sont peuvent joindre Jellyfin.", "When this PC joins a café or hotel Wi-Fi, people on it can reach Jellyfin.")
        : L("Les Wi-Fi publics ne peuvent pas joindre Jellyfin.", "Public Wi-Fi can’t reach Jellyfin.");

    public static string CloseInternetTitle => L("Fermer Jellyfin à Internet ?", "Close Jellyfin to the Internet?");
    public static string CloseInternetText => L("Tes appareils à la maison et sur Tailscale continuent de marcher, les liens de party et d’invitation aussi. Tout le reste est refusé.",
        "Your devices at home and on Tailscale keep working, and so do party and invite links. Everything else is turned away.");
    public static string WouldCut(int count) => count == 1 ? L("1 appareil s’est connecté de l’extérieur ces derniers temps et serait coupé :", "1 device connected from outside lately and would be cut:")
        : L($"{count} appareils se sont connectés de l’extérieur ces derniers temps et seraient coupés :", $"{count} devices connected from outside lately and would be cut:");
    public static string CutHint(int count) => count == 1 ? L("Installe Mira ou Tailscale dessus pour continuer à regarder depuis là-bas.", "Install Mira or Tailscale on it to keep watching from there.")
        : L("Installe Mira ou Tailscale dessus pour continuer à regarder depuis là-bas.", "Install Mira or Tailscale on them to keep watching from there.");
    /// <summary>« Jellyfin Android TV · Sam »: what it is and who used it, else its address.</summary>
    public static string OutsideLine(OutsideDevice device) =>
        string.Join(" · ", new[] { device.App, device.UserName }.Where(x => x is { Length: > 0 })) is { Length: > 0 } who ? who : device.Address;
    public static string FromOutside => L("de l’extérieur", "from outside");
    public static string FromHomeTitle => L("Fais-le depuis la maison", "Do this from home");
    public static string FromHomeText => L("Cet appareil joint Jellyfin depuis l’extérieur en ce moment : fermer d’ici le couperait aussi. Fais-le à la maison, sur le PC de Jellyfin, ou passe d’abord par Tailscale.",
        "This device is reaching Jellyfin from outside right now: closing it here would cut it too. Do it at home, on the PC where Jellyfin runs, or connect through Tailscale first.");
    public static string ClosePublicTitle => L("Fermer Jellyfin aux Wi-Fi publics ?", "Close Jellyfin to public Wi-Fi?");
    public static string ClosePublicText => L("La maison et Tailscale restent ouverts. Windows demande son autorisation, une fois.",
        "Your home and Tailscale stay open. Windows asks for permission, once.");
    public static string ClosePublicFailed => L("Le pare-feu n’a pas changé. Une règle de Jellyfin vient peut-être d’une suite de sécurité : regarde dans ses réglages.",
        "The firewall didn’t change. A Jellyfin rule may come from a security suite: look in its settings.");
    public static string Refused => L("Windows n’a pas eu l’autorisation : rien n’a changé.", "Windows didn’t get permission: nothing changed.");
    public static string InternetStepTitle => L("Jellyfin répond à tout Internet", "Jellyfin answers the whole Internet");
    public static string InternetStepText => L("Qui trouve ton serveur peut y essayer des mots de passe. Mira n’a besoin que de ta maison et de Tailscale, et les liens de party passent aussi par Tailscale.",
        "Anyone who finds your server can try passwords on it. Mira only needs your home and Tailscale, and party links go through Tailscale too.");

    // ---- An account ----

    public static string You => L("Toi", "You");
    public static string Friend => L("Ami", "Friend");
    public static string Profile => L("Profil", "Profile");
    public static string Admin => L("Administrateur", "Administrator");
    public static string NoPassword => L("Sans mot de passe", "No password");
    public static string ShortPassword => L("Mot de passe court", "Short password");
    public static string Protected => L("Protégé", "Protected");
    public static string Disabled => L("Désactivé", "Disabled");
    public static string Protect => L("Protéger", "Protect");
    public static string Change => L("Changer…", "Change…");
    public static string ChooseOwn => L("Choisir un mot de passe…", "Choose a password…");

    /// <summary>What the line under an account's name says.</summary>
    public static string Line(SecurityAccount account)
    {
        if (account.Disabled) return L("Ne peut pas se connecter", "Can’t sign in");
        if (account.Short)
            return L($"Administrateur · ton mot de passe a {account.Length} caractères. Un administrateur peut tout changer : mets-en {AccountSecurity.MinLength} ou plus.",
                $"Administrator · your password has {account.Length} characters. An administrator can change everything: use {AccountSecurity.MinLength} or more.");
        if (account.NoPassword)
            return account.You ? L("Quiconque joint ton Jellyfin peut ouvrir ton compte d’administrateur sans rien taper.", "Anyone who reaches your Jellyfin can open your administrator account without typing anything.")
                : L("Quiconque joint ton Jellyfin peut ouvrir ce compte sans rien taper.", "Anyone who reaches your Jellyfin can open this account without typing anything.");
        var line = (account.Admin ? Admin + " · " : "") + L("A un mot de passe", "Has a password");
        return line + account.Way switch
        {
            AccountWay.Home => L(" · entre avec le mot de passe du foyer", " · gets in with the home password"),
            AccountWay.Friend => L(" · entre par son lien d’invitation", " · gets in by their invite link"),
            _ => "",
        };
    }

    // ---- « Protect » ----

    public static string ProtectTitle(string name) => L($"Protéger le compte de {name}", $"Protect {name}’s account");
    public static string ProtectNote => L("Aujourd’hui, quiconque joint ton Jellyfin peut l’ouvrir sans mot de passe.", "Today anyone who reaches your Jellyfin can open it without a password.");
    public static string LockIt => L("Laisser Mira le verrouiller", "Let Mira lock it");
    public static string LockItShort => L("Verrouiller", "Lock it");
    public static string Recommended => L("Conseillé", "Recommended");
    public static string LockAbout(SecurityAccount account) => account.Way switch
    {
        AccountWay.Home => L($"Mira met un long mot de passe que personne n’a à retenir. {account.Name} continue d’entrer avec le mot de passe du foyer, puis choisit son profil.",
            $"Mira sets a long password nobody has to remember. {account.Name} keeps getting in with the home password, then picks their profile."),
        AccountWay.Friend => L($"Mira met un long mot de passe que personne n’a à retenir. Les appareils de {account.Name} se déconnectent : envoie-lui un nouveau lien d’invitation.",
            $"Mira sets a long password nobody has to remember. {account.Name}’s devices are signed out: send them a new invite link."),
        _ => L($"Mira met un long mot de passe que personne n’a à retenir. {account.Name} n’entrera plus qu’avec un code depuis un appareil déjà connecté, ou par le foyer si tu l’y ajoutes.",
            $"Mira sets a long password nobody has to remember. {account.Name} then only gets in with a code from a device already signed in, or through the home if you add them."),
    };
    public static string Choose => L("Choisir un mot de passe", "Choose a password");
    public static string ChooseInstead => L("Choisir un mot de passe plutôt", "Choose a password instead");
    public static string ChooseAbout(string name) => L($"Si {name} se connecte aussi ailleurs, comme l’app Jellyfin d’une télé. Tu le lui donnes.",
        $"If {name} also signs in elsewhere, like the Jellyfin app on a TV. You give it to them.");
    public static string SignedOutToo(string name) => L($"Les appareils déjà connectés au compte de {name} devront se reconnecter.", $"Devices already signed in to {name}’s account will need to sign in again.");
    public static string NewPassword => L("Nouveau mot de passe", "New password");
    public static string CurrentPassword => L("Mot de passe actuel", "Current password");
    public static string Show => L("Afficher", "Show");
    public static string Locked(string name) => L($"Le compte de {name} est protégé.", $"{name}’s account is protected.");

    /// <summary>Under a password being typed: its length, and whether it is long enough.</summary>
    public static string Strength(int length) => length == 0 ? L($"Au moins {AccountSecurity.MinLength} caractères ; trois mots, c’est bien.", $"At least {AccountSecurity.MinLength} characters; three words work well.")
        : length < AccountSecurity.MinLength ? L($"{length} caractères · encore {AccountSecurity.MinLength - length}", $"{length} characters · {AccountSecurity.MinLength - length} more")
        : L($"{length} caractères · bien", $"{length} characters · good");
    public static string TooShort => L($"Au moins {AccountSecurity.MinLength} caractères.", $"At least {AccountSecurity.MinLength} characters.");
    public static string WrongCurrent => L("Ce n’est pas ton mot de passe actuel.", "That’s not your current password.");

    // ---- Your own password ----

    public static string ChangeTitle => L("Changer ton mot de passe", "Change your password");
    public static string ChangeNote => L("Tes autres appareils devront se reconnecter avec le nouveau ; celui-ci reste connecté.", "Your other devices will need to sign in with the new one; this one stays signed in.");
    public static string ChooseTitle => L("Choisir ton mot de passe", "Choose your password");
    public static string ChangeButton => L("Changer le mot de passe", "Change password");
    public static string Changed => L("Ton mot de passe est changé.", "Your password is changed.");

    // ---- Devices ----

    public static string ThisDevice(bool mac) => mac ? L("Ce Mac", "This Mac") : L("Ce PC", "This PC");
    public static string SignOut => L("Déconnecter", "Sign out");
    public static string SignOutAll => L("Déconnecter tous les autres…", "Sign out all others…");
    public static string InTheHome => L("dans le foyer", "in the home");
    public static string FriendDevice => L("ami", "friend");

    /// <summary>« Mira for Windows 0.9.16 · Alex · in the home »</summary>
    public static string DeviceLine(SecurityDevice device)
    {
        var app = (device.App + (device.Version is { Length: > 0 } version ? " " + version : "")).Trim();
        var parts = new List<string>();
        if (app.Length > 0) parts.Add(app);
        if (device.UserName is { Length: > 0 } user) parts.Add(user);
        if (device.Way == DeviceWay.Home) parts.Add(InTheHome);
        if (device.Way == DeviceWay.Friend) parts.Add(FriendDevice);
        return string.Join(" · ", parts);
    }

    /// <summary>When it was last used: « Active now », « Watching now », « Yesterday », « 3 days ago », « Not used for 4 months ».</summary>
    public static string Seen(SecurityDevice device, DateTimeOffset now)
    {
        if (device.Watching) return L("Regarde en ce moment", "Watching now");
        if (device.Active) return L("Actif en ce moment", "Active now");
        if (device.LastActive is not { } last) return L("Jamais vu", "Never seen");
        var days = (int)Math.Floor((now.ToLocalTime().Date - last.ToLocalTime().Date).TotalDays);
        if (device.Stale(now)) return L($"Inutilisé depuis {Months(now - last)} mois", $"Not used for {Months(now - last)} months");
        return days switch
        {
            <= 0 => L($"Aujourd’hui à {Time(last.ToLocalTime())}", $"Today at {Time(last.ToLocalTime())}"),
            1 => L("Hier", "Yesterday"),
            < 30 => L($"Il y a {days} jours", $"{days} days ago"),
            _ => L($"Il y a {days / 30} mois", days / 30 == 1 ? "1 month ago" : $"{days / 30} months ago"),
        };
    }
    private static int Months(TimeSpan span) => Math.Max(3, (int)(span.TotalDays / 30));

    public static string SignOutTitle(SecurityDevice device) => L($"Déconnecter « {device.Name} » ?", $"Sign out “{device.Name}”?");
    public static string SignOutText(SecurityDevice device) => device.Way switch
    {
        DeviceWay.Home => L("Il sort aussi du foyer : il faudra y retaper le mot de passe du foyer. Rien n’est supprimé.", "It also leaves the home: the home password will be asked there again. Nothing is deleted."),
        DeviceWay.Friend => L("Son lien avec ton serveur est coupé : il faudra un nouveau lien d’invitation. Rien n’est supprimé.", "Its line to your server is cut: it will need a new invite link. Nothing is deleted."),
        _ => L("Il redemandera le mot de passe sur cet appareil. Rien n’est supprimé.", "It will ask for the password again on that device. Nothing is deleted."),
    };
    public static string SignOutAllTitle(int count) => L("Déconnecter ", "Sign out ") + Count(count, "autre appareil", "autres appareils", "other device", "other devices") + L(" ?", "?");
    public static string SignOutAllText(bool mac) => L($"Chacun redemandera son mot de passe, ou celui du foyer. {(mac ? "Ce Mac" : "Ce PC")} reste connecté. Rien n’est supprimé.",
        $"Each one will ask for its password, or the home password, again. {(mac ? "This Mac" : "This PC")} stays signed in. Nothing is deleted.");
    public static string SignedOut(string name) => L($"« {name} » est déconnecté.", $"“{name}” is signed out.");

    // ---- « Fix all… », one question at a time ----

    public static string Step(int number, int count) => L($"VÉRIFICATION · {number} SUR {count}", $"SECURITY CHECK · {number} OF {count}");
    public static string Skip => L("Passer", "Skip");
    public static string Keep => L("Oui, je le garde", "Yes, keep it");
    public static string SignItOut => L("Le déconnecter", "Sign it out");
    public static string NoPasswordTitle(SecurityAccount account) => account.You ? L("Ton compte n’a pas de mot de passe", "Your account has no password")
        : L($"Le compte de {account.Name} n’a pas de mot de passe", $"{account.Name}’s account has no password");
    public static string ShortTitle => L("Ton mot de passe est court", "Your password is short");
    public static string ShortText(int length) => L($"Il a {length} caractères, et ton compte peut tout changer sur le serveur. Mets-en {AccountSecurity.MinLength} ou plus : trois mots, c’est bien.",
        $"It has {length} characters, and your account can change everything on the server. Use {AccountSecurity.MinLength} or more: three words work well.");
    public static string StaleTitle => L("Tu utilises encore cet appareil ?", "Do you still use this device?");
    public static string StaleText(SecurityDevice device, DateTimeOffset now) => L($"Il est encore connecté à ton serveur, mais personne ne s’en est servi depuis {Months(now - (device.LastActive ?? now))} mois.",
        $"It is still signed in to your server, but nobody has used it for {Months(now - (device.LastActive ?? now))} months.");
    public static string SkipNote => L("« Passer » le laisse tel quel et le redemande dans 30 jours.", "« Skip » leaves it as it is and asks again in 30 days.");
    public static string Done => L("C’est tout pour aujourd’hui.", "That’s all for today.");

    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + L(" et ", ", and ") + parts[^1],
    };
    private static string Names(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => names[0] + L(" et ", " and ") + names[1],
        _ => string.Join(", ", names.Take(names.Count - 1)) + L(" et ", " and ") + names[^1],
    };
    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Lang.Culture) + text[1..];
}
