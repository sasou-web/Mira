using System.Windows;
using System.Windows.Threading;
using Mira.Core;

namespace Mira.Desktop;

/// <summary>
/// « Tes choix gardés sur ton Jellyfin » (<see cref="AccountChoices"/>): the languages, the intros, the tracks of each series
/// and the titles removed from « Continuer à regarder » follow the account to its other devices, and come from them.
/// </summary>
public partial class MainWindow
{
    // A change here goes a moment later (several together go at once); the window coming forward brings what changed elsewhere.
    private readonly DispatcherTimer _choicesSoon = new() { Interval = TimeSpan.FromSeconds(2) };
    private DateTimeOffset _choicesAsked = DateTimeOffset.MinValue;
    private bool _choicesBusy, _choicesAgain;
    private void InitializeChoices()
    {
        _choicesSoon.Tick += async (_, _) => { _choicesSoon.Stop(); await SyncChoicesAsync(); };
        Activated += (_, _) => { if (DateTimeOffset.UtcNow - _choicesAsked > TimeSpan.FromMinutes(1)) ChoicesChanged(); };
    }
    /// <summary>Something kept on Jellyfin changed here, or may have changed elsewhere: Mira and Jellyfin compare them shortly.</summary>
    private void ChoicesChanged()
    {
        if (_demo || _client is null || _store is null) return;
        _choicesSoon.Stop(); _choicesSoon.Start();
    }
    private async Task SyncChoicesAsync()
    {
        if (_demo || _closing || _client is not { } client || _store is not { } store) return;
        if (_choicesBusy) { _choicesAgain = true; return; }
        _choicesBusy = true; _choicesAsked = DateTimeOffset.UtcNow;
        try
        {
            var update = await AccountChoices.SyncAsync(client, store, () => (_settings.AudioLanguage, _settings.SubtitleLanguage, _settings.AutoSkip), _connectionLifetime.Token);
            if (ReferenceEquals(client, _client) && !_closing) UseChoices(update);
        }
        // Jellyfin or the disk not answering now: what changed here keeps its time for the next try.
        catch (Exception ex) when (IsExpected(ex)) { }
        finally
        {
            _choicesBusy = false;
            if (_choicesAgain) { _choicesAgain = false; ChoicesChanged(); }
        }
    }
    private void UseChoices(AccountChoices.Update update)
    {
        if (update.SettingsChanged)
        {
            // The settings page open shows what closing it saves: what came from Jellyfin shows there too, unless it was
            // changed on the page meanwhile (that change is newer, and goes to Jellyfin once saved).
            var page = SettingsOverlay.Visibility == Visibility.Visible;
            _settingsBusy = true;
            if (update.AudioLanguage is { } audio)
            {
                if (page && AudioLanguageBox.Text.Trim() == _settings.AudioLanguage) { AudioLanguageBox.Text = audio; SelectChoice(AudioLanguageChoice, audio); }
                _settings.AudioLanguage = audio;
            }
            if (update.SubtitleLanguage is { } subtitles)
            {
                if (page && SubtitleLanguageBox.Text.Trim() == _settings.SubtitleLanguage) { SubtitleLanguageBox.Text = subtitles; SelectChoice(SubtitleLanguageChoice, subtitles); }
                _settings.SubtitleLanguage = subtitles;
            }
            if (update.AutoSkip is { } skip)
            {
                if (page && (AutoSkipCheck.IsChecked == true) == _settings.AutoSkip) AutoSkipCheck.IsChecked = skip;
                _settings.AutoSkip = skip;
            }
            _settingsBusy = false;
            if (page) LanguageChoice_Changed(this, null!);
            TrySaveSettings();
        }
        if (update.Hidden is { } hidden) { _catalog.HiddenResume = hidden; RenderLibrary(); }
    }
}
