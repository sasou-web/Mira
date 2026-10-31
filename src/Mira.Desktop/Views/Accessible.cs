using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Mira.Core;

namespace Mira.Desktop.Views;

/// <summary>What screen readers (Narrator, NVDA) get from Mira's controls: their names, and the messages said aloud.</summary>
public static class Accessible
{
    /// <summary>
    /// The text a control shows, as a screen reader should name it: a string, or the words of a label built from an icon
    /// and text (« Plus d’infos », « Dans tes favoris »); empty for an icon alone.
    /// </summary>
    public static string ContentText(object? content) => content switch
    {
        string text => text,
        TextBlock block => block.Text,
        AccessText access => access.Text,
        ContentControl control => ContentText(control.Content),
        Decorator decorator => ContentText(decorator.Child),
        Panel panel => string.Join(" ", panel.Children.OfType<object>().Select(ContentText).Where(x => x.Length > 0)),
        _ => ""
    };
    /// <summary>Names a button after its <see cref="ContentControl.Content"/> (the default style's AutomationProperties.Name).</summary>
    public static readonly IValueConverter ContentName = new ContentNameConverter();
    private sealed class ContentNameConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => ContentText(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>
    /// The controls shown under <paramref name="root"/> that a keyboard reaches but a screen reader could not name:
    /// buttons, switches, fields, lists and sliders with an empty UI Automation name. Shown means visible up to the root.
    /// </summary>
    public static List<FrameworkElement> Unnamed(DependencyObject root)
    {
        var found = new List<FrameworkElement>();
        void Visit(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return;
            if (node is FrameworkElement { Focusable: true } control && control is ButtonBase or TextBoxBase or PasswordBox or ComboBox or Slider
                && string.IsNullOrWhiteSpace(UIElementAutomationPeer.CreatePeerForElement(control)?.GetName())) found.Add(control);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
        Visit(root);
        return found;
    }
    /// <summary>False from the moment the window goes to the background until Tab is pressed in it again: coming back by
    /// Alt+Tab, WPF puts the keyboard ring back on the focused button, which nobody asked for (KeyboardFocus hides it).</summary>
    public static readonly DependencyProperty FocusRingShownProperty = DependencyProperty.RegisterAttached("FocusRingShown", typeof(bool), typeof(Accessible),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetFocusRingShown(DependencyObject element) => (bool)element.GetValue(FocusRingShownProperty);
    public static void SetFocusRingShown(DependencyObject element, bool value) => element.SetValue(FocusRingShownProperty, value);
    /// <summary>Set on a control that draws its own keyboard focus (the ring of a card): <see cref="WithoutFocusRing"/> takes its word for it.</summary>
    public static readonly DependencyProperty OwnFocusRingProperty = DependencyProperty.RegisterAttached("OwnFocusRing", typeof(bool), typeof(Accessible), new PropertyMetadata(false));
    public static bool GetOwnFocusRing(DependencyObject element) => (bool)element.GetValue(OwnFocusRingProperty);
    public static void SetOwnFocusRing(DependencyObject element, bool value) => element.SetValue(OwnFocusRingProperty, value);
    /// <summary>
    /// The controls shown under <paramref name="root"/> that Tab reaches but that would show no ring there: none, or
    /// WPF's own (a black dotted line, unseen on Mira's dark pages). A ring is Mira's <paramref name="ring"/> style
    /// (KeyboardFocus), a field's border lit by its template, or one the control draws itself (<see cref="OwnFocusRingProperty"/>).
    /// </summary>
    public static List<FrameworkElement> WithoutFocusRing(DependencyObject root, Style ring)
    {
        var found = new List<FrameworkElement>();
        static bool LitByTemplate(Control control) => control.Template?.Triggers.OfType<Trigger>().Any(t => t.Property == UIElement.IsKeyboardFocusedProperty || t.Property == UIElement.IsKeyboardFocusWithinProperty || t.Property == UIElement.IsFocusedProperty) == true;
        void Visit(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return;
            if (node is Control { Focusable: true, IsEnabled: true } control && control is ButtonBase or TextBoxBase or PasswordBox or ComboBox or Slider or ListBoxItem
                && KeyboardNavigation.GetIsTabStop(control) && !ReferenceEquals(control.FocusVisualStyle, ring) && !GetOwnFocusRing(control) && !LitByTemplate(control)) found.Add(control);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
        Visit(root);
        return found;
    }
    /// <summary>A control as a person finding it in a list of faults would: its type, x:Name and where it sits.</summary>
    public static string Describe(FrameworkElement control)
    {
        var path = new List<string>();
        for (DependencyObject? node = control; node is not null && path.Count < 6; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { Name.Length: > 0 } named) path.Add(named.Name);
        return control.GetType().Name + (path.Count > 0 ? " in " + string.Join(" < ", path) : "");
    }

    /// <summary>
    /// A status line read aloud by screen readers when its text changes (« Connexion… », an error under the sign-in
    /// form): Polite waits for what is being said, Assertive interrupts it. Off by default.
    /// </summary>
    public static readonly DependencyProperty AnnounceProperty = DependencyProperty.RegisterAttached("Announce", typeof(AutomationLiveSetting), typeof(Accessible), new PropertyMetadata(AutomationLiveSetting.Off, AnnounceChanged));
    public static AutomationLiveSetting GetAnnounce(DependencyObject element) => (AutomationLiveSetting)element.GetValue(AnnounceProperty);
    public static void SetAnnounce(DependencyObject element, AutomationLiveSetting value) => element.SetValue(AnnounceProperty, value);
    private static readonly DependencyPropertyDescriptor TextChanges = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
    private static void AnnounceChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block) return;
        AutomationProperties.SetLiveSetting(block, (AutomationLiveSetting)e.NewValue);
        if ((AutomationLiveSetting)e.OldValue == AutomationLiveSetting.Off) TextChanges.AddValueChanged(block, TextChanged);
        else if ((AutomationLiveSetting)e.NewValue == AutomationLiveSetting.Off) TextChanges.RemoveValueChanged(block, TextChanged);
    }
    private static void TextChanged(object? sender, EventArgs e) { if (sender is TextBlock { Text.Length: > 0 } block) Say(block); }
    /// <summary>Has screen readers say <paramref name="element"/> now, as its AutomationProperties.LiveSetting asks.</summary>
    public static void Say(UIElement element)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        (UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    /// <summary>
    /// Arrow keys move between the cards of this panel (a grid of posters, a row of « Continuer à regarder »), as on a
    /// TV; at an edge the key does what it did before (scroll, or leave the panel). See <see cref="CardGrid"/>.
    /// </summary>
    public static readonly DependencyProperty ArrowKeysProperty = DependencyProperty.RegisterAttached("ArrowKeys", typeof(bool), typeof(Accessible), new PropertyMetadata(false, ArrowKeysChanged));
    public static bool GetArrowKeys(DependencyObject element) => (bool)element.GetValue(ArrowKeysProperty);
    public static void SetArrowKeys(DependencyObject element, bool value) => element.SetValue(ArrowKeysProperty, value);
    private static void ArrowKeysChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Panel panel) return;
        if ((bool)e.NewValue) panel.PreviewKeyDown += MoveAmongCards; else panel.PreviewKeyDown -= MoveAmongCards;
    }
    private static void MoveAmongCards(object sender, KeyEventArgs e)
    {
        if (sender is not Panel panel || Keyboard.Modifiers != ModifierKeys.None) return;
        CardGrid.Direction? direction = e.Key switch { Key.Left => CardGrid.Direction.Left, Key.Right => CardGrid.Direction.Right, Key.Up => CardGrid.Direction.Up, Key.Down => CardGrid.Direction.Down, _ => null };
        if (direction is not { } way || e.OriginalSource is not DependencyObject source) return;
        var cards = panel.Children.OfType<FrameworkElement>().Where(x => x.IsVisible && x.Focusable && x.IsEnabled).ToList();
        // The card holding the focus (the key may come from something inside it).
        var current = cards.FindIndex(card => ReferenceEquals(card, source) || source is Visual visual && visual.IsDescendantOf(card));
        if (current < 0) return;
        var boxes = cards.Select(card => card.TransformToAncestor(panel).TransformBounds(new Rect(card.RenderSize))).Select(x => new CardGrid.Box(x.Left, x.Top, x.Width, x.Height)).ToList();
        if (CardGrid.Neighbour(boxes, current, way) is not { } next) return;
        cards[next].Focus(); cards[next].BringIntoView(); e.Handled = true;
    }
}
