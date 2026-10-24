using System.Globalization;
using System.Text;
using WinTools.Core.Files;
using WinTools.Worker.Imaging;

namespace WinTools.Worker.Tests;

/// <summary>
/// The data the compression gates are chosen from (ADR-0017): for every case and every quality on the ladder, every
/// fidelity axis, the saving and, for cases with a marked region, that region's own SSIM and ΔE. Three sets:
/// <list type="bullet">
/// <item><c>train</c>: the regions the gates are tuned on (SmallDetailTests and the caption);</item>
/// <item><c>held</c>: regions the gates must hold on without being tuned on them (the evaluator's probes on PR #12:
/// WebP sources, detail under 16 px, 1–2 px lines, regions at the borders);</item>
/// <item><c>free</c>: photographs without small detail, on which a gate must not cost the saving.</item>
/// </list>
/// A measurement, not a check: WINTOOLS_MEASURE=1, written to WINTOOLS_MEASURE_OUT (tab-separated).
/// </summary>
public sealed class CompressionCalibration
{
    private static readonly bool[] BothModes = [true, false], OneMode = [false];
    private static readonly int[] Ladder = [95, 93, 90, 88, 85, 82, 80, 75, 70, 65, 60, 55, 50];

    private sealed record Case(string Set, string Name, RasterImage Image, ImageFormat Format, int Quality, bool FullChroma, (int X, int Y, int W, int H)? Region, List<(int X, int Y)>? Pixels = null);

    private static (byte R, byte G, byte B) C(int v) => ((byte)(v >> 16), (byte)(v >> 8), (byte)v);

