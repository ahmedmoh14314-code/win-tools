namespace WinTools.Core.Capabilities;

/// <summary>
/// What the public release offers: a converter for images, PDF and Office documents, and video (owner decision,
/// 2026-11-11). Every other command stays in the code and in <see cref="CapabilityResolver.For"/> for later versions,
/// but the wheel, Home and Features take <see cref="CapabilityResolver.Public"/>, which offers only these. The host
/// runs only a command the wheel offered, so a hidden command cannot be reached from another menu either.
/// </summary>
public static class PublicRelease
{
    public const string Images = "images", PdfOffice = "pdf-office", Video = "video";

    /// <summary>The three families in the order Home and Features show them, each with its commands in order.</summary>
    public static IReadOnlyList<(string Family, IReadOnlyList<string> Ids)> Families { get; } =
    [
        (Images,
        [
            "convert.to-jpeg", "convert.to-png", "convert.to-webp", "convert.to-bmp", "convert.to-tiff",
            "compress.high", "compress.balanced", "compress.small", "compress.lossless",
            "resize.percent-25", "resize.percent-50", "resize.percent-75", "resize.box-1920x1080", "resize.box-1280x720",
            PdfRenderCapabilities.ImagesToPdfId,
        ]),
        (PdfOffice,
        [
            OfficeCapabilities.ToPdfId, OfficeCapabilities.PdfToWordId, PdfRenderCapabilities.ToPowerPointId, PdfRenderCapabilities.ToExcelId,
            "pdfrender.to-jpg", "pdfrender.to-png", PdfRenderCapabilities.ImagesToPdfId,
            "pdf.merge", "pdf.split", PdfCapabilities.OptimizeId, PdfCapabilities.RotateRightId, "pdf.rotate-left",
        ]),
        (Video,
        [
            "media.video-to-mp4", "media.video-to-webm", "media.video-to-mkv", "media.video-to-mov",
            MediaCapabilities.CompressVideo720Id, MediaCapabilities.CompressVideoId, MediaCapabilities.CompressVideoHighId,
        ]),
    ];

    private static readonly HashSet<string> Allowed = Families.SelectMany(f => f.Ids).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether the public release offers a command: one on the list, or the exact image size chosen in Settings
    /// (<paramref name="customResizeId"/>, an id made from the user's numbers, so it cannot be listed).
    /// </summary>
    public static bool Offers(CapabilityDescriptor capability, string? customResizeId = null) =>
        Allowed.Contains(capability.Id) || (customResizeId is not null && capability.Id == customResizeId);
}
