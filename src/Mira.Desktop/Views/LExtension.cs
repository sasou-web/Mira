using System.Windows.Markup;

namespace Mira.Desktop.Views;

/// <summary>
/// A text of the window in Mira's language, written in both where it is shown: <c>{v:L 'Lecture', 'Play'}</c>
/// (<see cref="Mira.Core.Lang.L"/>). Read when the window loads, after App chose the language.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LExtension(string french, string english) : MarkupExtension
{
    [ConstructorArgument("french")] public string French { get; } = french;
    [ConstructorArgument("english")] public string English { get; } = english;
    public override object ProvideValue(IServiceProvider serviceProvider) => L(French, English);
}
