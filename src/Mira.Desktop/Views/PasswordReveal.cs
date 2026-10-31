using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using static Mira.Core.Lang;
namespace Mira.Desktop.Views;

/// <summary>An eye button that shows a password in a plain box laid over the PasswordBox, and a Caps Lock notice while typing in either.</summary>
internal static class PasswordReveal
{
    public static void Attach(PasswordBox password, TextBox plain, Button eye, Icon icon, TextBlock caps)
    {
        password.PasswordChanged += (_, _) => { if (plain.Text != password.Password) plain.Text = password.Password; };
        plain.TextChanged += (_, _) => { if (password.Password != plain.Text) password.Password = plain.Text; };
        eye.Click += (_, _) =>
        {
            var show = plain.Visibility != Visibility.Visible;
            plain.Text = password.Password;
            plain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            password.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            icon.Kind = show ? "eye-off" : "eye";
            var label = show ? L("Masquer le mot de passe", "Hide password") : L("Afficher le mot de passe", "Show password");
            System.Windows.Automation.AutomationProperties.SetName(eye, label); eye.ToolTip = label;
            var box = show ? (Control)plain : password; box.Focus();
            if (show) plain.CaretIndex = plain.Text.Length;
            Refresh();
        };
        void Refresh() => caps.Visibility = Keyboard.IsKeyToggled(Key.CapsLock) && (password.IsKeyboardFocused || plain.IsKeyboardFocused) ? Visibility.Visible : Visibility.Collapsed;
        foreach (UIElement box in new UIElement[] { password, plain })
        {
            box.GotKeyboardFocus += (_, _) => Refresh(); box.LostKeyboardFocus += (_, _) => caps.Visibility = Visibility.Collapsed;
            box.PreviewKeyUp += (_, _) => Refresh(); box.PreviewKeyDown += (_, _) => Refresh();
        }
    }
}
