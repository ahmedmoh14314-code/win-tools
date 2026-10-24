using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Worker.Imaging;

namespace WinTools.Worker.Tests;

/// <summary>
/// Evaluator, PR #12: a 48-megapixel search took up to 13.9 s alone on one machine, against a 60 s job timeout that a
/// slower processor, or four workers sharing one, could reach; a killed worker reports a timeout for work that had in
/// fact found a good copy. The search has a deadline: past it, the best copy found so far is used, and with none found,
/// the job fails clearly instead of claiming the file is already optimised. Time is a fake clock here.
/// </summary>
[Collection(CpuHeavyTests.Name)]
public sealed class CompressionDeadlineTests
{
    private static readonly RasterImage Photo = CompressionCorpus.Photo(seed: 41, width: 800, height: 600);
    private static readonly byte[] Phone = WicCodec.Encode(Photo, ImageFormat.Jpeg, 0.93f);
    private static RasterImage Decoded => ImagePipeline.Decode(Phone, ImageFormat.Jpeg).Image;

    [Fact]
    public void Past_the_deadline_the_best_copy_found_so_far_is_used_and_it_passed_every_gate()
    {
        var probes = 0;
        var c = LossyCompression.Smallest(Decoded, ImageFormat.Jpeg, CompressLevel.Small, Phone,
            () => probes++ == 0 ? TimeSpan.Zero : LossyCompression.SearchBudget);

        Assert.NotNull(c);
        Assert.Equal(80, c.Quality);                       // the first probe, the middle of the ladder (95..50)
        Assert.True(LossyCompression.For(CompressLevel.Small).Accepts(c.Fidelity));
        var full = LossyCompression.Smallest(Decoded, ImageFormat.Jpeg, CompressLevel.Small, Phone, () => TimeSpan.Zero)!;
        Assert.True(full.Quality < c.Quality && full.Bytes.Length < c.Bytes.Length, "without the deadline the search goes further");
    }

    [Fact]
    public void Past_the_deadline_with_nothing_found_the_job_fails_and_does_not_claim_already_optimized()
    {
        var e = Assert.Throws<ImagingException>(() =>
            LossyCompression.Smallest(Decoded, ImageFormat.Jpeg, CompressLevel.Balanced, Phone, () => LossyCompression.SearchBudget));
        Assert.Equal(ErrorCode.WorkerTimeout, e.Code); // "It took too long and was stopped."
        Assert.DoesNotContain("optimized", e.Message, StringComparison.OrdinalIgnoreCase);
    }
}
