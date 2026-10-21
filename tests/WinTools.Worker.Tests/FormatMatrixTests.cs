using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Worker.Imaging;

namespace WinTools.Worker.Tests;

/// <summary>Every supported source → target pair on real files, plus per-format alpha, damage and naming cases.</summary>
public sealed class FormatMatrixTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private static int _seq;

    public void Dispose() => _dir.Dispose();

    private static ConversionResult Run(ImageFormat target, string source) =>
        ConversionJob.Run(new ConversionRequest($"m{Interlocked.Increment(ref _seq)}", OperationCatalog.Convert(target).Id, source));

    /// <summary>The quadrant sample in the given format, all made by independent encoders (GDI+, and Pillow for WebP).</summary>
    private string Sample(ImageFormat format, string stem = "q") =>
        _dir.CopySample("quadrants-64x48" + ImageFormats.Extension(format), stem + ImageFormats.Extension(format));

    private static bool Lossy(ImageFormat f) => f is ImageFormat.Jpeg or ImageFormat.WebP;

    public static TheoryData<ImageFormat, ImageFormat> Pairs()
    {
        var data = new TheoryData<ImageFormat, ImageFormat>();
        foreach (var s in ImageFormats.Decodable)
            foreach (var t in ImageFormats.Encodable)
                if (s != t) data.Add(s, t);
        return data;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Every_pair_converts_to_a_valid_file_with_the_right_pixels(ImageFormat source, ImageFormat target)
    {
        var src = Sample(source);
        var before = Hash.Of(src);

        var r = Run(target, src);

        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(_dir.Path, "q" + ImageFormats.Extension(target)), r.OutputPath);
        var bytes = File.ReadAllBytes(r.OutputPath!);
        Assert.Equal(target, FileSignature.Detect(bytes));
        var px = Pixels.Load(r.OutputPath!);
        Assert.Equal((64, 48), (px.Width, px.Height));
        var tol = Lossy(source) || Lossy(target) ? 40 : 2;
        Near(px.At(8, 6), (255, 0, 0), tol); Near(px.At(55, 6), (0, 255, 0), tol);
        Near(px.At(8, 41), (0, 0, 255), tol); Near(px.At(55, 41), (255, 255, 0), tol);
        Assert.Equal(before, Hash.Of(src));
        Assert.Equal(2, _dir.Names().Length);
    }

    [Theory]
    [InlineData(ImageFormat.WebP)]
    [InlineData(ImageFormat.Tiff)]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Bmp)]
    public void Transparency_is_kept_where_the_format_can_hold_it_and_composited_on_white_elsewhere(ImageFormat target)
    {
        var src = _dir.CopySample("alpha-32x32.png");

        var r = Run(target, src);

        Assert.True(r.Success, r.Message);
        var px = Pixels.Load(r.OutputPath!);
        if (ImageFormats.KeepsAlpha(target))
        {
            Assert.InRange(px.At(4, 16).A, 0, 4);       // fully transparent stays transparent
            Assert.InRange(px.At(27, 16).A, 118, 138);  // 50 % stays 50 %
            Near(px.At(27, 16), (0, 0, 255), 12);
        }
        else
        {
            Assert.Equal(255, px.At(4, 16).A);
            Near(px.At(4, 16), (255, 255, 255), 6);     // white, never black
            Near(px.At(27, 16), (127, 127, 255), 14);
        }
    }

    [Fact]
    public void Png_alpha_round_trips_losslessly_through_tiff()
    {
        var tif = Run(ImageFormat.Tiff, _dir.CopySample("alpha-32x32.png"));
        Assert.True(tif.Success, tif.Message);
        var back = Run(ImageFormat.Png, tif.OutputPath!);
        Assert.True(back.Success, back.Message);
        var a = Pixels.Load(Path.Combine(AppContext.BaseDirectory, "samples", "alpha-32x32.png"));
        var b = Pixels.Load(back.OutputPath!);
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
                if (a.At(x, y).A != 0) Assert.Equal(a.At(x, y), b.At(x, y)); // colour under alpha 0 may be normalised
    }

    [Theory]
    [InlineData(ImageFormat.WebP)]
    [InlineData(ImageFormat.Bmp)]
    [InlineData(ImageFormat.Tiff)]
    public void Truncated_files_fail_cleanly(ImageFormat format)
    {
        var full = File.ReadAllBytes(Sample(format, "full"));
        var src = _dir.Write("cut" + ImageFormats.Extension(format), full[..(full.Length / 2)]);
        var before = _dir.Names();

        var r = Run(format == ImageFormat.Png ? ImageFormat.Jpeg : ImageFormat.Png, src);

        Assert.False(r.Success, $"{format} half file converted");
        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        Assert.Equal(before, _dir.Names());
    }

    [Fact]
    public void Content_decides_the_format_not_the_extension()
    {
        var webp = File.ReadAllBytes(Sample(ImageFormat.WebP, "real"));
        var src = _dir.Write("renamed.png", webp);

        var toJpeg = Run(ImageFormat.Jpeg, src);
        Assert.True(toJpeg.Success, toJpeg.Message);           // valid for WebP content

        var toWebP = Run(ImageFormat.WebP, src);
        Assert.Equal(ErrorCode.ContentMismatch, toWebP.Error); // WebP -> WebP is not a conversion
        Assert.Contains("WebP", toWebP.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Arabic_and_emoji_names_work_for_the_new_formats()
    {
        var src = Sample(ImageFormat.Bmp, "صورة 日本 🚀");
        var r = Run(ImageFormat.WebP, src);
        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(_dir.Path, "صورة 日本 🚀.webp"), r.OutputPath);
    }

    [Theory]
    [InlineData(ImageFormat.WebP)]
    [InlineData(ImageFormat.Tiff)]
    [InlineData(ImageFormat.Bmp)]
    public void Large_landscape_image_converts_in_bounded_time(ImageFormat target)
    {
        var src = _dir.Write("big.png", PngWriter.Rgba(4000, 3000, (x, y) => ((byte)x, (byte)y, (byte)(x ^ y), 255)));
        var started = System.Diagnostics.Stopwatch.StartNew();

        var r = Run(target, src);

        Assert.True(r.Success, r.Message);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"took {started.Elapsed}");
        var px = Pixels.Load(r.OutputPath!);
        Assert.Equal((4000, 3000), (px.Width, px.Height));
    }

    [Fact]
    public void Portrait_image_keeps_its_shape()
    {
        var src = _dir.Write("tall.png", PngWriter.Rgba(30, 90, (x, y) => (y < 45 ? (byte)255 : (byte)0, 0, y < 45 ? (byte)0 : (byte)255, 255)));
        var r = Run(ImageFormat.WebP, src);
        Assert.True(r.Success, r.Message);
        var px = Pixels.Load(r.OutputPath!);
        Assert.Equal((30, 90), (px.Width, px.Height));
        Near(px.At(15, 10), (255, 0, 0), 40);
        Near(px.At(15, 80), (0, 0, 255), 40);
    }

    private static void Near((int R, int G, int B, int A) actual, (int R, int G, int B) expected, int tolerance) =>
        Assert.True(Math.Abs(actual.R - expected.R) <= tolerance && Math.Abs(actual.G - expected.G) <= tolerance && Math.Abs(actual.B - expected.B) <= tolerance,
            $"pixel {actual} is not within {tolerance} of {expected}");
}
