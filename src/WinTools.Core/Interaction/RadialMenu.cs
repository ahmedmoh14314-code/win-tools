using WinTools.Core.Capabilities;

namespace WinTools.Core.Interaction;

public enum MenuCategory { Convert, Compress, Resize, Rotate, Tools }

/// <summary>One category of the radial menu and the commands it offers, in catalog order.</summary>
public sealed record MenuGroup(MenuCategory Category, string Label, IReadOnlyList<CapabilityDescriptor> Commands);

/// <summary>
/// Builds the two-level radial menu from the capabilities the resolver allows. The menu has no format logic: it groups
/// whatever it is given by the capability's category, in a fixed category order clockwise from 12 o'clock, and leaves
/// out empty categories. A new capability in an existing category appears without any change here (categories become
/// module data in V2 PR 1.3).
/// </summary>
public static class RadialMenu
{
    public static string LabelOf(MenuCategory category) => category.ToString();

    /// <summary>The menu category for a capability's category id; unknown ids go to Tools.</summary>
    public static MenuCategory CategoryOf(string categoryId) => categoryId switch
    {
        "convert" => MenuCategory.Convert,
        "compress" => MenuCategory.Compress,
        "resize" => MenuCategory.Resize,
        "rotate" => MenuCategory.Rotate,
        _ => MenuCategory.Tools,
    };

    public static IReadOnlyList<MenuGroup> Build(IReadOnlyList<CapabilityDescriptor> allowed) =>
        Enum.GetValues<MenuCategory>()
            .Select(c => new MenuGroup(c, LabelOf(c), allowed.Where(o => CategoryOf(o.Category) == c).ToArray()))
            .Where(g => g.Commands.Count > 0)
            .ToArray();
}
