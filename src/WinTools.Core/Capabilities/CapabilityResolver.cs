using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

/// <summary>
/// Answers "which commands are valid for these dropped files?". An operation is offered only when every file's
/// format hint matches its source format and every path passes <see cref="PathPolicy"/>; mixed or unsupported
/// selections get no commands, never placeholders.
/// </summary>
public sealed class CapabilityResolver(IReadOnlyList<Operation> catalog)
{
    /// <summary>Upper bound on files per drop for this slice; larger selections get no commands.</summary>
    public const int MaxFiles = 50;

    public static CapabilityResolver Default { get; } = new(OperationCatalog.All);

    public IReadOnlyList<Operation> Resolve(IReadOnlyList<string> paths)
    {
        if (paths.Count is 0 or > MaxFiles) return [];
        foreach (var p in paths) if (!PathPolicy.IsSupported(p)) return [];
        var format = FormatHint.FromPath(paths[0]);
        if (format == ImageFormat.Unknown) return [];
        for (var i = 1; i < paths.Count; i++)
            if (FormatHint.FromPath(paths[i]) != format) return [];
        return catalog.Where(o => o.Source == format).ToArray();
    }
}
