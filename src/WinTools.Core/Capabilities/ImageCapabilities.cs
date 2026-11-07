using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

/// <summary>
/// The image module's catalog: each <see cref="Operation"/> as a capability, pinned by MenuGoldenTests; which files an
/// operation applies to is still <see cref="Operation.AppliesTo"/>, asked through the file's kind. Collage is the one
/// command that takes the whole selection.
/// </summary>
public static class ImageCapabilities
{
    public const string Module = "images";
    public const string EditCategory = "image-edit";

    /// <summary>The image module's categories: V1's, with Edit (ADR-0031).</summary>
    public static IReadOnlyList<CapabilityCategory> Categories { get; } =
    [
        new("convert", "Convert", "convert", 10),
        new("compress", "Compress", "compress", 20),
        new("resize", "Resize", "resize", 30),
        // The rotations, flips, crops and adjustments together (ADR-0031): a top ring holds six groups with room for their
        // labels. Rotate's old place, so the rotations stay one stroke away; its own id, so video's rings keep their order.
        new(EditCategory, "Edit", "edit", 40),
        new("tools", "Tools", "tools", 11), // Convert, then Tools, then the module's own groups, Actions last (Tangerine's order)
    ];

    /// <summary>The image capabilities, with the settings' exact resize when it is not a preset.</summary>
    public static IReadOnlyList<CapabilityDescriptor> Catalog(string? customResizeId) => Operations(customResizeId).Select(Describe).ToArray();

    /// <summary>
    /// The operations the image module offers: the fixed catalog plus the exact resize from the settings
    /// (<see cref="Settings.AppSettings.CustomResizeId"/>), unless that size already is a preset. It comes last, so it ends
    /// up last in Resize.
    /// </summary>
    public static IReadOnlyList<Operation> Operations(string? customResizeId)
    {
        if (OperationCatalog.Find(customResizeId) is not { } custom || OperationCatalog.All.Any(o => o.Id == custom.Id)) return OperationCatalog.All;
        // Same size as a preset but different behaviour (stretch, enlarge): the label must tell them apart.
        if (OperationCatalog.All.Any(o => o.Label == custom.Label))
            custom = custom with { Label = custom.Label + (custom.Resize is { KeepAspect: false } ? " (stretch)" : " (enlarge)") };
        return [.. OperationCatalog.All, custom];
    }

    public static CapabilityDescriptor Describe(Operation op) => new(
        op.Id, op.Label, CategoryOf(op.Kind), Module,
        new InputRule(FileKinds.All.Where(kind => op.AppliesTo(FormatHint.Of(kind))).Select(kind => kind.Id).ToHashSet(StringComparer.Ordinal),
            MinCount: op.Kind == OperationKind.Collage ? 2 : 1,
            MaxCount: op.Kind == OperationKind.Collage ? OperationCatalog.MaxCollageInputs : CapabilityResolver.MaxFiles),
        op.Kind == OperationKind.Collage ? CapabilityShape.Selection : CapabilityShape.PerFile,
        op.WritesFile ? CapabilityOutput.File : CapabilityOutput.Facts,
        LongRunning: op.Kind != OperationKind.Info,
        Payload: op,
        Feedback: Feedback.FeedbackText.WordsOf(op));

    /// <summary>The image operation behind one of this module's capabilities.</summary>
    public static Operation OperationOf(CapabilityDescriptor capability) =>
        TryOperationOf(capability, out var op) ? op : throw new ArgumentException($"{capability.Id} is not an image capability.", nameof(capability));

    /// <summary>The image operation behind a capability, if it is one of this module's.</summary>
    public static bool TryOperationOf(CapabilityDescriptor capability, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Operation? op)
    {
        op = capability.Module == Module ? capability.Payload as Operation : null;
        return op is not null;
    }

    public static string CategoryOf(OperationKind kind) => kind switch
    {
        OperationKind.Convert => "convert",
        OperationKind.Compress => "compress",
        OperationKind.Resize => "resize",
        OperationKind.Rotate or OperationKind.Flip or OperationKind.Crop or OperationKind.Adjust => EditCategory,
        _ => "tools",
    };
}
