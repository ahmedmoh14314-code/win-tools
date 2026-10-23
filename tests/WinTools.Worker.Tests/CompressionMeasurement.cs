using WinTools.Core.Jobs;
using WinTools.Worker.Imaging;
using Xunit.Abstractions;

namespace WinTools.Worker.Tests;

/// <summary>Prints what Compress does to every corpus file at every level: bytes, saving and SSIM. A measurement, not a check.</summary>
public sealed class CompressionMeasurement(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolder _dir = new();
    private static int _seq;

    [Fact]
    [Trait("Category", "Measurement")]
    public void Measure_compress_on_the_corpus()
    {
        output.WriteLine($"{"file",-26} {"level",-9} {"source",10} {"output",10} {"saved",7} {"ssim",7}  result");
        foreach (var s in CompressionCorpus.All())
        {
            var src = _dir.Write(s.Name, s.Bytes);
            var original = ImagePipeline.Decode(s.Bytes, s.Format).Image;
            foreach (var level in new[] { "high", "balanced", "small", "lossless" })
            {
                var r = ConversionJob.Run(new ConversionRequest($"m{Interlocked.Increment(ref _seq)}", $"compress.{level}", src));
                if (r.Success && r.OutputPath is { } o)
                {
                    var bytes = File.ReadAllBytes(o);
                    var ssim = Ssim.Luma(original, ImagePipeline.Decode(bytes, s.Format).Image);
                    output.WriteLine($"{s.Name,-26} {level,-9} {s.Bytes.Length,10} {bytes.Length,10} {100.0 * (s.Bytes.Length - bytes.Length) / s.Bytes.Length,6:0.0}% {ssim,7:0.0000}  written");
                    File.Delete(o);
                }
                else output.WriteLine($"{s.Name,-26} {level,-9} {s.Bytes.Length,10} {"-",10} {"-",7} {"-",7}  {r.Error}");
            }
        }
    }

    [Fact]
    [Trait("Category", "Measurement")]
    public void Measure_quality_curves()
    {
        foreach (var s in CompressionCorpus.All().Where(x => x.Format is WinTools.Core.Files.ImageFormat.Jpeg or WinTools.Core.Files.ImageFormat.WebP))
        {
            var original = ImagePipeline.Decode(s.Bytes, s.Format).Image;
            var line = new System.Text.StringBuilder($"{s.Name,-26}");
            foreach (var q in new[] { 95, 92, 90, 88, 85, 82, 80, 75, 70, 65, 60, 50, 40 })
            {
                var bytes = s.Format == WinTools.Core.Files.ImageFormat.Jpeg ? WicCodec.Encode(original, s.Format, q / 100f) : SkiaCodec.EncodeWebP(original, q, lossless: false);
                var ssim = Ssim.Luma(original, ImagePipeline.Decode(bytes, s.Format).Image);
                line.Append($" q{q}:{100.0 * (s.Bytes.Length - bytes.Length) / s.Bytes.Length:0}%/{ssim:0.000}");
            }
            output.WriteLine(line.ToString());
        }
    }

    [Fact]
    [Trait("Category", "Measurement")]
    public void Measure_camera_444()
    {
        var s = CompressionCorpus.All().Single(x => x.Name == "camera.jpg");
        var original = ImagePipeline.Decode(s.Bytes, s.Format).Image;
        foreach (var q in new[] { 96, 95, 94, 93, 92, 90 })
        {
            var bytes = WicCodec.Encode(original, s.Format, q / 100f, fullChroma: true);
            output.WriteLine($"camera 4:4:4 q{q}: saved {100.0 * (s.Bytes.Length - bytes.Length) / s.Bytes.Length:0.0}% ssim {Ssim.Luma(original, ImagePipeline.Decode(bytes, s.Format).Image):0.0000}");
        }
    }

    public void Dispose() => _dir.Dispose();
}
