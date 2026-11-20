using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mira.Core;
using Mira.Core.Family;
using Mira.Core.Tailscale;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Le foyer » on the Mac, as on Windows (MainWindow.Family.cs there; ogsas, 2026-11-19: maquette A « Le hall », with the
/// quick switch of C): a Mac joins a home with the home's password, found on this network or opened from a home link
/// (mira://home#…, whose key brings the home's own Tailscale node in, <see cref="HomeNode"/>), then « Who's watching? »
/// at each opening of Mira, a profile's PIN checked by the extension, and the profiles again from the rail's picture
/// (⌘P). Each profile is a real Jellyfin session of its account, opened by the extension (/Mira/home). Never an
/// administrator's account.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>This Mac in a home: read at the start, saved as it changes; null in none.</summary>
    private HomeMembership? _home;
    private HomeNode? _homeNode;
    /// <summary>What the join card is joining: the link opened, or the server found on this network.</summary>
    private HomeLink? _homeLink;
    private (string Server, string Name, string? ServerId)? _homeFound;
    /// <summary>The server found on this network has a home, and the person chose their own account instead: not offered again.</summary>
    private bool _homeDeclined;
    private bool _familyBusy;
    private HomeProfile? _pinProfile;
    /// <summary>The PIN asked from the rail's menu, over the library: « Back » goes back to it, not to the hall.</summary>
    private bool _pinFromMenu;
    private FamilyView? _family;
    /// <summary>A profile picture by « user id/tag », read once per opening of Mira.</summary>
    private readonly Dictionary<string, Bitmap?> _familyFaces = [];
    /// <summary>The device id the extension gives the sessions of a home's profiles (HomeController of Mira.Jellyfin).</summary>
    private const string FamilyDevicePrefix = "mira-home-";

    /// <summary>This Mac's home (the self-check).</summary>
    public HomeMembership? Home
    {
        get => _home;
        private set { _home = value; if (_profilesItem is { } item) item.IsEnabled = value is not null; }
    }
    /// <summary>« Changer de profil… » of the Go menu: enabled in a home only.</summary>
    private NativeMenuItem? _profilesItem;
    /// <summary>The home's pages over the window, while they show (the self-check).</summary>
    public FamilyView? Family => _family is { Parent: not null } shown ? shown : null;

    /// <summary>The home's own node of this profile, made at the first need.</summary>
    private HomeNode FamilyNode => _homeNode ??= new HomeNode(new()
    {
        Locate = TailnetExecutable.Locate, Directory = Profile.HomeNodeDirectory, StateKey = Profile.HomeNodeKey, Forget = Profile.ForgetHomeNode,
        AppVersion = JellyfinClient.AppVersion, ControlUrl = Tailnet.ControlUrl,
    });

    private JellyfinClient HomeClient(string server) => new(new(server, "", "", "", Profile.DeviceId), _handler);

    // —— Opening Mira, and the hall ——

    /// <summary>
    /// At Mira's start, in a home: « Who's watching? », its profiles as last read, then as the home says now. Signed in
    /// last time with an account of its own (« I have my own account »): that account opens, as it would without a
    /// home, and the hall stays in the rail's menu (false: the saved connection opens).
    /// </summary>
    private async Task<bool> OpenHomeAtStartAsync()
    {
        HomeMembership? home;
        // The keychain may ask the person after an update: never on the UI thread.
        try { home = await Task.Run(Profile.LoadHome); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { home = null; }
        if (home is null) return false;
        Home = home;
        if (await Task.Run(Profile.LoadConnection) is { } own && !own.DeviceId.StartsWith(FamilyDevicePrefix, StringComparison.Ordinal))
        {
            // Its account on the home's Jellyfin, away from home: reached through the home's node, as a profile would be.
            if (own.Server == home.Server) await ReachHomeAsync(home, null);
            return false;
        }
        ShowHall();
        _ = RefreshHallAsync();
        return true;
    }

    /// <summary>The home's pages over the window (the rail hidden), above the sign-in or the library, under the player.</summary>
    private FamilyView RevealFamily()
    {
        Reopen();
        if (_family is null)
        {
            var view = _family = new FamilyView(this);
            view.Chosen += profile => _ = ChooseProfileAsync(profile);
            view.PinTyped += pin => { if (_pinProfile is { } profile) _ = OpenProfileAsync(profile, pin); };
            view.Join += () => _ = JoinAsync();
            view.OwnAccount += OwnAccount;
            view.Leave += () => _ = LeaveHomeAsync();
            view.Retry += () => _ = RefreshHallAsync();
            view.PinBack += PinBack;
            view.Escape += FamilyEscape;
            view.FriendStart += () => _ = FriendStartAsync();
            view.FriendNotNow += () => _ = LeaveFriendPageAsync();
            view.FriendAddress += () => _ = FriendAddressAsync();
        }
        if (!_overlay.Children.Contains(_family))
        {
            var at = Player is { } player && _overlay.Children.IndexOf(player) is >= 0 and var index ? index : _overlay.Children.Count;
            _overlay.Children.Insert(at, _family);
        }
        _rail.IsVisible = false;
        return _family;
    }

    /// <summary>The home's pages gone; the library and its rail back when an account is open.</summary>
    private void HideFamily()
    {
        if (_family is { } view)
        {
            _overlay.Children.Remove(view);
            view.Password = ""; view.Busy(null);
        }
        if (Session is not null && Login is null && !InParty) _rail.IsVisible = true;
    }

    private void ShowHall(string? said = null, bool retry = false)
    {
        if (_home is not { } home) return;
        _pinProfile = null;
        RevealFamily().ShowHall(HomeText.HomeOf(home.Name), home.Profiles, home.Current, (profile, size) => ProfileFace(home, profile, size),
            said ?? (home.Profiles.Count == 0 ? HomeText.NoProfiles : null), retry);
    }

    /// <summary>A profile's face: its Jellyfin picture when it has one (read without a session), else its letter.</summary>
    private Control ProfileFace(HomeMembership home, HomeProfile profile, double size)
    {
        var holder = new Panel { Width = size, Height = size };
        if (profile.ImageTag is not { Length: > 0 } tag) { holder.Children.Add(FamilyView.Face(profile, size, null)); return holder; }
        var key = profile.Id + "/" + tag;
        _familyFaces.TryGetValue(key, out var known);
        holder.Children.Add(FamilyView.Face(profile, size, known));
        if (!_familyFaces.ContainsKey(key)) _ = LoadAsync();
        return holder;

        async Task LoadAsync()
        {
            byte[]? bytes;
            using (var client = HomeClient(home.Server)) bytes = await client.ProfilePictureAsync(profile.Id, tag, 320);
            Bitmap? picture = null;
            if (bytes is { Length: > 0 })
                try { using var stream = new MemoryStream(bytes); picture = new Bitmap(stream); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException) { picture = null; }
            _familyFaces[key] = picture;
            if (picture is null) return;
            holder.Children.Clear(); holder.Children.Add(FamilyView.Face(profile, size, picture));
        }
    }

    /// <summary>The home's profiles as it says now; the hall says why not when it cannot (the home's node, the network, out of the home).</summary>
    private async Task RefreshHallAsync()
    {
        if (_home is not { } home) return;
        _family?.HallSays(_family.HallStatus, retry: false);
        if (!await ReachHomeAsync(home, null)) return;
        using var client = HomeClient(home.Server);
        try
        {
            var now = await client.HomeProfilesAsync(home.Token);
            if (!ReferenceEquals(_home, home)) return;
            Home = home with { Profiles = now.Profiles, Current = now.Current, DeviceId = now.DeviceId };
            await SaveHomeAsync();
            if (_family is { Shown: FamilyView.Stage.Hall, Parent: not null }) ShowHall();
        }
        catch (HomeRefusedException ex) when (ex.Kind is HomeRefusal.OutOfHome) { AskHomePasswordAgain(home, ex.Message); }
        catch (HomeRefusedException ex) when (ex.Kind is HomeRefusal.NoHome) { HallSays(ex.Message, retry: false); }
        catch (Exception ex) when (Errors.Expected(ex)) { HallSays(HomeText.Unreachable(home.Name), retry: true); }
    }

    private void HallSays(string text, bool retry)
    {
        if (_family is { Shown: FamilyView.Stage.Hall, Parent: not null } view) view.HallSays(text, retry);
    }

    /// <summary>
    /// The home's Jellyfin reachable: through the home's node when it was joined from a link (its key on the first time,
    /// then its saved state). False, said on the page shown, when the node cannot take this Mac there.
    /// </summary>
    private async Task<bool> ReachHomeAsync(HomeMembership home, HomeLink? link)
    {
        if (!home.ThroughTailnet && link is null) return true;
        if (!Uri.TryCreate(home.Server, UriKind.Absolute, out var address)) return false;
        if (FamilyNode.Node is null) Busy(HomeText.Reaching(home.Name));
        string? code;
        try { code = await FamilyNode.StartAsync(address.Host, link?.Key); }
        finally { Busy(null); }
        if (code is null) return true;
        // Without a key, signed out means the home's administrator took this device off their network.
        var said = code == TailnetCodes.NeedsLogin && link is null ? HomeText.Removed(home.Name) : HomeText.Node(code, home.Name);
        if (_family is { Shown: FamilyView.Stage.Join, Parent: not null } view) view.JoinError(said);
        else if (_family is { Parent: not null }) { ShowHall(said, retry: code != TailnetCodes.NeedsLogin); }
        else Notice(said, tone: NoticeTone.Error);
        return false;
    }

    private async Task SaveHomeAsync()
    {
        if (_home is not { } home) return;
        try { await Task.Run(() => Profile.SaveHome(home)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AddNotice(L("Mira n’a pas pu enregistrer le foyer sur ce Mac : disque plein ou dossier sans droits d’écriture.", "Mira couldn’t save the home on this Mac: the disk is full or the folder can’t be written to.")); }
    }

    private void Busy(string? text) => _family?.Busy(text);

    // —— A profile chosen ——

    /// <summary>
    /// A tile, or a profile of the rail's menu: the profile open on this Mac goes on with its session; another one asks
    /// for its PIN when it has one, then the extension opens its session.
    /// </summary>
    private async Task ChooseProfileAsync(HomeProfile profile)
    {
        if (_familyBusy || _home is not { } home) return;
        if (profile.Id == home.Current && await Task.Run(Profile.LoadConnection) is { } saved && saved.UserId == profile.Id && saved.Server == home.Server)
        {
            // Away from home, the profile's Jellyfin is only reached through the home's node.
            if (Session is null && !await ReachHomeAsync(home, null)) return;
            HideFamily();
            if (Session?.Connection.UserId != saved.UserId || Session?.Connection.Server != saved.Server) await ActivateAsync(saved, _handler);
            return;
        }
        if (profile.Pin) { _pinFromMenu = Family is null && Session is not null; AskPin(profile); return; }
        await OpenProfileAsync(profile, null);
    }

    private async Task OpenProfileAsync(HomeProfile profile, string? pin)
    {
        if (_home is not { } home || _familyBusy) return;
        _familyBusy = true; _family?.PinError(null);
        Busy(L($"Ouverture du profil {profile.Name}…", $"Opening {profile.Name}’s profile…"));
        try
        {
            if (!await ReachHomeAsync(home, null)) { if (Family is null) { ShowHall(); await RefreshHallAsync(); } return; }
            Connection connection;
            using (var client = HomeClient(home.Server)) connection = await client.HomeOpenAsync(home.Token, profile.Id, pin);
            if (!ReferenceEquals(_home, home)) return;
            Home = home with { Current = profile.Id, DeviceId = connection.DeviceId };
            await SaveHomeAsync();
            var previous = await Task.Run(Profile.LoadConnection);
            await Task.Run(() => Profile.SaveConnection(connection));
            _pinFromMenu = false;
            HideFamily();
            await ActivateAsync(connection, _handler);
            // Signed in with an account of its own before: that session ends on its Jellyfin, nothing here could revoke it later.
            if (previous is not null && !previous.DeviceId.StartsWith(FamilyDevicePrefix, StringComparison.Ordinal) && (previous.Server != connection.Server || previous.UserId != connection.UserId))
                _ = EndReplacedSessionAsync(previous);
        }
        catch (HomeRefusedException ex)
        {
            switch (ex.Kind)
            {
                case HomeRefusal.PinAsked: AskPin(profile); break;
                case HomeRefusal.PinWrong: AskPin(profile, HomeText.PinLeft(ex.Left ?? 0)); break;
                case HomeRefusal.PinWait: AskPin(profile, HomeText.PinWait(ex.Wait ?? 300)); break;
                case HomeRefusal.OutOfHome: AskHomePasswordAgain(home, ex.Message); break;
                default: FamilyFailed(ex.Message); await RefreshHallAsync(); break;
            }
        }
        catch (Exception ex) when (Errors.Expected(ex)) { FamilyFailed(HomeText.Unreachable(home.Name)); }
        finally { _familyBusy = false; Busy(null); }
    }

    /// <summary>The session of this Mac's own account, replaced by a profile's: ended on its Jellyfin. Never throws.</summary>
    private async Task EndReplacedSessionAsync(Connection previous)
    {
        try { using var client = new JellyfinClient(previous, _handler); await client.LogoutAsync(); }
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }

    /// <summary>A failure said where the person is: on the PIN page, the hall, or as a notice from the rail's menu.</summary>
    private void FamilyFailed(string text)
    {
        if (Family is not { } view) { Notice(text, tone: NoticeTone.Error); return; }
        if (view.Shown == FamilyView.Stage.Pin) { view.PinError(text); return; }
        if (view.Shown == FamilyView.Stage.Join) { view.JoinError(text); return; }
        view.HallSays(text, retry: true);
    }

    // —— The PIN ——

    private void AskPin(HomeProfile profile, string? error = null)
    {
        if (_home is not { } home) return;
        _pinProfile = profile;
        RevealFamily().ShowPin(HomeText.PinOf(profile.Name), ProfileFace(home, profile, 96),
            L($"Oublié ? Demande à l’administrateur de {home.Name}.", $"Forgot it? Ask {home.Name}’s administrator."), error);
    }

    private void PinBack()
    {
        _pinProfile = null;
        // Opened from the rail's menu, over the library: back to it.
        if (_pinFromMenu && Session is not null) { _pinFromMenu = false; HideFamily(); return; }
        ShowHall();
    }

    /// <summary>Échap: the PIN goes back, the hall over the library goes away, the join card means « my own account ».</summary>
    private void FamilyEscape()
    {
        if (_family is not { } view || _familyBusy) return;
        switch (view.Shown)
        {
            case FamilyView.Stage.Friend: _ = LeaveFriendPageAsync(); break;
            case FamilyView.Stage.Pin: PinBack(); break;
            case FamilyView.Stage.Hall when Session is not null: HideFamily(); break;
            case FamilyView.Stage.Join: OwnAccount(); break;
        }
    }

    // —— Joining ——

    /// <summary>A home link opened in Mira (mira://home#…, from its page): the home's card, its password asked.</summary>
    private void OpenHomeLink(string text)
    {
        if (HomeLink.Read(text) is not { } link) { Notice(HomeText.LinkBroken, tone: NoticeTone.Error); return; }
        if (link.Friend) { OpenFriendLink(link); return; }
        // Already in this home: its hall.
        if (_home is { } home && link.Is(home.ServerId)) { ShowHall(); _ = RefreshHallAsync(); return; }
        _homeLink = link; _homeFound = null;
        var expired = link.Expired(DateTimeOffset.UtcNow);
        RevealFamily().ShowJoin(HomeText.Join(link.Name), "link", HomeText.FromLink(link.Name), HomeText.FromLinkNote, expired ? HomeText.LinkExpired(link.Name) : null, HomeText.OwnAccount, canJoin: !expired);
    }

    /// <summary>
    /// The sign-in found a server on this network that has a home, for a Mac with no account yet: its password, no address
    /// to type (« Use an address or my own account » goes back to the form).
    /// </summary>
    internal async Task OfferHomeAsync(ServerSearch.Found found)
    {
        if (_homeDeclined || _home is not null || _homeLink is not null || _friendLink is not null || _friendWaiting is not null || Session is not null || InParty || Login is not { } login) return;
        if (await Task.Run(Profile.LoadConnection) is not null) return;
        bool? on;
        using (var client = HomeClient(found.Address))
            try { on = await client.HomeOnAsync(); }
            catch (Exception ex) when (Errors.Expected(ex)) { on = null; }
        if (on != true || Session is not null || !ReferenceEquals(Login, login) || login.Server != found.Address || _homeLink is not null) return;
        _homeFound = (found.Address, found.Name, found.Id);
        RevealFamily().ShowJoin(L("Se connecter", "Sign in"), "wifi", HomeText.HomeOf(found.Name), HomeText.FoundHere, null, L("Une adresse ou mon propre compte", "Use an address or my own account"), canJoin: true);
    }

    /// <summary>Out of the home (removed, its password changed): the password again, where it was.</summary>
    private void AskHomePasswordAgain(HomeMembership home, string said)
    {
        _homeLink = null;
        _homeFound = (home.Server, home.Name, home.ServerId);
        RevealFamily().ShowJoin(HomeText.Join(home.Name), home.ThroughTailnet ? "link" : "wifi", HomeText.HomeOf(home.Name), home.ThroughTailnet ? HomeText.FromLinkNote : HomeText.FoundHere, said, HomeText.OwnAccount, canJoin: true);
    }

    private async Task JoinAsync()
    {
        if (_familyBusy || _family is not { } view) return;
        var password = view.Password;
        if (password.Length == 0) { view.JoinError(L("Tape le mot de passe du foyer.", "Type the home’s password.")); return; }
        var link = _homeLink;
        var (server, name, serverId) = link is not null ? (link.Address, link.Name, link.ServerId) : _homeFound ?? default;
        if (server is null) return;
        if (link?.Expired(DateTimeOffset.UtcNow) == true && _home?.ThroughTailnet != true) { view.JoinError(HomeText.LinkExpired(link.Name)); return; }
        _familyBusy = true; view.SetJoinBusy(true); view.JoinError(null);
        try
        {
            var through = link is not null || _home?.ThroughTailnet == true && _home.Server == server;
            var trial = new HomeMembership { Server = server, Name = name, Token = "", DeviceId = "", ServerId = serverId, ThroughTailnet = through };
            if (through && !await ReachHomeAsync(trial, link)) return;
            using var client = HomeClient(server);
            // The Jellyfin the link names, and no other: its own id first.
            var id = await client.LearnServerIdAsync();
            if (serverId is { Length: > 0 } && !string.Equals(id?.Replace("-", ""), serverId.Replace("-", ""), StringComparison.OrdinalIgnoreCase)) { view.JoinError(HomeText.NotThisServer(name)); return; }
            // A short home link has no name: the server's own.
            var learned = serverId is { Length: > 0 } ? null : await client.ServerNameAsync();
            var joined = await client.HomeJoinAsync(password, Profile.DeviceId, HomeText.DeviceName("Mira", MacName()));
            Home = trial with { Server = client.Connection.Server, ServerId = id ?? serverId, Name = learned ?? name, Token = joined.Token, DeviceId = joined.DeviceId, Profiles = joined.Profiles, Current = null };
            _homeLink = null; _homeFound = null;
            await SaveHomeAsync();
            ShowHall();
        }
        catch (HomeRefusedException ex) { view.JoinError(ex.Message); }
        catch (Exception ex) when (Errors.Expected(ex)) { view.JoinError(HomeText.Unreachable(name)); }
        finally { _familyBusy = false; view.SetJoinBusy(false); }
    }

    /// <summary>This Mac's name, as the home's administrator sees it among the devices (« Mira on … »).</summary>
    private static string MacName()
    {
        var name = Environment.MachineName;
        // macOS adds « .local » to the name the network gives it.
        return name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;
    }

    /// <summary>« I have my own account »: the usual sign-in, the home left aside (a home of this Mac stays).</summary>
    private void OwnAccount()
    {
        _homeDeclined = true; _homeLink = null;
        if (_family is { } view) _overlay.Children.Remove(view);
        if (Session is not null) { _rail.IsVisible = true; OpenAccount(); return; }
        if (Login is null) ShowLogin();
        else Login.FocusServer();
        // The home's Jellyfin, most likely the one of this account: written in, still free to change.
        if (Home is { } home && Login is { } login && login.Server.Trim().Length == 0) login.Server = home.Server;
    }

    // —— Leaving ——

    /// <summary>
    /// « Leave this home »: asked first (coming back takes the password, and a new link away from home); the extension
    /// told, the home's node off the tailnet and its state gone, the profile's session closed here.
    /// </summary>
    private async Task LeaveHomeAsync()
    {
        if (_home is not { } home || _familyBusy) return;
        if (!await ConfirmAsync(HomeText.Leave + " ?", home.ThroughTailnet
                ? L($"Pour revenir, il faudra le mot de passe du foyer et un nouveau lien de {home.Name}.", $"To come back, you’ll need the home’s password and a new link from {home.Name}.")
                : L("Pour revenir, il faudra le mot de passe du foyer.", "To come back, you’ll need the home’s password."), HomeText.Leave, L("Annuler", "Cancel"))) return;
        _familyBusy = true;
        try
        {
            var told = false;
            var reach = ReachHomeAsync(home, null);
            if (await Task.WhenAny(reach, Task.Delay(TimeSpan.FromSeconds(20))) == reach && reach.Result)
                using (var client = HomeClient(home.Server)) told = await client.HomeLeaveAsync(home.Token);
            if (home.ThroughTailnet) await FamilyNode.LeaveAsync();
            Home = null; _familyFaces.Clear();
            try { await Task.Run(Profile.ClearHome); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (_family is { } view) _overlay.Children.Remove(view);
            // The profile's session was the home's: it ended with the device on the server.
            var homeSession = Session is { } session ? session.Connection.DeviceId.StartsWith(FamilyDevicePrefix, StringComparison.Ordinal)
                : (await Task.Run(Profile.LoadConnection))?.DeviceId.StartsWith(FamilyDevicePrefix, StringComparison.Ordinal) == true;
            if (homeSession)
            {
                await StopPlaybackAsync();
                await CloseSessionAsync(); SetPhoto(null);
                try { await Task.Run(Profile.ClearConnection); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (Session is null) { ShowLogin(); if (Login is { } login) login.Server = home.ThroughTailnet ? "" : home.Server; }
            else _rail.IsVisible = true;
            Notice(told ? HomeText.Left(home.Name) : HomeText.LeftHere(home.Name), tone: told ? NoticeTone.Done : NoticeTone.Info);
        }
        finally { _familyBusy = false; }
    }

    // —— The rail's picture: its menu is built by MainWindow.Away.cs (D « Le menu du profil ») ——

    private ContextMenu? _familyMenu;
    /// <summary>The rail's menu of the home, while it is open (the self-check).</summary>
    internal ContextMenu? FamilyMenu => _familyMenu;

    /// <summary>The rail's avatar: the profile menu (MainWindow.Away.cs); signed out, the account, as always.</summary>
    private void AccountClicked()
    {
        if (!OpenProfileMenu()) OpenAccount();
    }

    /// <summary>⌘P: the profiles of the home, from anywhere the rail can be reached (never while playing full window).</summary>
    internal bool FamilyShortcut()
    {
        if (_home is null || Watching || CurrentSheet is not null || Current is SettingsPage) return false;
        if (_familyMenu is { IsOpen: true } open) { open.Close(); return true; }
        return OpenProfileMenu();
    }

    // —— For the self-check ——

    /// <summary>The join card for a server found on this network, as the sign-in's search offers it.</summary>
    internal Task OfferHomeForCheckAsync(string server, string name, string id) => OfferHomeAsync(new ServerSearch.Found(server, name, id, ServerSearch.Place.Home));
    /// <summary>« Who's watching? » as at Mira's start, the profiles read again.</summary>
    internal void HallForCheck() { ShowHall(); _ = RefreshHallAsync(); }
    /// <summary>A profile of the rail's menu chosen, as a click on it does.</summary>
    internal void ChooseFromMenuForCheck(string name)
    {
        var profile = _home?.Profiles.FirstOrDefault(x => x.Name == name);
        _familyMenu?.Close();
        if (profile is not null) _ = ChooseProfileAsync(profile);
    }
}
