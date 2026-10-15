using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Mira.Desktop.Services;

/// <summary>
/// Windows notifications of Mira (« Nouvel épisode », someone who joined a group), under the Shell identity
/// <see cref="WindowsIdentity.Register"/> writes: the validation profiles show none.
/// </summary>
internal static class Toasts
{
    // A notification no longer referenced may lose its Activated handler: the last ones shown are kept.
    private static readonly Queue<ToastNotification> Shown = new();
    /// <summary>
    /// Shows one; <paramref name="activated"/> runs on a thread of Windows when it is clicked while Mira runs. Kept in
    /// Windows' notification center three days, or <paramref name="expires"/>.
    /// </summary>
    public static bool Show(string title, string text, string? image, Action activated, string group = "episodes", TimeSpan? expires = null)
    {
        if (App.ShellValidation) return false;
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml("<toast><visual><binding template=\"ToastGeneric\">" +
                $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(text)}</text>" +
                (image is null ? "" : $"<image src=\"{SecurityElement.Escape(new Uri(image).AbsoluteUri)}\"/>") +
                "</binding></visual></toast>");
            var toast = new ToastNotification(xml) { Group = group, ExpirationTime = DateTimeOffset.Now.Add(expires ?? TimeSpan.FromDays(3)) };
            toast.Activated += (_, _) => activated();
            ToastNotificationManager.CreateToastNotifier(App.ShellAppId).Show(toast);
            lock (Shown) { Shown.Enqueue(toast); while (Shown.Count > 12) Shown.Dequeue(); }
            return true;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException or UriFormatException or IOException) { return false; }
    }
}
