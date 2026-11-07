using WinTools.Core.Feedback;
using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

/// <summary>
/// The Documents module's first slice (V2 PR 6.1, ADR-0025): Word, Excel and PowerPoint files read in the text worker.
/// Extract text writes the document's text as a new UTF-8 file; Document info shows its properties and warns about
/// macros, external links and embedded objects. Nothing is rendered, run or fetched, and the document is never changed.
/// Remove properties (V2 PR 6.2) writes a copy whose core, extended and custom properties are empty.
/// ADR-0033 adds conversions done in house: CSV and TSV to Excel, Excel to CSV (one file per sheet with data), and
/// Markdown to HTML (raw HTML never passed through, no script, a page that loads nothing but images).
/// </summary>
public static class DocumentCapabilities
{
    public const string Module = "documents";
    public const string ExtractTextId = "documents.text", InfoId = "documents.info", RemovePropertiesId = "documents.clean";

    /// <summary>The in-house conversions (ADR-0033): CSV/TSV to Excel, Excel to CSV, Markdown to HTML.</summary>
    public const string ToXlsxId = "documents.to-xlsx", ToCsvId = "documents.to-csv", ToHtmlId = "documents.to-html";

    /// <summary>The largest CSV, TSV or Markdown file read (decoded in memory, then written as a workbook or a page).</summary>
    public const long MaxTextInputBytes = 50L << 20;

    /// <summary>The most sheets To CSV writes (each with data becomes one file).</summary>
    public const int MaxCsvSheets = 255;

    /// <summary>
    /// The Content-Security-Policy every page To HTML writes carries, and the host looks for: no script, frame, form,
    /// plug-in or outside style can run or load; images still load when the user opens the page.
    /// </summary>
    public const string HtmlPolicy = "default-src 'none'; img-src *; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <summary>To CSV's folder suffix when a workbook has several sheets with data: "book (CSV)".</summary>
    public const string CsvFolderSuffix = "CSV";

    /// <summary>The largest package read (an ordinary report or deck is far smaller).</summary>
    public const long MaxInputBytes = 200L << 20;

    public static IReadOnlyList<CapabilityCategory> Categories { get; } =
    [
        new("convert", "Convert", "convert", 10),
        new("tools", "Tools", "tools", 11), // Convert, then Tools, then the module's own groups, Actions last (Tangerine's order)
    ];

    private static readonly IReadOnlySet<string> Office = new HashSet<string>(StringComparer.Ordinal) { FileKinds.Docx.Id, FileKinds.Xlsx.Id, FileKinds.Pptx.Id };
    private static readonly IReadOnlySet<string> Tables = new HashSet<string>(StringComparer.Ordinal) { FileKinds.Csv.Id, FileKinds.Tsv.Id };
    private static readonly IReadOnlySet<string> Workbooks = new HashSet<string>(StringComparer.Ordinal) { FileKinds.Xlsx.Id };
    private static readonly IReadOnlySet<string> MarkdownFiles = new HashSet<string>(StringComparer.Ordinal) { FileKinds.Markdown.Id };

    public static IReadOnlyList<CapabilityDescriptor> Catalog { get; } =
    [
        new(ExtractTextId, "Extract text", "convert", Module, new InputRule(Office), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Extracting text from", "extract the text of", "Text extracted", "There is no text to extract", "document", "documents")),
        new(ToXlsxId, "To Excel", "convert", Module, new InputRule(Tables), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Converting", "convert", "Converted to Excel", "The table is empty", "table", "tables")),
        new(ToCsvId, "To CSV", "convert", Module, new InputRule(Workbooks), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Converting", "convert", "Converted to CSV", "No sheet has data", "workbook", "workbooks")),
        new(ToHtmlId, "To HTML", "convert", Module, new InputRule(MarkdownFiles), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Converting", "convert", "Converted to HTML", "The file is empty", "document", "documents")),
        // Remove properties first: its long word fits the first sector of the ring, not the second.
        new(RemovePropertiesId, "Remove properties", "tools", Module, new InputRule(Office), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Removing properties from", "remove the properties of", "Properties removed", "There were no properties to remove", "document", "documents")),
        new(InfoId, "Document info", "tools", Module, new InputRule(Office), CapabilityShape.PerFile, CapabilityOutput.Facts, LongRunning: false,
            Feedback: new("Reading", "read", "Read", "Nothing needed doing", "document", "documents")),
    ];

    /// <summary>Extract text's output names: "report (text).txt", "report (text) (1).txt"...</summary>
    public static IEnumerable<string> Candidates(string source, string? outputDirectory) =>
        Output.OutputNaming.CandidatesWithExtension(source, ".txt", "text", outputDirectory);

    /// <summary>To Excel's output names: "data.xlsx", "data (1).xlsx"...</summary>
    public static IEnumerable<string> XlsxCandidates(string source, string? outputDirectory) =>
        Output.OutputNaming.CandidatesWithExtension(source, ".xlsx", null, outputDirectory);

    /// <summary>To HTML's output names: "notes.html", "notes (1).html"...</summary>
    public static IEnumerable<string> HtmlCandidates(string source, string? outputDirectory) =>
        Output.OutputNaming.CandidatesWithExtension(source, ".html", null, outputDirectory);

    /// <summary>To CSV's output names for a workbook with one sheet of data: "book.csv", "book (1).csv"...</summary>
    public static IEnumerable<string> CsvCandidates(string source, string? outputDirectory) =>
        Output.OutputNaming.CandidatesWithExtension(source, ".csv", null, outputDirectory);

    /// <summary>To CSV's folder names for a workbook with several sheets of data: "book (CSV)", "book (CSV) (1)"...</summary>
    public static IEnumerable<string> CsvFolderCandidates(string source, string? outputDirectory) =>
        Output.OutputNaming.FolderCandidates(source, CsvFolderSuffix, outputDirectory);

    /// <summary>Remove properties' output names, in the source's own type: "report (no properties).docx"...</summary>
    public static IEnumerable<string> CleanCandidates(string source, string? outputDirectory) =>
        Output.OutputNaming.CandidatesWithExtension(source, Path.GetExtension(source).ToLowerInvariant(), "no properties", outputDirectory);
}
