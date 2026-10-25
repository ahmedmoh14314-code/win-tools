using System.Windows.Markup;
using WinTools.Core.Localization;

namespace WinTools.Dialogs;

/// <summary><c>{l:T key}</c> in XAML: the text of a key in the language the host passed (<c>--lang</c>).</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class T : MarkupExtension
{
    public T() { }

    public T(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => UiText.T(Key);
}
