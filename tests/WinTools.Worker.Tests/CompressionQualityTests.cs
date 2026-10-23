using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Worker.Imaging;

namespace WinTools.Worker.Tests;

/// <summary>
/// Release blocker 1 (owner field report, 2026-10-23): Compress often saved only a few percent on real photographs, wrote
/// insignificant copies, and had no quality floor. These tests pin the redesigned semantics (ADR-0017) on the generated
/// corpus (<see cref="CompressionCorpus"/>), measured with an SSIM written independently of the product:
/// <list type="bullet">
/// <item>High: no visible difference (SSIM ≥ 0.990). Balanced: SSIM ≥ 0.980. Smallest: SSIM ≥ 0.960.</item>
/// <item>A copy is written only if it saves at least 8 %; otherwise "nothing to do" (already optimised).</item>
/// <item>Dimensions never change; the source is never touched; the output is never larger; PNG stays lossless.</item>
/// </list>
/// The expected savings are not universal promises: they are what the quality floors allow on these corpus files, as
/// measured (docs/performance/2026-10-23-compression.md), with a margin.
/// </summary>
public sealed class CompressionQualityTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private static int _seq;

    private static readonly Dictionary<string, double> Floor = new() { ["high"] = 0.990, ["balanced"] = 0.980, ["small"] = 0.960 };
    private const double MinimumSaving = 0.08;

    private static readonly IReadOnlyList<CompressionCorpus.Sample> Corpus = CompressionCorpus.All();

    private sealed record Outcome(ConversionResult Result, long Source, long Output, double Saving, double Ssim, int Width, int Height, bool SourceIntact, byte[] Bytes);

    private Outcome Compress(string file, string level)
    {
        var s = Corpus.Single(c => c.Name == file);
        var src = Path.Combine(_dir.Path, $"{level}-{s.Name}");
        File.WriteAllBytes(src, s.Bytes);
        var r = ConversionJob.Run(new ConversionRequest($"c{Interlocked.Increment(ref _seq)}", $"compress.{level}", src));
        var intact = File.ReadAllBytes(src).AsSpan().SequenceEqual(s.Bytes);
        if (!r.Success || r.OutputPath is null) return new(r, s.Bytes.Length, 0, 0, 0, 0, 0, intact, []);
        var bytes = File.ReadAllBytes(r.OutputPath);
        var original = ImagePipeline.Decode(s.Bytes, s.Format).Image;
        var output = ImagePipeline.Decode(bytes, s.Format).Image;
        return new(r, s.Bytes.Length, bytes.Length, 1.0 - (double)bytes.Length / s.Bytes.Length, Ssim.Luma(original, output), output.Width, output.Height, intact, bytes);
    }

    public static TheoryData<string, string> LossyCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var f in new[] { "phone.jpg", "camera.jpg", "moderate.jpg", "noisy.jpg", "screenshot.jpg", "photo-lossy.webp", "screenshot-lossless.webp" })
            foreach (var level in new[] { "high", "balanced", "small" }) data.Add(f, level);
        return data;
    }

    [Theory]
    [MemberData(nameof(LossyCases))]
    public void A_written_copy_keeps_the_presets_quality_floor_and_saves_meaningfully(string file, string level)
    {
        var o = Compress(file, level);
        Assert.True(o.SourceIntact, "the source must be byte-identical");
        if (!o.Result.Success)
        {
            Assert.Equal(ErrorCode.NothingToDo, o.Result.Error); // never a failure: either a good copy or "already optimised"
            return;
        }
        Assert.True(o.Ssim >= Floor[level], $"{file} {level}: SSIM {o.Ssim:0.0000} below the floor {Floor[level]}");
        Assert.True(o.Saving >= MinimumSaving, $"{file} {level}: saves only {o.Saving:P1}, an insignificant copy");
        Assert.Equal((CompressionCorpus.Width, CompressionCorpus.Height), (o.Width, o.Height));
        var format = Corpus.Single(c => c.Name == file).Format;
        Assert.Equal(format, FileSignature.Detect(o.Bytes));
        Assert.True(ContainerCheck.IsComplete(o.Bytes, format, out _));
    }

    [Theory]
    // Measured on the corpus at the presets' floors (see the class summary): what each preset can save without visible loss.
    [InlineData("phone.jpg", "high", 0.10)]
    [InlineData("phone.jpg", "balanced", 0.50)]
    [InlineData("phone.jpg", "small", 0.65)]
    [InlineData("camera.jpg", "high", 0.10)]  // 4:4:4 kept: q95 is the lowest at SSIM ≥ 0.99 (13.6 %)
    [InlineData("camera.jpg", "balanced", 0.65)]
    [InlineData("camera.jpg", "small", 0.85)]
    [InlineData("photo-lossy.webp", "high", 0.15)]
    [InlineData("photo-lossy.webp", "balanced", 0.55)]
    // Screenshots: text edges limit JPEG (the worst-region gate). Measured: Balanced 10.8 % (q93), Smallest 27.1 % (q88);
    // Balanced at 30 % (q82) left the worst 1 % of windows at SSIM 0.80, visibly ringing glyphs (ADR-0017).
    [InlineData("screenshot.jpg", "balanced", 0.08)]
    [InlineData("screenshot.jpg", "small", 0.25)]
    public void Typical_photographs_and_screenshots_get_a_meaningful_reduction(string file, string level, double atLeast)
    {
        var o = Compress(file, level);
        Assert.True(o.Result.Success, $"{file} {level}: {o.Result.Error} {o.Result.Message}");
        Assert.True(o.Saving >= atLeast, $"{file} {level}: saved {o.Saving:P1}, expected at least {atLeast:P0} (SSIM {o.Ssim:0.0000})");
    }

    [Theory]
    [InlineData("moderate.jpg", "high")]
    [InlineData("moderate.jpg", "balanced")]
    [InlineData("noisy.jpg", "high")]
    [InlineData("noisy.jpg", "balanced")]
    [InlineData("screenshot-lossless.webp", "balanced")]
    public void Files_that_cannot_shrink_without_visible_loss_are_reported_as_already_optimised(string file, string level)
    {
        var o = Compress(file, level);
        Assert.Equal(ErrorCode.NothingToDo, o.Result.Error);
        Assert.Equal([$"{level}-{file}"], _dir.Names().Where(n => n.StartsWith(level + "-", StringComparison.Ordinal) && n.Contains(Path.GetFileNameWithoutExtension(file), StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public void High_quality_keeps_full_colour_resolution_of_a_4_4_4_jpeg()
    {
        var o = Compress("camera.jpg", "high");
        Assert.True(o.Result.Success, o.Result.Message);
        Assert.Equal((1, 1), JpegSampling.ChromaSubsampling(o.Bytes)); // 4:4:4 in, 4:4:4 out
    }

    [Fact]
    public void Png_compression_stays_lossless_and_skips_an_already_optimised_png()
    {
        var o = Compress("unoptimized.png", "lossless");
        Assert.True(o.Result.Success, o.Result.Message);
        var s = Corpus.Single(c => c.Name == "unoptimized.png");
        Assert.Equal(ImagePipeline.Decode(s.Bytes, ImageFormat.Png).Image.Bgra, ImagePipeline.Decode(o.Bytes, ImageFormat.Png).Image.Bgra);
        Assert.True(o.Saving > 0.9);
        Assert.Equal(ErrorCode.NothingToDo, Compress("optimized.png", "lossless").Result.Error);
    }

    [Fact]
    public void The_summary_says_how_much_was_saved()
    {
        var op = OperationCatalog.Get("compress.balanced");
        var item = new JobItem("photo.jpg", ConversionResult.Ok("1", @"C:\x\photo (compressed).jpg", "0"), 2_480_000, 612_000);
        var m = FeedbackText.For(op, [item]);
        Assert.Contains("2.37 MB → 597.7 KB", m.Detail, StringComparison.Ordinal);
        Assert.Contains("saved 75%", m.Detail, StringComparison.Ordinal);
        var nothing = FeedbackText.For(op, [new JobItem("a.jpg", ConversionResult.Fail("1", ErrorCode.NothingToDo, ""))]);
        Assert.Contains("Already optimized", nothing.Title + " " + nothing.Detail, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _dir.Dispose();
}

/// <summary>Reads a baseline or progressive JPEG's chroma subsampling factors (horizontal, vertical) from its SOF segment.</summary>
internal static class JpegSampling
{
    public static (int H, int V) ChromaSubsampling(byte[] jpeg)
    {
        for (var i = 2; i + 4 < jpeg.Length;)
        {
            if (jpeg[i] != 0xFF) { i++; continue; }
            var marker = jpeg[i + 1];
            var len = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                var components = jpeg[i + 9];
                var luma = jpeg[i + 11];
                var chroma = components > 1 ? jpeg[i + 14] : luma;
                return ((luma >> 4) / Math.Max(1, chroma >> 4), (luma & 15) / Math.Max(1, chroma & 15));
            }
            i += 2 + len;
        }
        throw new InvalidDataException("No SOF segment.");
    }
}
