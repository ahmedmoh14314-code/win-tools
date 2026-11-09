using WinTools.Core.Feedback;
using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

/// <summary>
/// Office documents to PDF through the user's own LibreOffice (ADR-0033). LibreOffice is not shipped, installed or
/// downloaded by WinTools: when it is not found, the command is not on the wheel (<see cref="CapabilityDescriptor.Requires"/>),
/// and the Features page says what it needs. A module of its own so its jobs run one at a time, with a longer limit than
/// the documents module's in-house commands. The text worker runs LibreOffice headless, as a child process, with a
/// throw-away profile that disables macros and blocks linked content.
/// </summary>
public static class OfficeCapabilities
{
    public const string Module = "office";
    public const string ToPdfId = "office.to-pdf";

    /// <summary>A PDF opened by LibreOffice's PDF import and saved as a Word document or a PowerPoint deck.</summary>
    public const string PdfToWordId = "office.pdf-to-word", PdfToPowerPointId = "office.pdf-to-powerpoint";

    /// <summary>The largest document handed to LibreOffice.</summary>
    public const long MaxInputBytes = 200L << 20;

    /// <summary>The largest PDF kept.</summary>
    public const long MaxOutputBytes = 1L << 30;

    public static IReadOnlyList<CapabilityCategory> Categories { get; } = [new("convert", "Convert", "convert", 10)];

    private static readonly IReadOnlySet<string> Inputs = new[]
    {
        FileKinds.Docx, FileKinds.Doc, FileKinds.Rtf, FileKinds.Odt, FileKinds.Xlsx, FileKinds.Xls, FileKinds.Ods,
        FileKinds.Pptx, FileKinds.Ppt, FileKinds.Odp, FileKinds.Text, FileKinds.Csv,
    }.Select(k => k.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>What a failed conversion says, in the module's own words.</summary>
    private static readonly IReadOnlyDictionary<Jobs.ErrorCode, string> OwnReasons = new Dictionary<Jobs.ErrorCode, string>
    {
        [Jobs.ErrorCode.UnsupportedOperation] = "No program on this PC can open this document: install Microsoft Office or LibreOffice (free).",
        [Jobs.ErrorCode.EncodeFailed] = "The document could not be turned into a PDF. Nothing was saved.",
        [Jobs.ErrorCode.OutputValidationFailed] = "The PDF written was not readable. Nothing was saved.",
    };

    /// <summary>What a failed PDF import says.</summary>
    private static readonly IReadOnlyDictionary<Jobs.ErrorCode, string> FromPdfReasons = new Dictionary<Jobs.ErrorCode, string>
    {
        [Jobs.ErrorCode.UnsupportedOperation] = "No program on this PC can open this PDF: install Microsoft Word or LibreOffice (free).",
        [Jobs.ErrorCode.EncodeFailed] = "This PDF could not be converted. Nothing was saved.",
        [Jobs.ErrorCode.OutputValidationFailed] = "The document written was not readable. Nothing was saved.",
    };

    private static readonly InputRule PdfInput = new(new HashSet<string>(StringComparer.Ordinal) { FileKinds.Pdf.Id });

    public static IReadOnlyList<CapabilityDescriptor> Catalog { get; } =
    [
        new(PdfToWordId, "To Word", "convert", Module, PdfInput, CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Converting", "convert", "Converted to Word", "Nothing needed doing", "PDF", "PDFs", Reasons: FromPdfReasons),
            Requires: ExternalPrograms.PdfImporter),
        new(PdfToPowerPointId, "To PowerPoint", "convert", Module, PdfInput, CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Converting", "convert", "Converted to PowerPoint", "Nothing needed doing", "PDF", "PDFs", Reasons: FromPdfReasons),
            Requires: ExternalPrograms.LibreOffice),
        new(ToPdfId, "To PDF", "convert", Module, new InputRule(Inputs), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Converting", "convert", "Converted to PDF", "Nothing needed doing", "document", "documents", Reasons: OwnReasons),
            Requires: ExternalPrograms.AnyOffice),
    ];

    /// <summary>The extension a command writes: ".pdf", ".docx" or ".pptx".</summary>
    public static string OutputExtension(string capabilityId) => capabilityId switch
    {
        PdfToWordId => ".docx",
        PdfToPowerPointId => ".pptx",
        _ => ".pdf",
    };

    /// <summary>A command's output names: "report.pdf", "report (1).pdf"... ("report.docx" for To Word).</summary>
    public static IEnumerable<string> Candidates(string capabilityId, string source, string? outputDirectory) =>
        Output.OutputNaming.CandidatesWithExtension(source, OutputExtension(capabilityId), null, outputDirectory);

    /// <summary>The start of every conversion's private folder, which the host also sweeps when it starts.</summary>
    public const string WorkFolderPrefix = "wintools-office-";

    /// <summary>The private folder of one conversion under the user's temp folder (the copy, the profile, the PDF).</summary>
    public static string WorkFolderName(string requestId) => WorkFolderPrefix + requestId;
}
