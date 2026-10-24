using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

/// <summary>
/// The image module's catalog: each <see cref="Operation"/> as a capability. Ids, labels, categories and catalog order
/// are exactly those of V1 (MenuGoldenTests); which files an operation applies to is still
/// <see cref="Operation.AppliesTo"/>, asked through the file's kind.
/// </summary>
public static class ImageCapabilities
{
    public const string Module = "images";

    /// <summary>The image capabilities, with the settings' exact resize when it is not a preset.</summary>
    public static IReadOnlyList<CapabilityDescriptor> Catalog(string? customResizeId) =>
        Interaction.RadialMenu.Catalog(customResizeId).Select(Describe).ToArray();

    public static CapabilityDescriptor Describe(Operation op) => new(
        op.Id, op.Label, CategoryOf(op.Kind), Module,
        new InputRule(kind => op.AppliesTo(FormatHint.Of(kind))),
        CapabilityShape.PerFile,
        op.WritesFile ? CapabilityOutput.File : CapabilityOutput.Facts,
        LongRunning: op.Kind != OperationKind.Info,
        Payload: op);

    /// <summary>The image operation behind one of this module's capabilities.</summary>
    public static Operation OperationOf(CapabilityDescriptor capability) =>
        capability.Payload as Operation ?? throw new ArgumentException($"{capability.Id} is not an image capability.", nameof(capability));

    public static string CategoryOf(OperationKind kind) => kind switch
    {
        OperationKind.Convert => "convert",
        OperationKind.Compress => "compress",
        OperationKind.Resize => "resize",
        OperationKind.Rotate => "rotate",
        _ => "tools",
    };
}
