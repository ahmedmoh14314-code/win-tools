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

    /// <summary>Every fidelity axis (ADR-0017) across the quality ladder: what each gate would allow, file by file.</summary>
    [Fact]
    [Trait("Category", "Measurement")]
    public void Measure_fidelity_axes()
    {
        output.WriteLine($"{"file",-26} {"enc",-9} {"saved",7} {"ssim",7} {"worst",7} {"chroma",7} {"dE",6} {"dE99",6} {"edge",7} {"ms",5}");
        foreach (var s in CompressionCorpus.All().Where(x => x.Format is WinTools.Core.Files.ImageFormat.Jpeg or WinTools.Core.Files.ImageFormat.WebP))
        {
            var original = ImagePipeline.Decode(s.Bytes, s.Format).Image;
            var encodings = new List<(string Name, Func<byte[]> Encode)>();
            foreach (var q in new[] { 97, 95, 93, 90, 88, 85, 82, 80, 75, 70, 65, 60, 55, 50 })
            {
                if (s.Format == WinTools.Core.Files.ImageFormat.Jpeg)
                {
                    encodings.Add(($"420 q{q}", () => WicCodec.Encode(original, s.Format, q / 100f)));
                    encodings.Add(($"444 q{q}", () => WicCodec.Encode(original, s.Format, q / 100f, fullChroma: true)));
                }
                else encodings.Add(($"webp q{q}", () => SkiaCodec.EncodeWebP(original, q, lossless: false)));
            }
            foreach (var (name, encode) in encodings)
            {
                var bytes = encode();
                var decoded = ImagePipeline.Decode(bytes, s.Format).Image;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var f = Fidelity.Compare(original, decoded);
                var ms = watch.Elapsed.TotalMilliseconds;
                output.WriteLine($"{s.Name,-26} {name,-9} {100.0 * (s.Bytes.Length - bytes.Length) / s.Bytes.Length,6:0.0}% {f.LumaSsim,7:0.0000} {f.WorstLumaSsim,7:0.0000} {f.ChromaSsim,7:0.0000} {f.MeanDeltaE,6:0.00} {f.DeltaE99,6:0.00} {f.EdgeSsim,7:0.0000} {ms,5:0}");
            }
        }
    }

    /// <summary>A lossless photograph (WebP or PNG-like source): what lossy WebP keeps of its full sensor noise.</summary>
    [Fact]
    [Trait("Category", "Measurement")]
    public void Measure_lossless_webp_photo()
    {
        foreach (var (w, h) in new[] { (400, 300), (1600, 1200) })
        {
            var image = CompressionCorpus.Photo(seed: 12, width: w, height: h);
            var source = SkiaCodec.EncodeWebP(image, 100, lossless: true);
            foreach (var q in new[] { 95, 90, 85, 80, 75, 70, 60, 50 })
            {
                var bytes = SkiaCodec.EncodeWebP(image, q, lossless: false);
                var f = Fidelity.Compare(image, ImagePipeline.Decode(bytes, WinTools.Core.Files.ImageFormat.WebP).Image);
                output.WriteLine($"{w}x{h} webp q{q}: saved {100.0 * (source.Length - bytes.Length) / source.Length,5:0.0}% luma {f.LumaSsim:0.0000} worst {f.WorstLumaSsim:0.0000} chroma {f.ChromaSsim:0.0000} dE {f.MeanDeltaE:0.00} dE99 {f.DeltaE99:0.00} edge {f.EdgeSsim:0.0000}");
            }
        }
    }

    /// <summary>Time per Compress on the corpus (decode to written copy, in process) and the fidelity cost on a large image.</summary>
    [Fact]
    [Trait("Category", "Measurement")]
    public void Measure_compress_time()
    {
        foreach (var s in CompressionCorpus.All().Where(x => x.Format != WinTools.Core.Files.ImageFormat.Png))
        {
            var src = _dir.Write("t-" + s.Name, s.Bytes);
            var line = new System.Text.StringBuilder($"{s.Name,-26}");
            foreach (var level in new[] { "high", "balanced", "small" })
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var r = ConversionJob.Run(new ConversionRequest($"t{Interlocked.Increment(ref _seq)}", $"compress.{level}", src));
                line.Append($" {level}:{watch.Elapsed.TotalMilliseconds,6:0} ms {(r.Success ? "written" : r.Error.ToString())}");
                if (r.OutputPath is { } o) File.Delete(o);
            }
            output.WriteLine(line.ToString());
        }
        var big = CompressionCorpus.Photo(seed: 31, width: 8000, height: 6000);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var w = System.Diagnostics.Stopwatch.StartNew();
        Fidelity.Compare(big, big);
        output.WriteLine($"fidelity 48 MP: {w.Elapsed.TotalMilliseconds:0} ms, allocated {(GC.GetTotalAllocatedBytes(precise: true) - before) / 1048576.0:0.0} MB");
    }

    /// <summary>Compress at the worker's size limit (48 MP), against the host's 60 s job timeout.</summary>
    [Fact]
    [Trait("Category", "Measurement")]
    public void Measure_compress_time_large()
    {
        var image = CompressionCorpus.Photo(seed: 32, width: 8000, height: 6000);
        foreach (var (name, bytes) in new[] { ("big-420.jpg", WicCodec.Encode(image, WinTools.Core.Files.ImageFormat.Jpeg, 0.93f)), ("big-444.jpg", WicCodec.Encode(image, WinTools.Core.Files.ImageFormat.Jpeg, 0.97f, fullChroma: true)) })
        {
            var src = _dir.Write(name, bytes);
            foreach (var level in new[] { "high", "balanced", "small" })
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var r = ConversionJob.Run(new ConversionRequest($"L{Interlocked.Increment(ref _seq)}", $"compress.{level}", src));
                output.WriteLine($"{name} {level}: {watch.Elapsed.TotalSeconds:0.0} s {(r.Success ? $"written, saved {100.0 * (bytes.Length - new FileInfo(r.OutputPath!).Length) / bytes.Length:0.0}%" : r.Error.ToString())}");
                if (r.OutputPath is { } o) File.Delete(o);
            }
        }
    }

    public void Dispose() => _dir.Dispose();
}
