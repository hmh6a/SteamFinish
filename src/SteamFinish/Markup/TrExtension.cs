using System.Windows.Data;
using System.Windows.Markup;
using SteamFinish.Core.Localization;

namespace SteamFinish.Markup;

/// <summary>
/// <c>{m:Tr Some.Key}</c> in XAML. It resolves to a binding against <see cref="Loc"/>'s indexer
/// rather than a plain string, so switching language updates every label in place.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        };

        return binding.ProvideValue(serviceProvider);
    }
}
