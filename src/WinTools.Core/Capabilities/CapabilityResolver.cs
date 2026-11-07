using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

/// <summary>
/// Answers "which commands are valid for these dropped files?" from the capability descriptors of every module. The
/// answer is the intersection: a command is offered only if it applies to every file's kind and to the selection's
/// size, and every path passes <see cref="PathPolicy"/>. Unrecognised files, refused paths and empty or oversized
/// selections get no commands, never placeholders.
/// </summary>
public sealed class CapabilityResolver(IReadOnlyList<CapabilityDescriptor> catalog, IReadOnlyList<CapabilityCategory>? categories = null)
{
    /// <summary>Upper bound on files per drop; larger selections get no commands.</summary>
    public const int MaxFiles = 1000;

    public static CapabilityResolver Default { get; } = For(customResizeId: null);

    /// <summary>
    /// The resolver over every module's catalog, with the settings' exact resize. A command that needs a program the user
    /// installs (<see cref="CapabilityDescriptor.Requires"/>) is in it only when <paramref name="installed"/> names that
    /// program (ADR-0033): without LibreOffice, To PDF is not a button on the wheel.
    /// </summary>
    public static CapabilityResolver For(string? customResizeId, IReadOnlySet<string>? installed = null) =>
        new([.. ((IEnumerable<CapabilityDescriptor>)[.. ImageCapabilities.Catalog(customResizeId), .. PdfCapabilities.Catalog, .. SubtitleCapabilities.Catalog, .. DocumentCapabilities.Catalog, .. OfficeCapabilities.Catalog, .. MediaCapabilities.Catalog, .. FilesCapabilities.Catalog, .. ArchiveCapabilities.Catalog])
                .Where(c => c.Requires is null || installed?.Contains(c.Requires) == true)],
            [.. ImageCapabilities.Categories, .. PdfCapabilities.Categories, .. ArchiveCapabilities.Categories, .. SubtitleCapabilities.Categories, .. DocumentCapabilities.Categories, .. OfficeCapabilities.Categories, .. MediaCapabilities.Categories, .. FilesCapabilities.Categories]);

    /// <summary>The image module alone (V1's menu, which MenuGoldenTests pins).</summary>
    public static CapabilityResolver Images(string? customResizeId) => new(ImageCapabilities.Catalog(customResizeId), ImageCapabilities.Categories);

    public IReadOnlyList<CapabilityDescriptor> Catalog => catalog;

    /// <summary>Every module's categories, merged by id (the first declaration of an id wins).</summary>
    public IReadOnlyList<CapabilityCategory> Categories { get; } = (categories ?? ImageCapabilities.Categories).DistinctBy(c => c.Id).ToArray();

    public IReadOnlyList<CapabilityDescriptor> Resolve(IReadOnlyList<string> paths)
    {
        if (paths.Count is 0 or > MaxFiles) return [];
        var kinds = new HashSet<FileKind>();
        foreach (var p in paths)
        {
            if (!PathPolicy.IsSupported(p)) return [];
            if (FileKinds.IdentifyByName(p) is not { } kind) return [];
            kinds.Add(kind);
        }
        var offered = catalog.Where(c => c.AppliesTo(kinds, paths.Count)).ToArray();
        return offered.Any(c => c.Fallback)
            ? [.. offered.Where(c => !c.Fallback || !offered.Any(o => o.Module != c.Module && o.Produces == CapabilityOutput.Facts))]
            : offered;
    }
}
