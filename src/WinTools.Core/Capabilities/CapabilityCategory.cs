namespace WinTools.Core.Capabilities;

/// <summary>
/// One category of the radial menu, as module data (V2 PR 1.3): a stable id that capabilities name, the label on the
/// ring, the key of its icon, and its place clockwise from 12 o'clock (lower first). Modules that share an id share the
/// category (several modules add to "tools"). <paramref name="MostOnRing"/> (ADR-0032): the most sectors its commands take
/// on their ring, "More…" included, when the labels are long ("Rename files" did not fit five to a ring at every DPI);
/// null for the ring's own limit.
/// </summary>
public sealed record CapabilityCategory(string Id, string Label, string Icon, int Order, int? MostOnRing = null)
{
    /// <summary>
    /// The limit of a category of long labels: four sectors, so three commands and "More…" when it overflows. A file no
    /// module reads has two categories, and Back then takes half the ring: a fifth sector would leave each 36 px at 96 DPI,
    /// too narrow even for "More…" (UiSurfaceTests).
    /// </summary>
    public const int LongLabels = 4;

    /// <summary>Where a capability goes whose category no module declared.</summary>
    public static CapabilityCategory Other { get; } = new("other", "Other", "tools", 1000);

    /// <summary>The top-level ring's last sector when there are more categories than fit (RadialMenu.RingCapacity).</summary>
    public static CapabilityCategory More { get; } = new("more", "More…", "more", int.MaxValue);
}
