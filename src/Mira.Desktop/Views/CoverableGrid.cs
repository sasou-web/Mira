using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Mira.Desktop.Views;

/// <summary>
/// The library's page, kept drawn-ready under a title's page, the Settings or a sign-in that covers it (opacity 0, never
/// taken out: coming back stays instant). While <see cref="Covered"/>, UI Automation sees nothing in it: Narrator and the
/// other readers no longer walk the posters behind a title's page (the Dell, 2026-11-14).
/// </summary>
public sealed class CoverableGrid : Grid
{
    private bool _covered;
    public bool Covered
    {
        get => _covered;
        set
        {
            if (_covered == value) return;
            _covered = value;
            // Readers that listen are told the tree changed; the others read it again when they next look.
            if (UIElementAutomationPeer.FromElement(this) is { } peer) peer.RaiseAutomationEvent(AutomationEvents.StructureChanged);
        }
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(CoverableGrid owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override List<AutomationPeer>? GetChildrenCore() => owner.Covered ? null : base.GetChildrenCore();
        protected override bool IsOffscreenCore() => owner.Covered || base.IsOffscreenCore();
    }
}
