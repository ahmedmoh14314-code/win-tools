using WinTools.Core.Feedback;
using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

/// <summary>
/// The Documents module's first slice (V2 PR 6.1, ADR-0025): Word, Excel and PowerPoint files read in the text worker.
/// Extract text writes the document's text as a new UTF-8 file; Document info shows its properties and warns about
/// macros, external links and embedded objects. Nothing is rendered, run or fetched, and the document is never changed.
/// </summary>
public static class DocumentCapabilities
{
    public const string Module = "documents";
    public const string ExtractTextId = "documents.text", InfoId = "documents.info";

    /// <summary>The largest package read (an ordinary report or deck is far smaller).</summary>
    public const long MaxInputBytes = 200L << 20;

    public static IReadOnlyList<CapabilityCategory> Categories { get; } =
    [
        new("convert", "Convert", "convert", 10),
        new("tools", "Tools", "tools", 90),
    ];

    private static readonly IReadOnlySet<string> Office = new HashSet<string>(StringComparer.Ordinal) { FileKinds.Docx.Id, FileKinds.Xlsx.Id, FileKinds.Pptx.Id };

    public static IReadOnlyList<CapabilityDescriptor> Catalog { get; } =
    [
        new(ExtractTextId, "Extract text", "convert", Module, new InputRule(Office), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Extracting text from", "extract the text of", "Text extracted", "There is no text to extract", "document", "documents")),
        new(InfoId, "Document info", "tools", Module, new InputRule(Office), CapabilityShape.PerFile, CapabilityOutput.Facts, LongRunning: false,
            Feedback: new("Reading", "read", "Read", "Nothing needed doing", "document", "documents")),
    ];

    /// <summary>Extract text's output names: "report (text).txt", "report (text) (1).txt"...</summary>
    public static IEnumerable<string> Candidates(string source, string? outputDirectory) =>
        Output.OutputNaming.CandidatesWithExtension(source, ".txt", "text", outputDirectory);
}
