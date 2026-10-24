namespace WinTools.Core.Capabilities;

/// <summary>
/// One category of the radial menu, as module data (V2 PR 1.3): a stable id that capabilities name, the label on the
/// ring, the key of its icon, and its place clockwise from 12 o'clock (lower first). Modules that share an id share the
/// category (several modules add to "tools").
/// </summary>
public sealed record CapabilityCategory(string Id, string Label, string Icon, int Order)
{
    /// <summary>Where a capability goes whose category no module declared.</summary>
    public static CapabilityCategory Other { get; } = new("other", "Other", "tools", 1000);
}
