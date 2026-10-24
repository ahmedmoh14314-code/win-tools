using System.Diagnostics;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;

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
/// <item>The search has a deadline (<see cref="SearchBudget"/>), well inside the host's 60 s job timeout even when four
/// workers share the processor: once it passes, the search stops and the best copy found so far is used (it has passed
/// every gate, so only some saving is lost). With none found yet, the job fails with a clear message, never as
/// "already optimized" (which would be a claim the search did not establish).</item>
/// </list>
/// The gate values were set from measurements on the generated corpus (docs/performance/2026-10-23-compression.md).
/// </summary>
internal static class LossyCompression
{
    /// <summary>A copy that saves less than this is not worth a second file.</summary>
    public const double MinimumSaving = 0.08;

    /// <summary>How long the quality search may run before it settles for the best copy found so far.</summary>
    public static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(25);

    private static readonly int[] Ladder = [95, 93, 90, 88, 85, 82, 80, 75, 70, 65, 60, 55, 50];

    internal sealed record Preset(CompressLevel Level, int MinQuality, double Luma, double WorstLuma, double Chroma, double MeanDeltaE, double DeltaE99, double Edge,
        double WorstTileLuma, double WorstTileDeltaE)
    {
        public bool Accepts(FidelityReport f) =>
            f.LumaSsim >= Luma && f.WorstLumaSsim >= WorstLuma && f.ChromaSsim >= Chroma
            && f.MeanDeltaE <= MeanDeltaE && f.DeltaE99 <= DeltaE99 && f.EdgeSsim >= Edge
            && f.WorstTileLumaSsim >= WorstTileLuma && f.WorstTileDeltaE <= WorstTileDeltaE;
    }

    /// <summary>High: no visible difference. Balanced: differences only on close comparison. Smallest: visible on close
    /// inspection, never damaged.</summary>
    public static Preset For(CompressLevel level) => level switch
    {
        CompressLevel.High => new(level, MinQuality: 85, Luma: 0.990, WorstLuma: 0.970, Chroma: 0.993, MeanDeltaE: 1.0, DeltaE99: 4.0, Edge: 0.975, WorstTileLuma: 0.975, WorstTileDeltaE: 3.0),
        CompressLevel.Balanced => new(level, MinQuality: 65, Luma: 0.980, WorstLuma: 0.930, Chroma: 0.985, MeanDeltaE: 1.5, DeltaE99: 6.0, Edge: 0.950, WorstTileLuma: 0.950, WorstTileDeltaE: 5.0),
        CompressLevel.Small => new(level, MinQuality: 50, Luma: 0.960, WorstLuma: 0.880, Chroma: 0.970, MeanDeltaE: 2.5, DeltaE99: 10.0, Edge: 0.900, WorstTileLuma: 0.900, WorstTileDeltaE: 7.0),
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Lossless is not a lossy preset."),
    };

    public sealed record Candidate(byte[] Bytes, int Quality, bool FullChroma, FidelityReport Fidelity);

    /// <summary>The smallest acceptable copy, or null when none saves <see cref="MinimumSaving"/>.</summary>
    public static Candidate? Smallest(RasterImage image, ImageFormat format, CompressLevel level, byte[] input, CancellationToken cancel = default)
    {
        var started = Stopwatch.GetTimestamp();
        return Smallest(image, format, level, input, () => Stopwatch.GetElapsedTime(started), cancel);
    }

    /// <summary>The test seam: <paramref name="elapsed"/> is the time the search has taken so far.</summary>
    internal static Candidate? Smallest(RasterImage image, ImageFormat format, CompressLevel level, byte[] input, Func<TimeSpan> elapsed, CancellationToken cancel = default)
    {
        var preset = For(level);
        var ladder = Ladder.Where(q => q >= preset.MinQuality).ToArray();
        bool[] modes = format == ImageFormat.Jpeg && Metadata.IsFullChromaJpeg(input) ? [true, false] : [false];
        var largestUseful = (long)Math.Floor(input.LongLength * (1 - MinimumSaving));
        Candidate? best = null;
        var outOfTime = false;
        foreach (var fullChroma in modes)
        {
            var (c, finished) = Search(image, format, preset, ladder, fullChroma, largestUseful, elapsed, cancel);
            if (c is not null && (best is null || c.Bytes.Length < best.Bytes.Length)) best = c;
            if (!finished) { outOfTime = true; break; }
        }
        if (best is null && outOfTime)
            throw new ImagingException(ErrorCode.WorkerTimeout,
                $"Compress could not find a smaller copy within {SearchBudget.TotalSeconds:0} s for an image this large; no copy was written.");
        return best;
    }

    /// <summary>The lowest quality on the ladder that passes the gates and is small enough; and whether the search finished
    /// before the deadline.</summary>
    private static (Candidate? Found, bool Finished) Search(RasterImage image, ImageFormat format, Preset preset, int[] ladder, bool fullChroma, long largestUseful,
        Func<TimeSpan> elapsed, CancellationToken cancel)
    {
        var encoded = new byte[ladder.Length][];
        var candidates = new Candidate?[ladder.Length];
        byte[] Bytes(int i) => encoded[i] ??= Encode(image, format, ladder[i], fullChroma);
        bool Passes(int i)
        {
            cancel.ThrowIfCancellationRequested();
            var decoded = ImagePipeline.Decode(Bytes(i), format).Image;
            var fidelity = Fidelity.Compare(image, decoded);
            if (!preset.Accepts(fidelity) || !SameAlpha(image, decoded)) return false;
            candidates[i] = new Candidate(Bytes(i), ladder[i], fullChroma, fidelity);
            return true;
        }
        var chosen = Choose(ladder.Length, i => Bytes(i).LongLength <= largestUseful, Passes, () => elapsed() >= SearchBudget, out var finished);
        return (chosen is { } c ? candidates[c] : null, finished);
    }

    /// <summary>
    /// The search over ladder indexes (0 is the highest quality, larger indexes encode smaller): the largest index that
    /// is small enough and passes. Bisection, because a candidate that is too big leaves only lower qualities, and pass
    /// or fail is close to monotonic in quality. <paramref name="finished"/> is false when the deadline cut it short.
    /// </summary>
    internal static int? Choose(int count, Func<int, bool> smallEnough, Func<int, bool> passes, Func<bool> outOfTime, out bool finished)
    {
        int? best = null;
        int lo = 0, hi = count - 1;
        finished = false;
        while (lo <= hi)
        {
            if (outOfTime()) return best;
            var mid = (lo + hi) / 2;
            if (!smallEnough(mid)) { lo = mid + 1; continue; } // too big: only a lower quality can help
            if (passes(mid)) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        finished = true;
        return best;
    }

    /// <summary>Fidelity is measured on white, where a change in transparency alone would not show: alpha must not change.</summary>
    private static bool SameAlpha(RasterImage a, RasterImage b)
    {
        if (!a.HasTransparency()) return !b.HasTransparency();
        for (var i = 3; i < a.Bgra.Length; i += 4)
            if (a.Bgra[i] != b.Bgra[i]) return false;
        return true;
    }

    private static byte[] Encode(RasterImage image, ImageFormat format, int quality, bool fullChroma) => format switch
    {
        ImageFormat.Jpeg => WicCodec.Encode(image, format, quality / 100f, fullChroma),
        ImageFormat.WebP => SkiaCodec.EncodeWebP(image, quality, lossless: false),
        _ => throw new ImagingException(WinTools.Core.Jobs.ErrorCode.UnsupportedOperation, $"No lossy compression for {format}."),
    };
}
