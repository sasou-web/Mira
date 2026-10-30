using Mira.Core;
using Mira.Core.Updates;

namespace Mira.Desktop;

/// <summary>
/// Mira web on Jellyfin follows this Mira: the guide installs it, and after Mira updates itself, the next start moves
/// Jellyfin's repository of Mira web forward to the new version, asks Jellyfin to install it at once, then offers to
/// restart Jellyfin once nobody watches. Jellyfin is only ever pointed at a release that Mira checked against its signed
/// manifest.
/// </summary>
public partial class MainWindow
{
    /// <summary>The repository of Mira web for this Mira, checked on GitHub; an <see cref="UpdateException"/> says plainly why not.</summary>
    private static async Task<string> VerifiedMiraWebAsync()
    {
        var version = ReleaseFeed.ParseVersion(JellyfinClient.AppVersion) ?? throw new UpdateException(L("Cette copie de Mira n’a pas de numéro de version : Mira web ne peut pas être vérifié.", "This copy of Mira has no version number: Mira web can’t be checked."));
        using var github = new UpdateClient(version, UpdateKeys.Trusted);
        return await MiraWebRelease.VerifyAsync(github, version);
    }

    /// <summary>
    /// At startup, where this Jellyfin has Mira's repository (from the guide, or added by hand) and this account manages
    /// its plugins: the repository moves forward to this version's checked one, never back, and nothing is installed or
    /// restarted (<see cref="JellyfinClient.KeepWebAppCurrentAsync"/>). Jellyfin's own update task installs the new
    /// version, under its own rules: a plugin uninstalled, disabled or kept from updating by the administrator stays so.
    /// Silent: not allowed, offline or not published, it is tried again at the next start; once done, not again for this
    /// version and this server. Then the new version brought the same day (<see cref="BringMiraWebNowAsync"/>).
    /// </summary>
    private async Task KeepMiraWebCurrentAsync()
    {
        if (_demo || _client is not { } client || ReleaseFeed.ParseVersion(JellyfinClient.AppVersion) is not { } version) return;
        var done = $"{version.ToString(3)} {client.Connection.Server}";
        try
        {
            if (_settings.MiraWebUpdated != done)
            {
                if (!await client.KeepWebAppCurrentAsync(version, VerifiedMiraWebAsync)) return;
                _settings.MiraWebUpdated = done; _profile.SaveSettings(_settings);
            }
            if (_settings.MiraWebServed != done) await BringMiraWebNowAsync(client, version, done);
        }
        catch (Exception ex) when (ex is UpdateException || IsExpected(ex)) { }
    }

    /// <summary>
    /// The same day rather than after Jellyfin's next restart: where Jellyfin serves an older Mira web, its « Update
    /// Plugins » task is run now (it installs the new version beside the served one, which then waits for a restart),
    /// then « Redémarrer Jellyfin » is offered once nobody watches anything on it, this PC included: read again every
    /// five minutes meanwhile. Jellyfin's rules stand: a plugin disabled or kept from updating gets nothing.
    /// </summary>
    private async Task BringMiraWebNowAsync(JellyfinClient client, Version version, string done)
    {
        if (await client.WebAppVersionsAsync() is not { Serving: { } serving } versions) return;
        if (serving >= version) { Served(done); return; }
        var waiting = versions.Waiting;
        if (waiting is null || waiting < version)
        {
            if (!await client.UpdatePluginsNowAsync() || !ReferenceEquals(client, _client)) return;
            waiting = (await client.WebAppVersionsAsync())?.Waiting;
            if (waiting is null || waiting < version) return;
        }
        while ((await client.ViewersAsync()).Count > 0)
        {
            await Task.Delay(TimeSpan.FromMinutes(5));
            if (!ReferenceEquals(client, _client)) return;
        }
        if (!ReferenceEquals(client, _client)) return;
        SetNotice(L($"Mira web {waiting.ToString(3)} est prêt sur Jellyfin : ton téléphone l’aura dès que Jellyfin redémarre, en quelques secondes. Personne ne regarde en ce moment.", $"Mira web {waiting.ToString(3)} is ready on Jellyfin: your phone gets it as soon as Jellyfin restarts, in a few seconds. Nobody is watching right now."),
            L("Redémarrer Jellyfin", "Restart Jellyfin"), () => _ = RestartForMiraWebAsync(client, version, done));
    }

    /// <summary>« Redémarrer Jellyfin », unless someone started watching since; then Jellyfin waited for until it serves the new version.</summary>
    private async Task RestartForMiraWebAsync(JellyfinClient client, Version version, string done)
    {
        try
        {
            if ((await client.ViewersAsync()).Count > 0)
            {
                SetNotice(L("Quelqu’un regarde sur Jellyfin en ce moment : Mira te proposera de le redémarrer quand ce sera fini.", "Someone is watching on Jellyfin right now: Mira will offer to restart it once they’re done."));
                await BringMiraWebNowAsync(client, version, done);
                return;
            }
            SetNotice(L("Jellyfin redémarre…", "Jellyfin is restarting…"), null, null, sticky: true);
            await client.RestartAsync();
        }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); return; }
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        await Task.Delay(TimeSpan.FromSeconds(2));
        while (DateTime.UtcNow < limit && ReferenceEquals(client, _client))
        {
            try
            {
                if (await client.WebAppVersionsAsync() is { Serving: { } serving } && serving >= version)
                {
                    Served(done);
                    SetNotice(L($"Jellyfin a redémarré : ton téléphone a Mira web {serving.ToString(3)} à sa prochaine ouverture.", $"Jellyfin restarted: your phone gets Mira web {serving.ToString(3)} the next time you open it."));
                    return;
                }
            }
            // Silent while it restarts.
            catch (Exception ex) when (IsExpected(ex)) { }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        if (ReferenceEquals(client, _client)) SetNotice(L("Jellyfin n’a pas encore fini de redémarrer : Mira web arrive sur ton téléphone dès qu’il répond.", "Jellyfin hasn’t finished restarting yet: Mira web reaches your phone as soon as it answers."));
    }

    private void Served(string done) { _settings.MiraWebServed = done; _profile.SaveSettings(_settings); }
}
