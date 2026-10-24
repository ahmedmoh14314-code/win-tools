using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

/// <summary>Whether a command runs once per file (each file its own job item) or once for the whole selection.</summary>
public enum CapabilityShape { PerFile, Selection }

/// <summary>What a command leaves behind.</summary>
public enum CapabilityOutput { File, Files, Folder, Facts, Clipboard }

/// <summary>
/// Which selections a command accepts: every file's kind must pass <paramref name="Accepts"/>, and the count must be
/// within bounds. A selection of several kinds is allowed as long as each kind passes.
/// </summary>
public sealed record InputRule(Func<FileKind, bool> Accepts, int MinCount = 1, int MaxCount = CapabilityResolver.MaxFiles);

/// <summary>
/// One command the radial menu can offer, as the resolver and the menu see it, whatever module provides it (ADR-0007,
/// V2 plan §5). The module keeps its own description of the work in <see cref="Payload"/> (the image module: its
/// <see cref="Operation"/>); nothing outside the module looks inside it.
/// </summary>
/// <param name="Id">Stable id, also the worker's operation id ("convert.to-png").</param>
/// <param name="Category">The menu category's id ("convert", "compress", ...).</param>
/// <param name="Module">The module that owns the command ("images").</param>
/// <param name="LongRunning">The job shows progress rather than finishing at once.</param>
public sealed record CapabilityDescriptor(string Id, string Label, string Category, string Module, InputRule Input,
    CapabilityShape Shape, CapabilityOutput Produces, bool LongRunning, object? Payload = null)
{
    /// <summary>Whether this command applies to every file of a selection of these kinds.</summary>
    public bool AppliesTo(IReadOnlyCollection<FileKind> kinds, int count) =>
        count >= Input.MinCount && count <= Input.MaxCount && kinds.All(Input.Accepts);
}
