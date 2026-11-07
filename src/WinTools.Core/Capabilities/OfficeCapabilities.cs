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
        [Jobs.ErrorCode.UnsupportedOperation] = "LibreOffice was not found. Install it (it is free), then try again.",
        [Jobs.ErrorCode.EncodeFailed] = "LibreOffice could not turn this document into a PDF. Nothing was saved.",
        [Jobs.ErrorCode.OutputValidationFailed] = "LibreOffice did not produce a readable PDF. Nothing was saved.",
    };

    public static IReadOnlyList<CapabilityDescriptor> Catalog { get; } =
    [
        new(ToPdfId, "To PDF", "convert", Module, new InputRule(Inputs), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Converting", "convert", "Converted to PDF", "Nothing needed doing", "document", "documents", Reasons: OwnReasons),
            Requires: ExternalPrograms.LibreOffice),
    ];

    /// <summary>To PDF's output names: "report.pdf", "report (1).pdf"...</summary>
    public static IEnumerable<string> Candidates(string source, string? outputDirectory) =>
        Output.OutputNaming.CandidatesWithExtension(source, ".pdf", null, outputDirectory);

    /// <summary>The private folder of one conversion under the user's temp folder (the copy, the profile, the PDF).</summary>
    public static string WorkFolderName(string requestId) => $"wintools-office-{requestId}";
}
