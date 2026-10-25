using WinTools.Core.Feedback;
using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

/// <summary>
/// The PDF module (V2 PR 2.1 and 2.3, ADR-0022): structural commands that qpdf performs in the PDF worker. PDF info
/// reads the document's facts; Merge joins two or more PDFs in the order Explorer gave them; Split writes every page as
/// its own PDF into one new folder; Rotate turns every page. Optimize rewrites the file losslessly (object streams,
/// recompressed streams) and keeps the result only when it is smaller; Fast web view linearizes it; Repair rewrites a
/// damaged file's structure, best effort. No command renders, runs or fetches anything in the document.
/// </summary>
public static class PdfCapabilities
{
    public const string Module = "pdf";
    public const string InfoId = "pdf.info", MergeId = "pdf.merge", SplitId = "pdf.split";
    public const string RotateRightId = "pdf.rotate-right", RotateLeftId = "pdf.rotate-left", Rotate180Id = "pdf.rotate-180";
    public const string OptimizeId = "pdf.optimize", LinearizeId = "pdf.linearize", RepairId = "pdf.repair";

    /// <summary>The least an optimized copy must save to be kept: otherwise the PDF is reported as already optimized.</summary>
    public const double MinOptimizeSaving = 0.01;
    public const long MinOptimizeSavingBytes = 1024;

    /// <summary>The most files Merge joins at once, and the most pages Split writes (each page is a file).</summary>
    public const int MaxMergeInputs = 200, MaxSplitPages = 2000;

    /// <summary>Organize is the PDF module's own; Rotate and Tools are shared with the image module (the first declaration wins).</summary>
    public static IReadOnlyList<CapabilityCategory> Categories { get; } =
    [
        new("organize", "Organize", "pages", 15),
        new("optimize", "Optimize", "compress", 25),
        new("rotate", "Rotate", "rotate", 40),
        new("tools", "Tools", "tools", 90),
    ];

    private static readonly IReadOnlySet<string> PdfOnly = new HashSet<string>(StringComparer.Ordinal) { FileKinds.Pdf.Id };

    private static FeedbackWords Words(string working, string verb, string done) => new(working, verb, done, "Nothing needed doing", "PDF", "PDFs");

    public static IReadOnlyList<CapabilityDescriptor> Catalog { get; } =
    [
        new(MergeId, "Merge", "organize", Module, new InputRule(PdfOnly, MinCount: 2, MaxCount: MaxMergeInputs), CapabilityShape.Selection, CapabilityOutput.File,
            LongRunning: true, Feedback: Words("Merging", "merge", "Merged")),
        new(SplitId, "Split into pages", "organize", Module, new InputRule(PdfOnly), CapabilityShape.PerFile, CapabilityOutput.Folder,
            LongRunning: true, Feedback: Words("Splitting", "split", "Split into pages")),
        new(RotateLeftId, "Left", "rotate", Module, new InputRule(PdfOnly), CapabilityShape.PerFile, CapabilityOutput.File,
            LongRunning: true, Feedback: Words("Rotating", "rotate", "Rotated left")),
        new(RotateRightId, "Right", "rotate", Module, new InputRule(PdfOnly), CapabilityShape.PerFile, CapabilityOutput.File,
            LongRunning: true, Feedback: Words("Rotating", "rotate", "Rotated right")),
        new(Rotate180Id, "180°", "rotate", Module, new InputRule(PdfOnly), CapabilityShape.PerFile, CapabilityOutput.File,
            LongRunning: true, Feedback: Words("Rotating", "rotate", "Rotated 180°")),
        new(OptimizeId, "Smaller (lossless)", "optimize", Module, new InputRule(PdfOnly), CapabilityShape.PerFile, CapabilityOutput.File,
            LongRunning: true, Feedback: new("Optimizing", "optimize", "Optimized without loss", "Already optimized", "PDF", "PDFs", Savings: true)),
        new(LinearizeId, "Fast web view", "optimize", Module, new InputRule(PdfOnly), CapabilityShape.PerFile, CapabilityOutput.File,
            LongRunning: true, Feedback: new("Preparing", "prepare", "Ready for fast web view", "Already set up for fast web view", "PDF", "PDFs")),
        new(RepairId, "Repair", "optimize", Module, new InputRule(PdfOnly), CapabilityShape.PerFile, CapabilityOutput.File,
            LongRunning: true, Feedback: new("Repairing", "repair", "Repaired (best effort: check the pages)", "No damage found", "PDF", "PDFs")),
        new(InfoId, "PDF info", "tools", Module, new InputRule(PdfOnly), CapabilityShape.PerFile, CapabilityOutput.Facts,
            LongRunning: false, Feedback: Words("Reading", "read", "Read")),
    ];

    /// <summary>The text added to an output's name ("report (merged).pdf"), or the folder's for Split ("report (pages)").</summary>
    public static string? NameSuffix(string capabilityId) => capabilityId switch
    {
        MergeId => "merged",
        SplitId => "pages",
        RotateRightId => "rotated right",
        RotateLeftId => "rotated left",
        Rotate180Id => "rotated 180",
        OptimizeId => "optimized",
        LinearizeId => "fast web view",
        RepairId => "repaired",
        _ => null,
    };

    /// <summary>The page rotation of a Rotate command, in qpdf's terms (+90 is clockwise), or null.</summary>
    public static string? RotationOf(string capabilityId) => capabilityId switch
    {
        RotateRightId => "+90",
        RotateLeftId => "-90",
        Rotate180Id => "180",
        _ => null,
    };

    /// <summary>
    /// The name of page <paramref name="page"/> of <paramref name="pages"/> inside Split's folder: the source's name and
    /// the page number, zero-padded so the pages sort in order ("report - page 007.pdf").
    /// </summary>
    public static string PageFileName(string sourcePath, int page, int pages)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, pages);
        var digits = pages.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        return $"{Path.GetFileNameWithoutExtension(sourcePath)} - page {page.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0')}.pdf";
    }
}
