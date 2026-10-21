using WinTools.Core.Capabilities;

namespace WinTools.Core.Interaction;

public enum MenuCategory { Convert, Compress, Resize, Rotate, Tools }

/// <summary>One category of the radial menu and the commands it offers, in catalog order.</summary>
public sealed record MenuGroup(MenuCategory Category, string Label, IReadOnlyList<Operation> Commands);

/// <summary>
/// Builds the two-level radial menu from the operations the capability resolver allows. The menu has no format logic:
/// it groups whatever it is given by operation kind, in a fixed category order clockwise from 12 o'clock, and leaves
/// out empty categories. A new operation of an existing kind appears without any change here.
/// </summary>
public static class RadialMenu
{
    public static MenuCategory CategoryOf(OperationKind kind) => kind switch
    {
        OperationKind.Convert => MenuCategory.Convert,
        OperationKind.Compress => MenuCategory.Compress,
        OperationKind.Resize => MenuCategory.Resize,
        OperationKind.Rotate => MenuCategory.Rotate,
        _ => MenuCategory.Tools,
    };

    public static string LabelOf(MenuCategory category) => category.ToString();

    public static IReadOnlyList<MenuGroup> Build(IReadOnlyList<Operation> allowed) =>
        Enum.GetValues<MenuCategory>()
            .Select(c => new MenuGroup(c, LabelOf(c), allowed.Where(o => CategoryOf(o.Kind) == c).ToArray()))
            .Where(g => g.Commands.Count > 0)
            .ToArray();

    /// <summary>
    /// The operations the menu may offer: the fixed catalog plus the exact resize from the settings
    /// (<see cref="Settings.AppSettings.CustomResizeId"/>), unless that size already is a preset. Grouping keeps catalog
    /// order, so the exact size ends up last in Resize.
    /// </summary>
    public static IReadOnlyList<Operation> Catalog(string? customResizeId) =>
        OperationCatalog.Find(customResizeId) is { } custom && OperationCatalog.All.All(o => o.Id != custom.Id)
            ? [.. OperationCatalog.All, custom]
            : OperationCatalog.All;
}
