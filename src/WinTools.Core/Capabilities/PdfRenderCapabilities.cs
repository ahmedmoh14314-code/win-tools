using WinTools.Core.Feedback;
using WinTools.Core.Files;
using WinTools.Core.Output;

namespace WinTools.Core.Capabilities;

/// <summary>
/// PDF to images and text, and images to PDF (Tangerine parity, ADR-0029), run by the image worker with the bundled
/// PDFium (no JavaScript, no XFA):
/// <list type="bullet">
/// <item>On a PDF, Convert: To PNG and To JPG (each page at <see cref="RenderDpi"/> dpi; one page gives a file, more a
/// folder), To TXT and To MD, Markdown (the text PDFium reads, page by page; a scan has none).</item>
/// <item>On images, Convert: To PDF (one page per image, in name order, each the size of its picture).</item>
/// </list>
/// Every result is new, next to the source or in the output folder, never over an existing file.
/// </summary>
public static class PdfRenderCapabilities
{
    public const string Module = "pdfrender";
    public const string ToPngId = "pdfrender.to-png", ToJpgId = "pdfrender.to-jpg", ToTextId = "pdfrender.to-txt", ToMarkdownId = "pdfrender.to-md";
    public const string ImagesToPdfId = "pdfrender.images-to-pdf";

    /// <summary>The resolution pages are rendered at: sharp on screen and in print, a few MB a page.</summary>
    public const int RenderDpi = 150;

    /// <summary>Most pages rendered or read from one PDF, and most images in one new PDF.</summary>
    public const int MaxPages = 500, MaxImages = 200;

    /// <summary>The largest PDF read (it is read whole: PDFium needs random access).</summary>
    public const long MaxPdfBytes = 512L << 20;

    public static IReadOnlyList<CapabilityCategory> Categories { get; } = [new("convert", "Convert", "convert", 10)];

    private static readonly IReadOnlySet<string> PdfOnly = new HashSet<string>(StringComparer.Ordinal) { FileKinds.Pdf.Id };
    private static readonly IReadOnlySet<string> Images = FileKinds.All.Select(k => k.Id).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyList<CapabilityDescriptor> Catalog { get; } =
    [
        new(ToPngId, "To PNG", "convert", Module, new InputRule(PdfOnly), CapabilityShape.PerFile, CapabilityOutput.Folder, LongRunning: true,
            Feedback: new("Converting", "convert", "Pages saved as PNG", "Nothing needed doing", "PDF", "PDFs")),
        new(ToJpgId, "To JPG", "convert", Module, new InputRule(PdfOnly), CapabilityShape.PerFile, CapabilityOutput.Folder, LongRunning: true,
            Feedback: new("Converting", "convert", "Pages saved as JPG", "Nothing needed doing", "PDF", "PDFs")),
        new(ToTextId, "To TXT", "convert", Module, new InputRule(PdfOnly), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Reading the text of", "read the text of", "Text saved", "The PDF has no text (a scan has only pictures)", "PDF", "PDFs")),
        new(ToMarkdownId, "To MD", "convert", Module, new InputRule(PdfOnly), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Reading the text of", "read the text of", "Saved as Markdown", "The PDF has no text (a scan has only pictures)", "PDF", "PDFs")),
        new(ImagesToPdfId, "To PDF", "convert", Module, new InputRule(Images, MaxCount: MaxImages), CapabilityShape.Selection, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Making a PDF of", "make a PDF of", "PDF made", "Nothing needed doing", "image", "images")),
    ];

    /// <summary>The image format of a PDF-to-image command, or null.</summary>
    public static ImageFormat? PageFormatOf(string capabilityId) => capabilityId switch
    {
        ToPngId => ImageFormat.Png,
        ToJpgId => ImageFormat.Jpeg,
        _ => null,
    };

    /// <summary>The images of Images to PDF in page order: by name, numbers as numbers ("scan 2" before "scan 10").</summary>
    public static IReadOnlyList<string> PageOrder(IReadOnlyList<string> images) => PdfCapabilities.MergeOrder(images);

    /// <summary>
    /// The names to try for a command's output: "report.png" for a one-page PDF, the folder "report (PNG)" for more,
    /// "report.txt", "report.md", and "photo.pdf" after the first image.
    /// </summary>
    public static IEnumerable<string> Candidates(string capabilityId, string source, string? outputDirectory, bool onePage = false) => capabilityId switch
    {
        ToPngId or ToJpgId when onePage => OutputNaming.CandidatesWithExtension(source, capabilityId == ToPngId ? ".png" : ".jpg", null, outputDirectory),
        ToPngId => OutputNaming.FolderCandidates(source, "PNG", outputDirectory),
        ToJpgId => OutputNaming.FolderCandidates(source, "JPG", outputDirectory),
        ToTextId => OutputNaming.CandidatesWithExtension(source, ".txt", null, outputDirectory),
        ToMarkdownId => OutputNaming.CandidatesWithExtension(source, ".md", null, outputDirectory),
        ImagesToPdfId => OutputNaming.CandidatesWithExtension(source, ".pdf", null, outputDirectory),
        _ => throw new ArgumentException($"{capabilityId} is not a {Module} command.", nameof(capabilityId)),
    };
}
