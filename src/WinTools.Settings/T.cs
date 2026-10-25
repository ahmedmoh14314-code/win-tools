using System.Windows.Markup;
using WinTools.Core.Localization;

namespace WinTools.Settings;

/// <summary>
/// <c>{l:T key}</c> in XAML: the text of a key in the language in use (<see cref="UiText"/>). The window is built again
/// when the language changes, so the text is read once, when it loads.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class T : MarkupExtension
{
    public T() { }

    public T(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => UiText.T(Key);
}
