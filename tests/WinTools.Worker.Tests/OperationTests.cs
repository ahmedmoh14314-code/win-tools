using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Worker.Imaging;

namespace WinTools.Worker.Tests;

/// <summary>Resize, rotate and compress on real files: geometry, colour, transparency, naming and "nothing to do".</summary>
public sealed class OperationTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private static int _seq;

    public void Dispose() => _dir.Dispose();

    private static ConversionResult Run(string opId, string source) =>
        ConversionJob.Run(new ConversionRequest($"o{Interlocked.Increment(ref _seq)}", opId, source));

    private static void Near((int R, int G, int B, int A) actual, (int R, int G, int B) expected, int tolerance) =>
        Assert.True(Math.Abs(actual.R - expected.R) <= tolerance && Math.Abs(actual.G - expected.G) <= tolerance && Math.Abs(actual.B - expected.B) <= tolerance,
            $"pixel {actual} is not within {tolerance} of {expected}");

    private string Quadrants(int w, int h, string name) =>
        _dir.Write(name, PngWriter.Rgba(w, h, (x, y) => (x < w / 2, y < h / 2) switch
        {
            (true, true) => (255, 0, 0, 255), (false, true) => (0, 255, 0, 255), (true, false) => (0, 0, 255, 255), _ => (255, 255, 0, 255),
        }));

    [Fact]
    public void Resize_50_percent_halves_the_png_and_names_the_output()
    {
        var src = Quadrants(200, 100, "photo.png");
        var before = Hash.Of(src);

        var r = Run("resize.percent-50", src);

        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(_dir.Path, "photo (resized 50%).png"), r.OutputPath);
        var px = Pixels.Load(r.OutputPath!);
        Assert.Equal((100, 50), (px.Width, px.Height));
        Near(px.At(20, 10), (255, 0, 0), 2); Near(px.At(80, 10), (0, 255, 0), 2);
        Near(px.At(20, 40), (0, 0, 255), 2); Near(px.At(80, 40), (255, 255, 0), 2);
        Assert.Equal(before, Hash.Of(src));
    }

    [Theory]
    [InlineData(4000, 3000, 1440, 1080)]
    [InlineData(3000, 4000, 810, 1080)]
    public void Resize_to_1920x1080_fits_landscape_and_portrait(int w, int h, int ew, int eh)
    {
        var src = _dir.Write("big.png", PngWriter.Rgba(w, h, (x, y) => ((byte)x, (byte)y, 100, 255)));
        var started = System.Diagnostics.Stopwatch.StartNew();

        var r = Run("resize.box-1920x1080", src);

        Assert.True(r.Success, r.Message);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"took {started.Elapsed}");
        Assert.Equal(Path.Combine(_dir.Path, "big (1920x1080).png"), r.OutputPath);
        var px = Pixels.Load(r.OutputPath!);
        Assert.Equal((ew, eh), (px.Width, px.Height));
    }

    [Fact]
    public void Small_image_is_not_upscaled_and_no_file_is_written()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "small.png");
        var r = Run("resize.box-1920x1080", src);
        Assert.Equal(ErrorCode.NothingToDo, r.Error);
        Assert.Equal(["small.png"], _dir.Names());
    }

    [Fact]
    public void Odd_and_tiny_sizes_round_sensibly()
    {
        var odd = Run("resize.percent-50", Quadrants(101, 51, "odd.png"));
        Assert.True(odd.Success, odd.Message);
        var px = Pixels.Load(odd.OutputPath!);
        Assert.Equal((51, 26), (px.Width, px.Height));

        var tiny = Run("resize.percent-25", _dir.Write("tiny.png", PngWriter.Rgba(1, 1, (x, y) => (1, 2, 3, 255))));
        Assert.Equal(ErrorCode.NothingToDo, tiny.Error);
    }

    [Fact]
    public void Resizing_transparency_does_not_create_dark_fringes()
    {
        // Left half: fully transparent BLACK; right half: opaque white. Without premultiplied resampling the black under
        // alpha 0 bleeds into the edge and makes grey semi-transparent pixels.
        var src = _dir.Write("edge.png", PngWriter.Rgba(64, 8, (x, y) => x < 32 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255, (byte)255)));

        var r = Run("resize.percent-25", src);

        Assert.True(r.Success, r.Message);
        var px = Pixels.Load(r.OutputPath!);
        for (var x = 0; x < px.Width; x++)
        {
            var p = px.At(x, 1);
            if (p.A > 8) Assert.True(p.R >= 235 && p.G >= 235 && p.B >= 235, $"dark fringe at x={x}: {p}");
        }
    }

    [Fact]
    public void Resizing_a_lossless_webp_stays_lossless()
    {
        var src = _dir.CopySample("quadrants-64x48.webp", "q.webp");
        var r = Run("resize.percent-50", src);
        Assert.True(r.Success, r.Message);
        Assert.True(SkiaCodec.IsLosslessWebP(File.ReadAllBytes(r.OutputPath!)));
        Near(Pixels.Load(r.OutputPath!).At(4, 4), (255, 0, 0), 2);
    }

    [Theory]
    [InlineData("rotate.right", 48, 64, "TR", "BR", "TL", "BL")]
    [InlineData("rotate.left", 48, 64, "BL", "TL", "BR", "TR")]
    [InlineData("rotate.180", 64, 48, "BR", "BL", "TR", "TL")]
    public void Rotation_moves_the_quadrants_exactly(string op, int w, int h, string red, string green, string blue, string yellow)
    {
        var r = Run(op, _dir.CopySample("quadrants-64x48.png", "q.png"));
        Assert.True(r.Success, r.Message);
        var px = Pixels.Load(r.OutputPath!);
        Assert.Equal((w, h), (px.Width, px.Height));
        (int, int) At(string c) => c switch { "TL" => (4, 4), "TR" => (w - 5, 4), "BL" => (4, h - 5), _ => (w - 5, h - 5) };
        void Check(string c, (int, int, int) rgb) { var (x, y) = At(c); Near(px.At(x, y), rgb, 0); }
        Check(red, (255, 0, 0)); Check(green, (0, 255, 0)); Check(blue, (0, 0, 255)); Check(yellow, (255, 255, 0));
    }

    [Fact]
    public void Rotation_starts_from_the_exif_orientation_the_user_sees()
    {
        // Stored 64x48, EXIF 6 = displayed rotated right (48x64). Rotating left must give back the stored layout.
        var jpeg = ExifWriter.WithOrientation(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", "quadrants-64x48.jpg")), 6);
        var r = Run("rotate.left", _dir.Write("phone.jpg", jpeg));
        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(_dir.Path, "phone (rotated left).jpg"), r.OutputPath);
        var px = Pixels.Load(r.OutputPath!);
        Assert.Equal((64, 48), (px.Width, px.Height));
        Near(px.At(8, 6), (255, 0, 0), 45);
        Near(px.At(55, 41), (255, 255, 0), 45);
    }

    private string NoisyJpeg(string name, float quality)
    {
        var rnd = new Random(5);
        var png = PngWriter.Rgba(400, 300, (x, y) => ((byte)(x + rnd.Next(40)), (byte)(y + rnd.Next(40)), (byte)((x ^ y) + rnd.Next(40)), 255));
        var image = ImagePipeline.Decode(png, ImageFormat.Png).Image;
        return _dir.Write(name, WicCodec.Encode(image, ImageFormat.Jpeg, quality));
    }

    [Fact]
    public void Jpeg_compression_levels_are_ordered_and_smaller_than_the_source()
    {
        var src = NoisyJpeg("photo.jpg", 0.98f);
        var original = new FileInfo(src).Length;
        var sizes = new Dictionary<string, long>();
        foreach (var level in new[] { "high", "balanced", "small" })
        {
            var r = Run($"compress.{level}", src);
            Assert.True(r.Success, r.Message);
            sizes[level] = new FileInfo(r.OutputPath!).Length;
            File.Move(r.OutputPath!, Path.Combine(_dir.Path, $"{level}.jpg"));
        }
        Assert.True(sizes["high"] < original && sizes["balanced"] < sizes["high"] && sizes["small"] < sizes["balanced"],
            $"original {original}, high {sizes["high"]}, balanced {sizes["balanced"]}, small {sizes["small"]}");
    }

    [Fact]
    public void Compression_that_would_not_shrink_the_file_writes_nothing()
    {
        var src = NoisyJpeg("already-small.jpg", 0.3f);
        var r = Run("compress.high", src);
        Assert.Equal(ErrorCode.NothingToDo, r.Error);
        Assert.Equal(["already-small.jpg"], _dir.Names());
    }

    [Fact]
    public void Lossless_png_compression_is_smaller_and_pixel_identical()
    {
        var bytes = PngWriter.Rgba(300, 200, (x, y) => ((byte)(x / 10 * 10), (byte)(y / 10 * 10), 128, (byte)(x < 150 ? 255 : 128)),
            System.IO.Compression.CompressionLevel.NoCompression);
        var src = _dir.Write("raw.png", bytes);

        var r = Run("compress.lossless", src);

        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(_dir.Path, "raw (compressed).png"), r.OutputPath);
        Assert.True(new FileInfo(r.OutputPath!).Length < bytes.Length / 4);
        Assert.Equal(Pixels.Load(src).Bgra, Pixels.Load(r.OutputPath!).Bgra);
    }

    [Fact]
    public void Webp_compression_shrinks_a_lossless_webp()
    {
        var image = ImagePipeline.Decode(PngWriter.Rgba(300, 200, (x, y) => ((byte)x, (byte)y, (byte)(x * y), 255)), ImageFormat.Png).Image;
        var src = _dir.Write("art.webp", SkiaCodec.EncodeWebP(image, 100, lossless: true));
        var r = Run("compress.small", src);
        Assert.True(r.Success, r.Message);
        Assert.True(new FileInfo(r.OutputPath!).Length < new FileInfo(src).Length);
        Assert.False(SkiaCodec.IsLosslessWebP(File.ReadAllBytes(r.OutputPath!)));
    }

    [Fact]
    public async Task Operation_outputs_pass_the_hosts_success_check_across_the_process_boundary()
    {
        var src = Quadrants(200, 100, "p.png");
        var client = ImageJobsCompat.Images(Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.exe"), TimeSpan.FromSeconds(30));
        var results = await client.RunAsync([new ConversionRequest("p1", "resize.percent-50", src), new ConversionRequest("p2", "rotate.right", src), new ConversionRequest("p3", "resize.box-1920x1080", src)],
            CancellationToken.None);
        Assert.True(results[0].Success, results[0].Message);
        Assert.True(results[1].Success, results[1].Message);
        Assert.Equal(ErrorCode.NothingToDo, results[2].Error);
        Assert.Equal(["p (resized 50%).png", "p (rotated right).png", "p.png"], _dir.Names());
    }
}