    private static IEnumerable<Case> Cases()
    {
        // train: SmallDetailTests' cases and the caption
        foreach (var c in SmallDetailTests.Cases())
        {
            var (name, width, height, x, y, w, h, fg, bg, stroke, full, quality) = ((string)c[0], (int)c[1], (int)c[2], (int)c[3], (int)c[4], (int)c[5], (int)c[6], (int)c[7], (int)c[8], (int)c[9], (bool)c[10], (int)c[11]);
            yield return new("train", name, SmallDetailTests.WithText(CompressionCorpus.Photo(seed: 71, width: width, height: height), x, y, w, h, C(fg), bg < 0 ? null : C(bg), stroke, seed: 72),
                ImageFormat.Jpeg, quality, full, (x, y, w, h));
        }
        yield return new("train", "caption 160x48", ImagePipeline.Decode(CompressionQualityTests.CaptionedPhoto(), ImageFormat.Jpeg).Image, ImageFormat.Jpeg, 93, false, (700, 820, 160, 48));

        // held: the evaluator's probes
        object[][] held =
        [
            ["red/blue 12x8, 444", 1600, 1200, 701, 821, 12, 8, 0xE01E1E, 0x1E3CC8, 1, true, 97, 71],
            ["red/blue 16x10, 444", 1600, 1200, 705, 825, 16, 10, 0xE01E1E, 0x1E3CC8, 1, true, 97, 71],
            ["green/magenta 20x12, 444", 1600, 1200, 703, 823, 20, 12, 0x20D020, 0xC020C0, 1, true, 97, 71],
            ["cyan on photo 40x14, 444", 1600, 1200, 700, 820, 40, 14, 0x00E0FF, -1, 1, true, 97, 71],
            ["red on photo 40x14, 420", 1600, 1200, 700, 820, 40, 14, 0xFF2020, -1, 1, false, 93, 71],
            ["red/blue 32x14 at 0,0, 444", 1600, 1200, 0, 0, 32, 14, 0xE01E1E, 0x1E3CC8, 1, true, 97, 71],
            ["red/blue 32x14 flush right, 444", 1603, 1203, 1603 - 32, 600, 32, 14, 0xE01E1E, 0x1E3CC8, 1, true, 97, 71],
            ["red/blue 32x14 bottom-right, 444", 1603, 1203, 1603 - 32, 1203 - 14, 32, 14, 0xE01E1E, 0x1E3CC8, 1, true, 97, 71],
            ["red/blue 32x14 seed 81, 444", 1600, 1200, 701, 821, 32, 14, 0xE01E1E, 0x1E3CC8, 1, true, 97, 81],
            ["red/blue 32x14 seed 91 +8, 444", 1600, 1200, 709, 829, 32, 14, 0xE01E1E, 0x1E3CC8, 1, true, 97, 91],
            ["red/blue 32x14 q92 444", 1600, 1200, 701, 821, 32, 14, 0xE01E1E, 0x1E3CC8, 1, true, 92, 71],
            ["blue/red 32x14, 444", 1600, 1200, 701, 821, 32, 14, 0x1E3CC8, 0xE01E1E, 1, true, 97, 71],
            ["red/green 32x14, 444", 1600, 1200, 701, 821, 32, 14, 0xC83C3C, 0x3C9C3C, 1, true, 97, 71],
            ["yellow/blue 2px 48x16, 420", 1600, 1200, 700, 820, 48, 16, 0xF0E000, 0x2020A0, 2, false, 93, 71],
        ];
        foreach (var c in held)
        {
            var (name, width, height, x, y, w, h, fg, bg, stroke, full, quality, seed) = ((string)c[0], (int)c[1], (int)c[2], (int)c[3], (int)c[4], (int)c[5], (int)c[6], (int)c[7], (int)c[8], (int)c[9], (bool)c[10], (int)c[11], (int)c[12]);
            yield return new("held", name, SmallDetailTests.WithText(CompressionCorpus.Photo(seed: seed, width: width, height: height), x, y, w, h, C(fg), bg < 0 ? null : C(bg), stroke, seed: seed + 1),
                ImageFormat.Jpeg, quality, full, (x, y, w, h));
        }
        foreach (var (name, x, y, w, h, fg, bg) in new[] { ("webp red/blue 32x14", 701, 821, 32, 14, 0xE01E1E, 0x1E3CC8), ("webp red/blue 12x8", 701, 821, 12, 8, 0xE01E1E, 0x1E3CC8), ("webp orange on photo 100x22", 1490, 1170, 100, 22, 0xFF8C00, -1) })
            yield return new("held", name, SmallDetailTests.WithText(CompressionCorpus.Photo(seed: 71), x, y, w, h, C(fg), bg < 0 ? null : C(bg), 1, seed: 72), ImageFormat.WebP, 92, false, (x, y, w, h));
        foreach (var (name, kind, thick, colour) in new[] { ("h line 1px red", 0, 1, 0xFF0000), ("h line 2px red", 0, 2, 0xFF0000), ("v line 1px magenta", 1, 1, 0xFF00FF), ("diagonal 1px green", 2, 1, 0x00FF40), ("h line 1px blue on sky", 3, 1, 0x0000FF) })
        {
            var photo = CompressionCorpus.Photo(seed: 75);
            var bgra = (byte[])photo.Bgra.Clone();
            var c = C(colour);
            void Px(int x, int y) { var i = (y * photo.Width + x) * 4; (bgra[i], bgra[i + 1], bgra[i + 2]) = (c.B, c.G, c.R); }
            (int X, int Y, int W, int H) box;
            List<(int, int)>? pixels = null;
            switch (kind)
            {
                case 0: for (var x = 600; x < 1000; x++) for (var t = 0; t < thick; t++) Px(x, 803 + t); box = (600, 803, 400, thick); break;
                case 1: for (var y = 700; y < 1000; y++) Px(803, y); box = (803, 700, 1, 300); break;
                case 2: for (var k = 0; k < 300; k++) Px(600 + k, 700 + k); box = (600, 700, 300, 300); pixels = [.. Enumerable.Range(0, 300).Select(k => (k, k))]; break;
                default: for (var x = 600; x < 1000; x++) Px(x, 203); box = (600, 203, 400, 1); break;
            }
            var image = photo.WithPixels(photo.Width, photo.Height, bgra);
            yield return new("held", name + ", 444", image, ImageFormat.Jpeg, 97, true, box, pixels);
            yield return new("held", name + ", 420", image, ImageFormat.Jpeg, 93, false, box, pixels);
        }

        // free: photographs without small detail
        foreach (var s in CompressionCorpus.All().Where(x => x.Name is "phone.jpg" or "camera.jpg" or "photo-lossy.webp" or "moderate.jpg"))
            yield return new("free", s.Name, ImagePipeline.Decode(s.Bytes, s.Format).Image, s.Format, -1, Metadata.IsFullChromaJpeg(s.Bytes), null);
        foreach (var seed in new[] { 111, 112 })
        {
            var photo = CompressionCorpus.Photo(seed);
            foreach (var (q, full) in new[] { (95, true), (92, false), (88, false), (97, true) })
                yield return new("free", $"photo{seed} q{q} {(full ? "444" : "420")}", photo, ImageFormat.Jpeg, q, full, null);
            yield return new("free", $"photo{seed} webp92", photo, ImageFormat.WebP, 92, false, null);
        }
    }

