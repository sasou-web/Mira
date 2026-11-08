using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

// Jellyfin's remote control (RemoteControl of Core), as on Windows (MainWindow.Remote.cs there): this Mac says what it
// takes each time its WebSocket opens, then obeys what another device sends through it: « Lire sur… » of the iPhone
// and of Jellyfin's pages (Play), pause, seek, next and stop (« Arrêter » of « En ce moment sur Jellyfin » included),
// volume, full screen, a message on screen. Jellyfin decides who may send them; a party's guest takes none.
public sealed partial class MainWindow
{
    /// <summary>A command of another device, on the interface's thread.</summary>
    internal void Remote(Session session, RemoteCommand command)
    {
        if (!ReferenceEquals(session, Session) || InParty) return;
        switch (command.Action)
        {
            case RemoteAction.Play when command.ItemId is { } id: _ = PlayFromElsewhereAsync(session, id, command.Start); break;
            case RemoteAction.Stop: Player?.Touched(); _ = StopPlaybackAsync(); break;
            case RemoteAction.Message when Player is not { } player || !player.Steer(command): Notice(RemoteControl.Said(command)); break;
            case RemoteAction.Message: break;
            default: Player?.Steer(command); break;
        }
    }

    /// <summary>
    /// « Lire sur… »: the title from where the other device said (0 = from the start; otherwise where this account left
    /// it), Mira brought to the front if it was hidden. In a group, as Lecture is: for the group.
    /// </summary>
    private async Task PlayFromElsewhereAsync(Session session, string id, double? start)
    {
        try
        {
            var item = await session.ItemAsync(id);
            if (!ReferenceEquals(session, Session)) return;
            if (!IsVisible || WindowState == Avalonia.Controls.WindowState.Minimized) { Show(); if (WindowState == Avalonia.Controls.WindowState.Minimized) WindowState = Avalonia.Controls.WindowState.Normal; }
            await PlayAsync(item, fromStart: start is 0);
        }
        catch (Exception ex) when (Errors.Expected(ex)) { if (ReferenceEquals(session, Session)) Notice(ex is IOException ? ex.Message : Errors.Friendly(ex)); }
    }
}
