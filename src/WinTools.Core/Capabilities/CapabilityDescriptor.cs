using System.Collections.Frozen;
using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

/// <summary>Whether a command runs once per file (each file its own job item) or once for the whole selection.</summary>
public enum CapabilityShape { PerFile, Selection }

/// <summary>What a command leaves behind.</summary>
public enum CapabilityOutput { File, Files, Folder, Facts, Clipboard }

/// <summary>
/// Which selections a command accepts: every file's kind must be one of <paramref name="Kinds"/> (kind ids), and the
/// count must be within bounds. A selection of several kinds is allowed as long as each is listed. Declarative, so a
/// module's supported inputs can be listed and logged. The rule keeps a frozen copy of the ids and refuses an id no
/// <see cref="FileKinds"/> entry declares: "PNG" or ".jpg" would otherwise match nothing, silently.
/// </summary>
public sealed record InputRule(IReadOnlySet<string> Kinds, int MinCount = 1, int MaxCount = CapabilityResolver.MaxFiles)
{
    public IReadOnlySet<string> Kinds { get; } = Checked(Kinds);

    public bool Accepts(FileKind kind) => Kinds.Contains(kind.Id);

    private static FrozenSet<string> Checked(IReadOnlySet<string> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        foreach (var id in kinds)
            if (FileKinds.Find(id) is null) throw new ArgumentException($"No file kind has the id \"{id}\".", nameof(kinds));
        return kinds.ToFrozenSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// One command the radial menu can offer, as the resolver and the menu see it, whatever module provides it (ADR-0007,
/// V2 plan §5). The module keeps its own description of the work in <see cref="Payload"/> (the image module: its
/// <see cref="Operation"/>), unwrapped only through the module (<see cref="ImageCapabilities.TryOperationOf"/>).
/// </summary>
/// <param name="Id">Stable id, also the worker's operation id ("convert.to-png").</param>
/// <param name="Category">The menu category's id ("convert", "compress", ...).</param>
/// <param name="Module">The module that owns the command ("images").</param>
/// <param name="LongRunning">The job shows progress rather than finishing at once.</param>
/// <param name="Feedback">What the user is told about it; null gives words made from the label.</param>
public sealed record CapabilityDescriptor(string Id, string Label, string Category, string Module, InputRule Input,
    CapabilityShape Shape, CapabilityOutput Produces, bool LongRunning, object? Payload = null, Feedback.FeedbackWords? Feedback = null)
{
    /// <summary>The words of the toasts about this command (the module's, so the presenter knows no module).</summary>
    public Feedback.FeedbackWords Words => Feedback ?? WinTools.Core.Feedback.FeedbackWords.From(Label);

    /// <summary>Whether this command applies to every file of a selection of these kinds.</summary>
    public bool AppliesTo(IReadOnlyCollection<FileKind> kinds, int count) =>
        count >= Input.MinCount && count <= Input.MaxCount && kinds.All(Input.Accepts);
}
