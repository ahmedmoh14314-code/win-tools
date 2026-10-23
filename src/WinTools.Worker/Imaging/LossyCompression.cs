using WinTools.Core.Capabilities;
using WinTools.Core.Files;

namespace WinTools.Worker.Imaging;

/// <summary>
/// Lossy Compress for JPEG and WebP (ADR-0017). Each preset is a promise about how the copy looks, not a fixed encoder
/// quality: the smallest encoding on a bounded quality ladder whose <see cref="FidelityReport"/> passes every gate of
/// the preset, written only if it saves at least <see cref="MinimumSaving"/>; otherwise the file is reported as already
/// optimised and nothing is written.
/// <list type="bullet">
/// <item>The ladder is searched by bisection (pass/fail is close to monotonic in quality); whatever is chosen has
/// itself passed every gate, so a non-monotonic step can only cost some saving, never quality.</item>
/// <item>A JPEG whose colour is stored at full resolution (4:4:4) is tried at 4:4:4 and at 4:2:0, and the chroma gates
/// decide whether halving the colour resolution is acceptable for the preset; a subsampled source stays subsampled.</item>
/// <item>A candidate that would not save the minimum is not measured: a higher quality could not save more.</item>
/// </list>
/// The gate values were set from measurements on the generated corpus (docs/performance/2026-10-23-compression.md).
/// </summary>
internal static class LossyCompression
{
    /// <summary>A copy that saves less than this is not worth a second file.</summary>
    public const double MinimumSaving = 0.08;

    private static readonly int[] Ladder = [95, 93, 90, 88, 85, 82, 80, 75, 70, 65, 60, 55, 50];

    internal sealed record Preset(CompressLevel Level, int MinQuality, double Luma, double WorstLuma, double Chroma, double MeanDeltaE, double DeltaE99, double Edge)
    {
        public bool Accepts(FidelityReport f) =>
            f.LumaSsim >= Luma && f.WorstLumaSsim >= WorstLuma && f.ChromaSsim >= Chroma
            && f.MeanDeltaE <= MeanDeltaE && f.DeltaE99 <= DeltaE99 && f.EdgeSsim >= Edge;
    }

    /// <summary>High: no visible difference. Balanced: differences only on close comparison. Smallest: visible on close
    /// inspection, never damaged.</summary>
    public static Preset For(CompressLevel level) => level switch
    {
        CompressLevel.High => new(level, MinQuality: 85, Luma: 0.990, WorstLuma: 0.970, Chroma: 0.993, MeanDeltaE: 1.0, DeltaE99: 4.0, Edge: 0.975),
        CompressLevel.Balanced => new(level, MinQuality: 65, Luma: 0.980, WorstLuma: 0.930, Chroma: 0.985, MeanDeltaE: 1.5, DeltaE99: 6.0, Edge: 0.950),
        CompressLevel.Small => new(level, MinQuality: 50, Luma: 0.960, WorstLuma: 0.880, Chroma: 0.970, MeanDeltaE: 2.5, DeltaE99: 10.0, Edge: 0.900),
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Lossless is not a lossy preset."),
    };

    public sealed record Candidate(byte[] Bytes, int Quality, bool FullChroma, FidelityReport Fidelity);

    /// <summary>The smallest acceptable copy, or null when none saves <see cref="MinimumSaving"/>.</summary>
    public static Candidate? Smallest(RasterImage image, ImageFormat format, CompressLevel level, byte[] input, CancellationToken cancel = default)
    {
        var preset = For(level);
        var ladder = Ladder.Where(q => q >= preset.MinQuality).ToArray();
        bool[] modes = format == ImageFormat.Jpeg && Metadata.IsFullChromaJpeg(input) ? [true, false] : [false];
        var largestUseful = (long)Math.Floor(input.LongLength * (1 - MinimumSaving));
        Candidate? best = null;
        foreach (var fullChroma in modes)
            if (Search(image, format, preset, ladder, fullChroma, largestUseful, cancel) is { } c && (best is null || c.Bytes.Length < best.Bytes.Length))
                best = c;
        return best;
    }

    /// <summary>The lowest quality on the ladder that passes the gates and is small enough, by bisection.</summary>
    private static Candidate? Search(RasterImage image, ImageFormat format, Preset preset, int[] ladder, bool fullChroma, long largestUseful, CancellationToken cancel)
    {
        Candidate? found = null;
        int lo = 0, hi = ladder.Length - 1;
        while (lo <= hi)
        {
            cancel.ThrowIfCancellationRequested();
            var mid = (lo + hi) / 2;
            var bytes = Encode(image, format, ladder[mid], fullChroma);
            if (bytes.LongLength > largestUseful) { lo = mid + 1; continue; } // too big: only a lower quality can help
            var fidelity = Fidelity.Compare(image, ImagePipeline.Decode(bytes, format).Image);
            if (preset.Accepts(fidelity))
            {
                found = new Candidate(bytes, ladder[mid], fullChroma, fidelity);
                lo = mid + 1;
            }
            else hi = mid - 1;
        }
        return found;
    }

    private static byte[] Encode(RasterImage image, ImageFormat format, int quality, bool fullChroma) => format switch
    {
        ImageFormat.Jpeg => WicCodec.Encode(image, format, quality / 100f, fullChroma),
        ImageFormat.WebP => SkiaCodec.EncodeWebP(image, quality, lossless: false),
        _ => throw new ImagingException(WinTools.Core.Jobs.ErrorCode.UnsupportedOperation, $"No lossy compression for {format}."),
    };
}
