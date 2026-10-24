using WinTools.Core.Capabilities;

namespace WinTools.Core.Interaction;

/// <summary>One category of the radial menu and the commands it offers, in catalog order.</summary>
public sealed record MenuGroup(CapabilityCategory Category, string Label, IReadOnlyList<CapabilityDescriptor> Commands);

/// <summary>
/// Builds the two-level radial menu from the capabilities the resolver allows. The menu has no format logic: it groups
/// whatever it is given by the capability's category, in the order the modules declared, clockwise from 12 o'clock, and
/// leaves out empty categories. A new capability or a new category appears without any change here.
/// </summary>
public static class RadialMenu
{
    /// <summary>
    /// Groups the allowed capabilities by their category, the categories in their declared order, leaving out empty
    /// ones. A capability whose category no module declared goes to <see cref="CapabilityCategory.Other"/>.
    /// </summary>
    public static IReadOnlyList<MenuGroup> Build(IReadOnlyList<CapabilityDescriptor> allowed, IReadOnlyList<CapabilityCategory> categories)
    {
        var known = categories.ToDictionary(c => c.Id, StringComparer.Ordinal);
        return allowed
            .GroupBy(c => known.TryGetValue(c.Category, out var category) ? category : CapabilityCategory.Other)
            .OrderBy(g => g.Key.Order).ThenBy(g => g.Key.Id, StringComparer.Ordinal)
            .Select(g => new MenuGroup(g.Key, g.Key.Label, g.ToArray()))
            .ToArray();
    }

    /// <summary>The menu for what a resolver allows, with that resolver's categories.</summary>
    public static IReadOnlyList<MenuGroup> Build(CapabilityResolver resolver, IReadOnlyList<string> paths) => Build(resolver.Resolve(paths), resolver.Categories);
}
