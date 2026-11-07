using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Worker.Imaging;

namespace WinTools.Worker.Tests;

/// <summary>
/// The edits and the collage of ADR-0031 through the real job: flips, centred crops, the one-click adjustments and a
/// grid of several images. Each writes a new file under a name that says what was done; the original is never touched.
/// </summary>
public sealed class EditsAndCollageTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private static int _seq;
    public void Dispose() => _dir.Dispose();

    private static ConversionResult Run(string opId, string source) =>
        ConversionJob.Run(new ConversionRequest($"x{Interlocked.Increment(ref _seq)}", opId, source));

    private string Blocks(string name = "blocks.png", int width = 160, int height = 120) => _dir.Write(name, HeifSamples.Picture(width, height));

    private static RasterImage Load(string path) => ImagePipeline.Decode(File.ReadAllBytes(path), FileSignature.Detect(File.ReadAllBytes(path))).Image;

    private static (int R, int G, int B, int A) At(RasterImage image, int x, int y)
    {
        var i = (y * image.Width + x) * 4;
        return (image.Bgra[i + 2], image.Bgra[i + 1], image.Bgra[i], image.Bgra[i + 3]);
    }

    // ---- Flip ----

    [Theory]
    [InlineData("flip.horizontal", "blocks (flipped horizontally).png")]
    [InlineData("flip.vertical", "blocks (flipped vertically).png")]
    public void A_flip_mirrors_the_picture_exactly(string op, string expectedName)
    {
        var src = Blocks();
        var before = Hash.Of(src);

        var r = Run(op, src);

        Assert.True(r.Success, r.Message);
        Assert.Equal(expectedName, Path.GetFileName(r.OutputPath));
        Assert.Equal(before, Hash.Of(src));
        var (a, b) = (Load(src), Load(r.OutputPath!));
        var horizontal = op == "flip.horizontal";
        for (var y = 0; y < a.Height; y += 7)
            for (var x = 0; x < a.Width; x += 7)
                Assert.Equal(At(a, x, y), horizontal ? At(b, a.Width - 1 - x, y) : At(b, x, a.Height - 1 - y));
    }

    // ---- Crop ----

    [Theory]
    [InlineData("crop.16x9", 160, 90)]
    [InlineData("crop.1x1", 120, 120)]
    [InlineData("crop.9x16", 68, 120)]
    public void A_crop_keeps_the_centre_at_the_chosen_shape(string op, int width, int height)
    {
        var src = Blocks();

        var r = Run(op, src);

        Assert.True(r.Success, r.Message);
        var (a, b) = (Load(src), Load(r.OutputPath!));
        Assert.Equal((width, height), (b.Width, b.Height));
        int dx = (a.Width - width) / 2, dy = (a.Height - height) / 2;
        for (var y = 0; y < height; y += 5)
            for (var x = 0; x < width; x += 5)
                Assert.Equal(At(a, x + dx, y + dy), At(b, x, y));
    }

    [Fact]
    public void A_crop_to_the_shape_the_image_already_has_writes_nothing()
    {
        var src = Blocks(width: 160, height: 120);
        var r = Run("crop.4x3", src);
        Assert.Equal(ErrorCode.NothingToDo, r.Error);
        Assert.Equal(["blocks.png"], _dir.Names());
    }

    [Fact]
    public void A_jpeg_crop_stays_a_jpeg()
    {
        var src = _dir.Write("photo.jpg", WicCodec.Encode(CompressionCorpus.Photo(seed: 2, width: 400, height: 300), ImageFormat.Jpeg, 0.9f));
        var r = Run("crop.1x1", src);
        Assert.True(r.Success, r.Message);
        Assert.Equal("photo (cropped 1x1).jpg", Path.GetFileName(r.OutputPath));
        Assert.Equal((300, 300), (Load(r.OutputPath!).Width, Load(r.OutputPath!).Height));
    }

    // ---- Adjustments ----

    private static double Mean(RasterImage image, Func<(int R, int G, int B, int A), double> f)
    {
        double sum = 0;
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++) sum += f(At(image, x, y));
        return sum / (image.Width * image.Height);
    }

    private static double Luma((int R, int G, int B, int A) p) => 0.2126 * p.R + 0.7152 * p.G + 0.0722 * p.B;
    private static double Chroma((int R, int G, int B, int A) p) => Math.Max(p.R, Math.Max(p.G, p.B)) - Math.Min(p.R, Math.Min(p.G, p.B));

    /// <summary>Mean absolute difference between neighbours: how much fine detail there is.</summary>
    private static double Detail(RasterImage image) => Enumerable.Range(1, image.Width - 2).Average(x =>
        Enumerable.Range(0, image.Height).Average(y => Math.Abs(Luma(At(image, x, y)) - Luma(At(image, x + 1, y)))));

    private string Photo() => _dir.Write("photo.png", WicCodec.Encode(CompressionCorpus.Photo(seed: 9, width: 320, height: 240), ImageFormat.Png));

    [Fact]
    public void Black_and_white_leaves_no_colour()
    {
        var r = Run("adjust.black-and-white", Photo());
        Assert.True(r.Success, r.Message);
        Assert.Equal("photo (black and white).png", Path.GetFileName(r.OutputPath));
        var b = Load(r.OutputPath!);
        Assert.Equal(0, Mean(b, Chroma));
    }

    [Fact]
    public void Brighter_and_darker_move_the_tones_and_keep_black_and_white()
    {
        var src = Photo();
        var before = Mean(Load(src), Luma);
        var brighter = Load(Run("adjust.brighter", src).OutputPath!);
        var darker = Load(Run("adjust.darker", src).OutputPath!);
        Assert.True(Mean(brighter, Luma) > before + 8, $"{Mean(brighter, Luma)} vs {before}");
        Assert.True(Mean(darker, Luma) < before - 8, $"{Mean(darker, Luma)} vs {before}");
        // Pure black and pure white stay as they are.
        var extremes = _dir.Write("bw.png", PngWriter.Rgba(4, 1, (x, _) => x < 2 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255)));
        var bw = Load(Run("adjust.brighter", extremes).OutputPath!);
        Assert.Equal((0, 0, 0, 255), At(bw, 0, 0));
        Assert.Equal((255, 255, 255, 255), At(bw, 3, 0));
    }

    [Fact]
    public void More_contrast_spreads_the_tones_and_more_vivid_strengthens_colour()
    {
        var src = Photo();
        var a = Load(src);
        static double Spread(RasterImage i) { var m = Mean(i, Luma); return Mean(i, p => Math.Abs(Luma(p) - m)); }
        var spread = Spread(Load(Run("adjust.contrast", src).OutputPath!));
        Assert.True(spread > Spread(a) * 1.1, $"spread {spread:0.0} from {Spread(a):0.0}");
        Assert.True(Mean(Load(Run("adjust.vivid", src).OutputPath!), Chroma) > Mean(a, Chroma) * 1.15);
    }

    [Fact]
    public void Blur_softens_and_sharpen_adds_detail()
    {
        var src = Photo();
        var detail = Detail(Load(src));
        Assert.True(Detail(Load(Run("adjust.blur", src).OutputPath!)) < detail * 0.7);
        Assert.True(Detail(Load(Run("adjust.sharpen", src).OutputPath!)) > detail * 1.1);
        Assert.Equal(["photo (blurred).png", "photo (sharpened).png", "photo.png"], _dir.Names());
    }

    [Fact]
    public void Adjustments_keep_transparency_and_blur_leaves_no_dark_fringe()
    {
        // A white square on a transparent background.
        var src = _dir.Write("logo.png", PngWriter.Rgba(60, 60, (x, y) => x is >= 20 and < 40 && y is >= 20 and < 40 ? ((byte)255, (byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0)));
        var darker = Load(Run("adjust.darker", src).OutputPath!);
        Assert.Equal(0, At(darker, 5, 5).A);
        Assert.Equal(255, At(darker, 30, 30).A);

        var blurred = Load(Run("adjust.blur", src).OutputPath!);
        for (var x = 10; x < 50; x++)
        {
            var p = At(blurred, x, 30);
            if (p.A > 8) Assert.True(p.R > 240 && p.G > 240 && p.B > 240, $"dark fringe at x={x}: {p}");
        }
        Assert.InRange(At(blurred, 19, 30).A, 1, 254); // the edge is soft
    }

    [Fact]
    public void Adjustments_keep_the_colour_profile_of_a_jpeg()
    {
        var icc = HeifSamples.SrgbProfile();
        var photo = CompressionCorpus.Photo(seed: 4, width: 200, height: 150);
        var src = _dir.Write("tagged.jpg", WicCodec.Encode(new RasterImage(photo.Width, photo.Height, photo.Bgra, 300, 300, icc), ImageFormat.Jpeg, 0.9f));
        foreach (var op in new[] { "adjust.vivid", "adjust.sharpen", "flip.vertical", "crop.16x9" })
        {
            var r = Run(op, src);
            Assert.True(r.Success, r.Message);
            Assert.Equal(icc, Load(r.OutputPath!).IccProfile);
        }
    }

    // ---- Collage ----

    private string Solid(string name, int width, int height, (byte R, byte G, byte B) c, ImageFormat format)
    {
        var px = new byte[width * height * 4];
        for (var i = 0; i < px.Length; i += 4) (px[i], px[i + 1], px[i + 2], px[i + 3]) = (c.B, c.G, c.R, 255);
        return _dir.Write(name, WicCodec.Encode(new RasterImage(width, height, px), format, format == ImageFormat.Jpeg ? 0.95f : null));
    }

    private static JobRequest Collage(params string[] inputs) => new($"c{Interlocked.Increment(ref _seq)}", OperationCatalog.CollageId, inputs);

    [Fact]
    public void Three_photos_make_a_jpeg_grid_in_name_order_the_host_believes()
    {
        // Dropped in another order; "img 2" comes before "img 10".
        var inputs = new[]
        {
            Solid("img 10.jpg", 400, 300, HeifSamples.Blue, ImageFormat.Jpeg),
            Solid("img 1.jpg", 400, 300, HeifSamples.Red, ImageFormat.Jpeg),
            Solid("img 2.jpg", 400, 300, HeifSamples.Green, ImageFormat.Jpeg),
        };
        var hashes = inputs.Select(Hash.Of).ToArray();
        var request = Collage(inputs);

        var result = ImageJobs.Run(request);

        Assert.True(result.Success, result.Message);
        Assert.Equal("img 1 (collage).jpg", Path.GetFileName(result.Outputs[0].Path));
        Assert.True(ImageJobModule.Instance.IsBelievable(request, result));
        Assert.Equal(hashes, inputs.Select(Hash.Of).ToArray());
        var c = Load(result.Outputs[0].Path);
        // 2 × 2 cells of 400 × 300 with a gap of 6: red, green on the first row; blue alone, centred, on the second.
        Assert.Equal((2 * 400 + 3 * 6, 2 * 300 + 3 * 6), (c.Width, c.Height));
        Assert.True(HeifSamples.Near(At(c, 6 + 200, 6 + 150), HeifSamples.Red, 12));
        Assert.True(HeifSamples.Near(At(c, 12 + 600, 6 + 150), HeifSamples.Green, 12));
        Assert.True(HeifSamples.Near(At(c, c.Width / 2, 12 + 450), HeifSamples.Blue, 12));
        Assert.True(HeifSamples.Near(At(c, 20, 12 + 450), (255, 255, 255), 6)); // the empty side of the last row is white
    }

    [Fact]
    public void A_collage_with_a_png_is_a_png_and_each_image_fits_whole_in_its_cell()
    {
        var wide = Solid("a.png", 800, 200, HeifSamples.Red, ImageFormat.Png);
        var tall = Solid("b.jpg", 300, 600, HeifSamples.Green, ImageFormat.Jpeg);

        var result = ImageJobs.Run(Collage(wide, tall));

        Assert.True(result.Success, result.Message);
        Assert.Equal("a (collage).png", Path.GetFileName(result.Outputs[0].Path));
        var c = Load(result.Outputs[0].Path);
        // Every red and green pixel of each image is there: the colours fill a rectangle of the image's shape.
        static (int W, int H) Extent(RasterImage img, (byte R, byte G, byte B) colour)
        {
            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
            for (var y = 0; y < img.Height; y++)
                for (var x = 0; x < img.Width; x++)
                    if (HeifSamples.Near(At(img, x, y), colour, 30)) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
            return (maxX - minX + 1, maxY - minY + 1);
        }
        var (rw, rh) = Extent(c, HeifSamples.Red);
        var (gw, gh) = Extent(c, HeifSamples.Green);
        Assert.InRange(rw / (double)rh, 3.8, 4.2);
        Assert.InRange(gw / (double)gh, 0.45, 0.55);
    }

    [Fact]
    public void A_collage_of_many_large_images_stays_within_the_size_limit()
    {
        var inputs = Enumerable.Range(1, 16).Select(i => Solid($"p{i:00}.png", 2000, 1500, HeifSamples.Yellow, ImageFormat.Png)).ToArray();
        var result = ImageJobs.Run(Collage(inputs));
        Assert.True(result.Success, result.Message);
        var c = Load(result.Outputs[0].Path);
        Assert.True(Math.Max(c.Width, c.Height) <= Imaging.Collage.MaxSide, $"{c.Width}x{c.Height}");
    }

    [Fact]
    public void A_collage_is_refused_for_one_or_too_many_images_duplicates_and_non_images()
    {
        var a = Solid("a.png", 40, 30, HeifSamples.Red, ImageFormat.Png);
        var b = Solid("b.png", 40, 30, HeifSamples.Green, ImageFormat.Png);
        var text = _dir.Write("notes.png", "not an image"u8.ToArray());
        Assert.Equal(ErrorCode.InvalidRequest, ImageJobs.Run(Collage(a)).Error);
        Assert.Equal(ErrorCode.InvalidRequest, ImageJobs.Run(Collage([.. Enumerable.Repeat(a, 17)])).Error);
        Assert.Equal(ErrorCode.InvalidRequest, ImageJobs.Run(Collage(a, a)).Error);
        Assert.Equal(ErrorCode.ContentMismatch, ImageJobs.Run(Collage(a, b, text)).Error);
        Assert.Equal(["a.png", "b.png", "notes.png"], _dir.Names());
    }

    /// <summary>A PNG of one colour tagged with an ICC profile, in an iCCP chunk of its own.</summary>
    private string TaggedPng(string name, (byte R, byte G, byte B) c, byte[] icc)
    {
        var png = PngWriter.Rgba(60, 40, (_, _) => (c.R, c.G, c.B, 255));
        var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) zs.Write(icc);
        return _dir.Write(name, PngWriter.InsertChunkAfterIhdr(png, "iCCP", [.. "test\0\0"u8, .. z.ToArray()]));
    }

    /// <summary>A small matrix profile with the Adobe RGB (1998) primaries, adapted to D50, and gamma 2.2.</summary>
    private static byte[] AdobeRgbLikeProfile()
    {
        static byte[] Xyz(double x, double y, double z) => [.. "XYZ "u8, 0, 0, 0, 0, .. Fixed(x), .. Fixed(y), .. Fixed(z)];
        static byte[] Fixed(double v) { var i = (int)Math.Round(v * 65536); return [(byte)(i >> 24), (byte)(i >> 16), (byte)(i >> 8), (byte)i]; }
        byte[] curve = [.. "curv"u8, 0, 0, 0, 0, 0, 0, 0, 1, 0x02, 0x33, 0, 0];
        var tags = new (string Name, byte[] Data)[]
        {
            ("rXYZ", Xyz(0.6097, 0.3111, 0.0195)), ("gXYZ", Xyz(0.2053, 0.6257, 0.0609)), ("bXYZ", Xyz(0.1492, 0.0632, 0.7446)),
            ("rTRC", curve), ("gTRC", curve), ("bTRC", curve),
        };
        var at = 128 + 4 + 12 * tags.Length;
        var table = new List<byte> { 0, 0, 0, (byte)tags.Length };
        var data = new List<byte>();
        foreach (var (name, bytes) in tags)
        {
            table.AddRange(System.Text.Encoding.ASCII.GetBytes(name));
            table.AddRange([(byte)(at >> 24), (byte)(at >> 16), (byte)(at >> 8), (byte)at, 0, 0, 0, (byte)bytes.Length]);
            data.AddRange(bytes);
            at += bytes.Length;
        }
        var p = new byte[at];
        (p[0], p[1], p[2], p[3]) = ((byte)(at >> 24), (byte)(at >> 16), (byte)(at >> 8), (byte)at);
        p[8] = 4; // version
        "mntrRGB XYZ "u8.CopyTo(p.AsSpan(12));
        "acsp"u8.CopyTo(p.AsSpan(36));
        Fixed(0.9642).CopyTo(p, 68); Fixed(1).CopyTo(p, 72); Fixed(0.8249).CopyTo(p, 76); // the D50 illuminant
        table.CopyTo(p, 128);
        data.CopyTo(p, 128 + table.Count);
        return p;
    }

    [Fact]
    public void A_collage_of_different_colour_profiles_converts_each_image_to_srgb()
    {
        (byte, byte, byte) colour = (100, 200, 100);
        var plain = TaggedPngless("a.png", colour);
        var wide = TaggedPng("b.png", colour, AdobeRgbLikeProfile());
        var request = Collage(plain, wide);

        var result = ImageJobs.Run(request);

        Assert.True(result.Success, result.Message);
        Assert.Empty(result.Facts); // nothing to warn about: the colours are right
        var c = Load(result.Outputs[0].Path);
        Assert.Null(c.IccProfile);
        var (x, y) = (c.Width / 4, c.Height / 2);
        Assert.True(HeifSamples.Near(At(c, x, y), colour, 2), $"{At(c, x, y)}");
        var converted = At(c, c.Width * 3 / 4, y);
        // The same numbers mean a stronger green in the wider space, as sRGB says it.
        Assert.True(converted.R < 80 && converted.G is > 190 and < 212, $"{converted}");
    }

    private string TaggedPngless(string name, (byte R, byte G, byte B) c) => _dir.Write(name, PngWriter.Rgba(60, 40, (_, _) => (c.R, c.G, c.B, 255)));

    [Fact]
    public void A_profile_that_cannot_be_applied_is_stated_without_claiming_a_conversion()
    {
        var plain = Solid("a.jpg", 200, 150, HeifSamples.Red, ImageFormat.Jpeg);
        // A sound profile but for one tag: it can be carried, not computed with.
        var broken = AdobeRgbLikeProfile();
        "xTRC"u8.CopyTo(broken.AsSpan(128 + 4 + 12 * 4));
        var unusable = TaggedPng("b.png", (100, 200, 100), broken);
        var request = Collage(plain, unusable);

        var result = ImageJobs.Run(request);

        Assert.True(result.Success, result.Message);
        var notice = Assert.Single(result.Facts);
        Assert.True(notice.Notice);
        Assert.Contains("could not be applied", notice.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("sRGB", notice.Value, StringComparison.Ordinal);
        Assert.True(ImageJobModule.Instance.IsBelievable(request, result));
        Assert.False(ImageJobModule.Instance.IsBelievable(request, result with { Facts = [new("Safe", "Verified")] })); // a fact that is not a notice
    }

    [Fact]
    public void A_collage_of_images_sharing_a_profile_keeps_it()
    {
        var icc = AdobeRgbLikeProfile();
        var result = ImageJobs.Run(Collage(TaggedPng("a.png", (90, 180, 90), icc), TaggedPng("b.png", (180, 90, 90), icc)));

        Assert.True(result.Success, result.Message);
        Assert.Empty(result.Facts);
        Assert.Equal(icc, Load(result.Outputs[0].Path).IccProfile);
    }

    [Fact]
    public void A_collage_out_of_time_says_so_and_writes_nothing()
    {
        var a = Solid("a.png", 40, 30, HeifSamples.Red, ImageFormat.Png);
        var b = Solid("b.png", 40, 30, HeifSamples.Green, ImageFormat.Png);
        Assert.Equal(ErrorCode.WorkerTimeout, ConversionJob.RunCollage("late1", [a, b], null, null, deadline: TimeSpan.Zero).Error);
        Assert.Equal(["a.png", "b.png"], _dir.Names());
    }

    [Theory]
    [InlineData("a.jpg", "resize.percent-50", 60)]
    [InlineData("a.png", "convert.to-webp", 60)]
    [InlineData("a.png", "convert.to-avif", 120)]
    public void The_host_gives_engine_work_the_longer_limit_and_everything_else_sixty_seconds(string name, string op, int seconds)
    {
        var path = _dir.Write(name, name.EndsWith(".png", StringComparison.Ordinal) ? HeifSamples.Picture(16, 16) : WicCodec.Encode(new RasterImage(8, 8, new byte[256]), ImageFormat.Jpeg));
        Assert.Equal(TimeSpan.FromSeconds(seconds), ImageJobModule.TimeoutFor(new JobRequest("t", op, [path])));
    }

    [Fact]
    public void The_limit_follows_the_content_not_the_name_and_a_collage_gets_the_longer_one()
    {
        var hidden = _dir.Write("really a heic.jpg", new HeifBuilder().Build([new CodedTile([], "hvc1", 8, 8, [1])], 1, 1, 8, 8));
        Assert.Equal(TimeSpan.FromSeconds(120), ImageJobModule.TimeoutFor(new JobRequest("t", "rotate.right", [hidden])));
        var png = _dir.Write("a.png", HeifSamples.Picture(16, 16));
        Assert.Equal(TimeSpan.FromSeconds(120), ImageJobModule.TimeoutFor(new JobRequest("t", OperationCatalog.CollageId, [png, hidden])));
    }

    [Fact]
    public void The_host_does_not_believe_a_collage_under_another_name()
    {
        var a = Solid("a.png", 40, 30, HeifSamples.Red, ImageFormat.Png);
        var b = Solid("b.png", 40, 30, HeifSamples.Green, ImageFormat.Png);
        var request = Collage(b, a);
        var result = ImageJobs.Run(request);
        Assert.True(ImageJobModule.Instance.IsBelievable(request, result));
        var moved = Path.Combine(_dir.Path, "b (collage).png");
        File.Copy(result.Outputs[0].Path, moved);
        Assert.False(ImageJobModule.Instance.IsBelievable(request, result with { Outputs = [new JobOutput(moved, result.Outputs[0].Sha256)] }));
    }
}
