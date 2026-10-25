using WinTools.Core.Capabilities;
using WinTools.Core.Localization;

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
        // The first declaration of an id wins; "more" is the menu's own overflow category, which no module may take.
        var known = categories.Where(c => c.Id != CapabilityCategory.More.Id).DistinctBy(c => c.Id, StringComparer.Ordinal)
            .ToDictionary(c => c.Id, StringComparer.Ordinal);
        var groups = allowed
            .GroupBy(c => known.TryGetValue(c.Category, out var category) ? category : CapabilityCategory.Other)
            .OrderBy(g => g.Key.Order).ThenBy(g => g.Key.Id, StringComparer.Ordinal)
            .Select(g => new MenuGroup(g.Key, g.Key.Label, g.ToArray()))
            .ToList();
        if (groups.Count > RingCapacity) // the last sector holds every category that does not fit
        {
            var rest = groups.Skip(RingCapacity - 1).SelectMany(g => g.Commands).ToArray();
            groups = [.. groups.Take(RingCapacity - 1), new MenuGroup(CapabilityCategory.More, CapabilityCategory.More.Label, rest)];
        }
        return [.. groups.Select(Fit).Select(Localized)];
    }

    /// <summary>A group with its own label and its commands' labels in the language in use (by id, <see cref="UiText"/>);
    /// a label the tables do not have (a custom size from the settings) stays as declared.</summary>
    private static MenuGroup Localized(MenuGroup g) =>
        g with { Label = UiText.TOr("cat." + g.Category.Id, g.Label), Commands = [.. g.Commands.Select(Localized)] };

    private static CapabilityDescriptor Localized(CapabilityDescriptor c) => IsMore(c)
        ? c with { Label = UiText.TOr("cat.more", c.Label), Payload = (IReadOnlyList<CapabilityDescriptor>)[.. Overflow(c).Select(Localized)] }
        : c with { Label = UiText.TOr("cmd." + c.Id, c.Label) };

    /// <summary>The most sectors one ring shows: more would make each too narrow to aim at during a drag.</summary>
    public const int RingCapacity = 8;

    public const string MoreId = "menu.more";

    /// <summary>
    /// How many commands a ring that overflows shows before "More…". A full ring of seven fits only short labels (V1's
    /// Resize); an overflowing category holds long ones ("Remove metadata", "Batch rename…"), which did not fit seven
    /// sectors at any DPI (V2 PR 7.2), so it shows five and "More…".
    /// </summary>
    public const int ShownBeforeMore = 5;

    /// <summary>A category ring is Back plus its commands; beyond seven, five of them and "More…".</summary>
    private static MenuGroup Fit(MenuGroup group)
    {
        if (group.Commands.Count <= RingCapacity - 1) return group;
        var shown = group.Commands.Take(ShownBeforeMore).ToArray();
        var rest = group.Commands.Skip(ShownBeforeMore).ToArray();
        var more = new CapabilityDescriptor(MoreId, "More…", group.Category.Id, "menu", new InputRule(new HashSet<string>()), // never resolved: it only carries the overflow
            CapabilityShape.Selection, CapabilityOutput.Facts, LongRunning: false, Payload: rest);
        return group with { Commands = [.. shown, more] };
    }

    /// <summary>Whether a command is the "More…" sector, which opens its overflow in a list instead of running.</summary>
    public static bool IsMore(CapabilityDescriptor command) => command.Id == MoreId && command.Payload is IReadOnlyList<CapabilityDescriptor>;

    /// <summary>The commands a "More…" sector holds, in menu order.</summary>
    public static IReadOnlyList<CapabilityDescriptor> Overflow(CapabilityDescriptor more) =>
        IsMore(more) ? (IReadOnlyList<CapabilityDescriptor>)more.Payload! : throw new ArgumentException($"{more.Id} is not a More… sector.", nameof(more));

    /// <summary>
    /// Why a selection gets no commands, when the ring can say something more useful than "No actions": folders, which
    /// no command of today's modules reads (UX-009). Null for the general case.
    /// </summary>
    public static string? EmptyReason(CapabilityResolver resolver, IReadOnlyList<string> paths)
    {
        // Only when the folders are the reason: nothing is offered, and without them something would be (or there is
        // nothing else). An empty ring for a folder and 1001 files, or a refused path, has another cause (evaluator, PR #18).
        static bool IsFolder(string p) => ReferenceEquals(Files.FileKinds.IdentifyByName(p), Files.FileKinds.Folder);
        if (!paths.Any(IsFolder) || resolver.Resolve(paths).Count > 0) return null;
        var others = paths.Where(p => !IsFolder(p)).ToArray();
        return others.Length == 0 || resolver.Resolve(others).Count > 0 ? UiText.T("menu.notForFolders") : null;
    }

    /// <summary>The menu for what a resolver allows, with that resolver's categories.</summary>
    public static IReadOnlyList<MenuGroup> Build(CapabilityResolver resolver, IReadOnlyList<string> paths) => Build(resolver.Resolve(paths), resolver.Categories);
}
