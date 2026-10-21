using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

/// <summary>
/// Answers "which commands are valid for these dropped files?". The answer is the intersection: an operation is
/// offered only if it applies to every file's format hint, and every path passes <see cref="PathPolicy"/>. Unsupported
/// files, refused paths and empty or oversized selections get no commands, never placeholders.
/// </summary>
public sealed class CapabilityResolver(IReadOnlyList<Operation> catalog)
{
    /// <summary>Upper bound on files per drop; larger selections get no commands.</summary>
    public const int MaxFiles = 1000;

    public static CapabilityResolver Default { get; } = new(OperationCatalog.All);

    public IReadOnlyList<Operation> Resolve(IReadOnlyList<string> paths)
    {
        if (paths.Count is 0 or > MaxFiles) return [];
        var formats = new HashSet<ImageFormat>();
        foreach (var p in paths)
        {
            if (!PathPolicy.IsSupported(p)) return [];
            var f = FormatHint.FromPath(p);
            if (f == ImageFormat.Unknown) return [];
            formats.Add(f);
        }
        return catalog.Where(o => formats.All(o.AppliesTo)).ToArray();
    }
}