    [MeasurementFact]
    public void Measure_calibration_set()
    {
        var path = Environment.GetEnvironmentVariable("WINTOOLS_MEASURE_OUT") ?? Path.Combine(Path.GetTempPath(), "wintools-calibration.tsv");
        var text = new StringBuilder("set\tcase\tmode\tq\tsaved\tluma\tworst\tchroma\tde\tde99\tedge\ttile\ttileDe\tcellChroma\tregionSsim\tregionDe\n");
        var corpus = CompressionCorpus.All().ToDictionary(s => s.Name);
        Parallel.ForEach(Cases(), new ParallelOptions { MaxDegreeOfParallelism = 3 }, c =>
        {
            byte[] source = c.Quality < 0 ? corpus[c.Name].Bytes
                : c.Format == ImageFormat.Jpeg ? WicCodec.Encode(c.Image, ImageFormat.Jpeg, c.Quality / 100f, c.FullChroma) : SkiaCodec.EncodeWebP(c.Image, c.Quality, lossless: false);
            var original = ImagePipeline.Decode(source, c.Format).Image;
            var lines = new StringBuilder();
            foreach (var mode in c.Format == ImageFormat.Jpeg && c.FullChroma ? BothModes : OneMode)
                foreach (var q in Ladder)
                {
                    var bytes = c.Format == ImageFormat.Jpeg ? WicCodec.Encode(original, ImageFormat.Jpeg, q / 100f, mode) : SkiaCodec.EncodeWebP(original, q, lossless: false);
                    var decoded = ImagePipeline.Decode(bytes, c.Format).Image;
                    var f = Fidelity.Compare(original, decoded);
                    double rs = double.NaN, rd = double.NaN;
                    if (c.Region is { } r)
                    {
                        var (a, b) = (SmallDetailTests.Crop(original, r.X, r.Y, r.W, r.H), SmallDetailTests.Crop(decoded, r.X, r.Y, r.W, r.H));
                        rs = r.W >= 8 && r.H >= 8 ? Ssim.Luma(a, b) : double.NaN;
                        if (c.Pixels is { } only)
                        {
                            var (pa, pb) = (new byte[only.Count * 4], new byte[only.Count * 4]);
                            for (var k = 0; k < only.Count; k++) { Array.Copy(a.Bgra, (only[k].Y * r.W + only[k].X) * 4, pa, k * 4, 4); Array.Copy(b.Bgra, (only[k].Y * r.W + only[k].X) * 4, pb, k * 4, 4); }
                            rd = SmallDetailTests.MeanDeltaE(new RasterImage(only.Count, 1, pa), new RasterImage(only.Count, 1, pb));
                        }
                        else rd = SmallDetailTests.MeanDeltaE(a, b);
                    }
                    lines.Append(string.Create(CultureInfo.InvariantCulture,
                        $"{c.Set}\t{c.Name}\t{(c.Format == ImageFormat.WebP ? "webp" : mode ? "444" : "420")}\t{q}\t{100.0 * (source.Length - bytes.Length) / source.Length:0.00}\t{f.LumaSsim:0.00000}\t{f.WorstLumaSsim:0.00000}\t{f.ChromaSsim:0.00000}\t{f.MeanDeltaE:0.000}\t{f.DeltaE99:0.00}\t{f.EdgeSsim:0.00000}\t{f.WorstTileLumaSsim:0.00000}\t{f.WorstTileDeltaE:0.000}\t{f.WorstCellChromaSsim:0.00000}\t{rs:0.00000}\t{rd:0.000}\n"));
                }
            lock (text) text.Append(lines);
        });
        File.WriteAllText(path, text.ToString());
    }

    /// <summary>What the presets really choose (the product's search, not the ladder) on every case: saving and the region's truth.</summary>
    [MeasurementFact]
    public void Measure_presets_on_the_calibration_set()
    {
        var path = Environment.GetEnvironmentVariable("WINTOOLS_MEASURE_OUT") ?? Path.Combine(Path.GetTempPath(), "wintools-presets.tsv");
        var text = new StringBuilder("set\tcase\tlevel\tresult\tsaved\tregionSsim\tregionDe\n");
        var corpus = CompressionCorpus.All().ToDictionary(s => s.Name);
        Parallel.ForEach(Cases(), new ParallelOptions { MaxDegreeOfParallelism = 3 }, c =>
        {
            byte[] source = c.Quality < 0 ? corpus[c.Name].Bytes
                : c.Format == ImageFormat.Jpeg ? WicCodec.Encode(c.Image, ImageFormat.Jpeg, c.Quality / 100f, c.FullChroma) : SkiaCodec.EncodeWebP(c.Image, c.Quality, lossless: false);
            var original = ImagePipeline.Decode(source, c.Format).Image;
            var lines = new StringBuilder();
            foreach (var level in new[] { WinTools.Core.Capabilities.CompressLevel.High, WinTools.Core.Capabilities.CompressLevel.Balanced, WinTools.Core.Capabilities.CompressLevel.Small })
            {
                var chosen = LossyCompression.Smallest(original, c.Format, level, source);
                double rs = double.NaN, rd = double.NaN;
                if (chosen is not null && c.Region is { } r)
                {
                    var decoded = ImagePipeline.Decode(chosen.Bytes, c.Format).Image;
                    var (a, b) = (SmallDetailTests.Crop(original, r.X, r.Y, r.W, r.H), SmallDetailTests.Crop(decoded, r.X, r.Y, r.W, r.H));
                    rs = r.W >= 8 && r.H >= 8 ? Ssim.Luma(a, b) : double.NaN;
                    rd = SmallDetailTests.MeanDeltaE(a, b);
                }
                lines.Append(string.Create(CultureInfo.InvariantCulture,
                    $"{c.Set}\t{c.Name}\t{level}\t{(chosen is null ? "optimized" : $"q{chosen.Quality} {(chosen.FullChroma ? "444" : "420")}")}\t{(chosen is null ? 0 : 100.0 * (source.Length - chosen.Bytes.Length) / source.Length):0.0}\t{rs:0.0000}\t{rd:0.00}\n"));
            }
            lock (text) text.Append(lines);
        });
        File.WriteAllText(path, text.ToString());
    }
}
