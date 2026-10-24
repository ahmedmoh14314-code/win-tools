using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

/// <summary>
/// Answers "which commands are valid for these dropped files?" from the capability descriptors of every module. The
/// answer is the intersection: a command is offered only if it applies to every file's kind and to the selection's
/// size, and every path passes <see cref="PathPolicy"/>. Unrecognised files, refused paths and empty or oversized
/// selections get no commands, never placeholders.
/// </summary>
public sealed class CapabilityResolver(IReadOnlyList<CapabilityDescriptor> catalog)
{
    /// <summary>Upper bound on files per drop; larger selections get no commands.</summary>
    public const int MaxFiles = 1000;

    public static CapabilityResolver Default { get; } = For(customResizeId: null);

    /// <summary>The resolver over every module's catalog, with the settings' exact resize.</summary>
    public static CapabilityResolver For(string? customResizeId) => new(ImageCapabilities.Catalog(customResizeId));

    public IReadOnlyList<CapabilityDescriptor> Catalog => catalog;

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
        return catalog.Where(c => c.AppliesTo(kinds, paths.Count)).ToArray();
    }
}
