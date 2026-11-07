using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Worker.Imaging;

namespace WinTools.Worker.Tests;

/// <summary>
/// HEIF (HEIC) and AVIF input, and AVIF output (ADR-0031), on files made by the bundled engine and packed as phones and
/// encoders pack them (<see cref="HeifSamples"/>): grids of tiles with their descriptor in idat, rotation and mirroring,
/// a thumbnail, a gain map, an Exif item with its own orientation, an ICC profile, transparency. What a camera writes is
/// read upright with its colours; what the product cannot show exactly is refused and leaves nothing behind.
/// </summary>
public sealed class HeifAvifTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private static int _seq;
    public void Dispose() => _dir.Dispose();

    private string ReferencePng(int width = 128, int height = 128) => _dir.Write($"ref-{width}x{height}.png", HeifSamples.Picture(width, height));

    private static ConversionResult Run(string opId, string source, string? outputDirectory = null) =>
        ConversionJob.Run(new ConversionRequest($"h{Interlocked.Increment(ref _seq)}", opId, source, outputDirectory));

    /// <summary>The stored picture: the reference cropped to the image's size (a grid's output size).</summary>
    private static RasterImage Stored(string referencePng, int width, int height)
    {
        var full = ImagePipeline.Decode(File.ReadAllBytes(referencePng), ImageFormat.Png).Image;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++) full.Bgra.AsSpan(y * full.Width * 4, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
        return new RasterImage(width, height, pixels);
    }

    /// <summary>Four points well inside the colour blocks, whatever the orientation (the blocks meet at 53 % and 64 %).</summary>
    private static readonly double[] ProbeAt = [0.2, 0.8];

    private static IEnumerable<(int X, int Y)> Probes(int width, int height) =>
        from fx in ProbeAt from fy in ProbeAt select ((int)(width * fx), (int)(height * fy));

    private static void AssertLooksLike(RasterImage expected, RasterImage actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        foreach (var (x, y) in Probes(expected.Width, expected.Height))
        {
            var i = (y * expected.Width + x) * 4;
            var want = (expected.Bgra[i + 2], expected.Bgra[i + 1], expected.Bgra[i]);
            var got = (actual.Bgra[i + 2], actual.Bgra[i + 1], actual.Bgra[i], actual.Bgra[i + 3]);
            Assert.True(HeifSamples.Near(got, want), $"at ({x},{y}) expected {want}, got {got}");
        }
    }

    /// <summary>What the pipeline shows for a file: decoded and turned by the orientation it reports.</summary>
    private static RasterImage Shown(byte[] file)
    {
        var format = FileSignature.Detect(file);
        var (image, orientation) = ImagePipeline.Decode(file, format);
        return Orientation.Apply(image, orientation);
    }

    /// <summary>The stored picture turned as the file says, built from the product's EXIF orientations one step at a time
    /// (irot k: k quarter turns anticlockwise; imir 0: top-bottom, 1: left-right).</summary>
    private static RasterImage Expected(RasterImage stored, int irot, int imir)
    {
        var image = stored;
        for (var k = 0; k < irot; k++) image = Orientation.Apply(image, 8);
        if (imir >= 0) image = Orientation.Apply(image, imir == 0 ? 4 : 2);
        return image;
    }

    // ---- HEIC as an iPhone writes it ----

    private string IphoneLike(string name, string reference, int rotation = 3)
    {
        var tiles = HeifSamples.Tiles(_dir.Path, reference, 64, 64, 2, 2);
        var builder = new HeifBuilder
        {
            Rotation = rotation, ExifOrientation = 6, Icc = HeifSamples.SrgbProfile(), GridInIdat = true,
            Thumbnail = HeifSamples.Encode(_dir.Path, reference, "scale=32:32,format=yuv420p"),
            Auxiliary = HeifSamples.Encode(_dir.Path, reference, "scale=64:64,format=gray,format=yuv420p"),
            AuxiliaryType = "urn:com:apple:photo:2020:aux:hdrgainmap",
        };
        return _dir.Write(name, builder.Build(tiles, 2, 2, 120, 100));
    }

    [Fact]
    public void An_iphone_like_heic_converts_upright_with_its_colour_profile_and_the_original_untouched()
    {
        var reference = ReferencePng();
        var src = IphoneLike("IMG_0001.HEIC", reference);
        Assert.Equal(ImageFormat.Heif, FileSignature.Detect(File.ReadAllBytes(src)));
        var before = Hash.Of(src);

        var r = Run("convert.to-png", src);

        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(_dir.Path, "IMG_0001.png"), r.OutputPath);
        Assert.Equal(before, Hash.Of(src));
        var output = ImagePipeline.Decode(File.ReadAllBytes(r.OutputPath!), ImageFormat.Png).Image;
        // irot 3 turns it a quarter clockwise; the Exif item's orientation (6) is not applied a second time.
        AssertLooksLike(Expected(Stored(reference, 120, 100), 3, -1), output);
        Assert.Equal(HeifSamples.SrgbProfile(), output.IccProfile);
    }

    [Fact]
    public void Every_rotation_and_mirror_shows_the_same_whether_the_image_is_a_grid_or_one_picture()
    {
        var reference = ReferencePng();
        var tiles = HeifSamples.Tiles(_dir.Path, reference, 64, 64, 2, 2);
        var single = HeifSamples.Encode(_dir.Path, reference, "crop=120:96:0:0,format=yuv420p");
        foreach (var irot in new[] { 0, 1, 2, 3 })
            foreach (var imir in new[] { -1, 0, 1 })
            {
                var builder = new HeifBuilder { Rotation = irot, Mirror = imir };
                // The engine composes a grid and turns it; a single picture is turned by the pipeline: both must agree.
                AssertLooksLike(Expected(Stored(reference, 120, 100), irot, imir), Shown(builder.Build(tiles, 2, 2, 120, 100)));
                AssertLooksLike(Expected(Stored(reference, 120, 96), irot, imir), Shown(builder.Build([single], 1, 1, 120, 96)));
            }
    }

    [Theory]
    [InlineData("rotate.right", "IMG_0001 (rotated right).jpg")]
    [InlineData("resize.percent-50", "IMG_0001 (resized 50%).jpg")]
    [InlineData("crop.1x1", "IMG_0001 (cropped 1x1).jpg")]
    [InlineData("adjust.black-and-white", "IMG_0001 (black and white).jpg")]
    [InlineData("convert.to-avif", "IMG_0001.avif")]
    public void An_edit_of_a_heic_writes_a_jpeg_the_host_believes(string op, string expectedName)
    {
        var src = IphoneLike("IMG_0001.heic", ReferencePng());
        var request = new JobRequest($"e{Interlocked.Increment(ref _seq)}", op, [src]);

        var result = ImageJobs.Run(request);

        Assert.True(result.Success, result.Message);
        Assert.Equal(expectedName, Path.GetFileName(result.Outputs[0].Path));
        Assert.True(ImageJobModule.Instance.IsBelievable(request, result));
    }

    [Fact]
    public void Image_info_of_a_heic_reports_what_is_shown()
    {
        var r = ImageJobs.Run(new JobRequest("i1", "metadata.info", [IphoneLike("IMG_0002.heic", ReferencePng())]));

        Assert.True(r.Success, r.Message);
        var facts = r.Facts.ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal("HEIC", facts["Format"]);
        Assert.StartsWith("100 × 120 px", facts["Dimensions"], StringComparison.Ordinal);
        Assert.Equal("Rotated 90° right", facts["Orientation"]);
        Assert.Equal("Embedded", facts["Colour profile"]);
        Assert.Equal("Yes", facts["EXIF"]);
        Assert.Equal("Not set", facts["Resolution"]);
    }

    [Fact]
    public void Removing_metadata_and_compressing_are_not_offered_for_heic_or_avif()
    {
        var src = IphoneLike("IMG_0003.heic", ReferencePng());
        Assert.Equal(ErrorCode.ContentMismatch, Run("metadata.strip", src).Error);
        Assert.Equal(ErrorCode.ContentMismatch, Run("compress.balanced", src).Error);
        Assert.Equal(["IMG_0003.heic", "ref-128x128.png"], _dir.Names());
    }

    // ---- AVIF ----

    private string AvifFromEngine(string name, string png, bool alpha = false)
    {
        var path = Path.Combine(_dir.Path, name);
        HeifSamples.Run(alpha
            ? ["-hide_banner", "-loglevel", "error", "-y", "-i", png, "-filter_complex", "[0]format=yuva444p,split[c][a];[a]alphaextract[al]", "-map", "[c]", "-map", "[al]",
               "-c:v", "libaom-av1", "-still-picture", "1", "-cpu-used", "8", "-frames:v", "1", "-f", "avif", path]
            : ["-hide_banner", "-loglevel", "error", "-y", "-i", png, "-c:v", "libaom-av1", "-still-picture", "1", "-cpu-used", "8", "-frames:v", "1", "-f", "avif", path]);
        return path;
    }

    [Fact]
    public void An_avif_from_an_encoder_converts_and_resizes_to_avif()
    {
        var reference = ReferencePng();
        var src = AvifFromEngine("still.avif", reference);
        Assert.Equal(ImageFormat.Avif, FileSignature.Detect(File.ReadAllBytes(src)));

        var png = Run("convert.to-png", src);
        Assert.True(png.Success, png.Message);
        AssertLooksLike(Stored(reference, 128, 128), ImagePipeline.Decode(File.ReadAllBytes(png.OutputPath!), ImageFormat.Png).Image);

        var half = Run("resize.percent-50", src);
        Assert.True(half.Success, half.Message);
        Assert.Equal("still (resized 50%).avif", Path.GetFileName(half.OutputPath));
        var bytes = File.ReadAllBytes(half.OutputPath!);
        Assert.Equal(ImageFormat.Avif, FileSignature.Detect(bytes));
        Assert.Equal((64, 64), (Shown(bytes).Width, Shown(bytes).Height));
    }

    [Fact]
    public void An_avif_grid_of_av1_tiles_is_composed()
    {
        var reference = ReferencePng();
        var tiles = HeifSamples.Tiles(_dir.Path, reference, 64, 64, 2, 2, av1: true);
        var src = _dir.Write("grid.avif", new HeifBuilder { Avif = true, Rotation = 1 }.Build(tiles, 2, 2, 120, 100));

        var r = Run("convert.to-png", src);

        Assert.True(r.Success, r.Message);
        AssertLooksLike(Expected(Stored(reference, 120, 100), 1, -1), ImagePipeline.Decode(File.ReadAllBytes(r.OutputPath!), ImageFormat.Png).Image);
    }

    [Fact]
    public void Transparency_of_an_avif_is_kept()
    {
        var png = _dir.Write("alpha.png", PngWriter.Rgba(64, 48, (x, y) => ((byte)(x * 4), 120, 200, (byte)(x < 32 ? 255 : 40))));
        var src = AvifFromEngine("alpha.avif", png, alpha: true);

        var r = Run("convert.to-png", src);

        Assert.True(r.Success, r.Message);
        var p = Pixels.Load(r.OutputPath!);
        Assert.Equal(255, p.At(8, 20).A);
        Assert.InRange(p.At(56, 20).A, 30, 50);
    }

    [Fact]
    public void To_avif_is_much_smaller_than_a_jpeg_and_shows_the_same_picture_with_its_profile()
    {
        var photo = CompressionCorpus.Photo(seed: 5, width: 640, height: 480);
        var tagged = new RasterImage(photo.Width, photo.Height, photo.Bgra, 96, 96, HeifSamples.SrgbProfile());
        var src = _dir.Write("photo.jpg", WicCodec.Encode(tagged, ImageFormat.Jpeg, 0.9f));
        var before = Hash.Of(src);

        var r = Run("convert.to-avif", src);

        Assert.True(r.Success, r.Message);
        Assert.Equal("photo.avif", Path.GetFileName(r.OutputPath));
        Assert.Equal(before, Hash.Of(src));
        var avif = File.ReadAllBytes(r.OutputPath!);
        Assert.True(avif.Length < new FileInfo(src).Length / 2, $"AVIF {avif.Length} bytes, JPEG {new FileInfo(src).Length}");
        var read = Heif.Read(avif, out var why);
        Assert.True(read is not null, why);
        Assert.Equal(HeifSamples.SrgbProfile(), read!.Icc);
        Assert.Equal(13, read.Transfer); // nclx: sRGB transfer, signalled
        var source = ImagePipeline.Decode(File.ReadAllBytes(src), ImageFormat.Jpeg).Image;
        var back = Shown(avif);
        Assert.True(HeifCodec.MeanError(source.Bgra, back.Bgra).Colour < 3, "the AVIF must look like its source");
    }

    [Fact]
    public void To_avif_keeps_transparency()
    {
        var src = _dir.Write("logo.png", PngWriter.Rgba(80, 60, (x, y) => (200, (byte)(y * 4), 30, (byte)(y < 30 ? 255 : 0))));

        var r = Run("convert.to-avif", src);

        Assert.True(r.Success, r.Message);
        var back = Shown(File.ReadAllBytes(r.OutputPath!));
        Assert.Equal(255, back.Bgra[(10 * 80 + 10) * 4 + 3]);
        Assert.Equal(0, back.Bgra[(50 * 80 + 10) * 4 + 3]);
    }

    [Fact]
    public void To_avif_is_not_offered_for_an_avif() =>
        Assert.Equal(ErrorCode.ContentMismatch, Run("convert.to-avif", AvifFromEngine("x.avif", ReferencePng())).Error);

    // ---- What is refused ----

    [Fact]
    public void Damaged_and_unsupported_files_fail_cleanly_and_leave_nothing()
    {
        var reference = ReferencePng();
        var tiles = HeifSamples.Tiles(_dir.Path, reference, 64, 64, 2, 2);
        var good = new HeifBuilder().Build(tiles, 2, 2, 120, 100);
        var single = HeifSamples.Encode(_dir.Path, reference, "crop=120:96:0:0,format=yuv420p");
        var cases = new (string Name, byte[] Bytes, ErrorCode Expected)[]
        {
            ("truncated.heic", good[..(good.Length - 200)], ErrorCode.DecodeFailed),
            ("empty.heic", [], ErrorCode.DecodeFailed),
            ("sequence.heic", new HeifBuilder { SequenceBox = true }.Build(tiles, 2, 2, 120, 100), ErrorCode.UnsupportedContent),
            ("huge.heic", new HeifBuilder().Build(tiles, 2, 2, 60_000, 60_000), ErrorCode.UnsupportedContent), // the tiles cannot cover it
            ("alpha-grid.heic", new HeifBuilder { Auxiliary = single }.Build(tiles, 2, 2, 120, 100), ErrorCode.UnsupportedContent),
            ("garbage.heic", [.. good[..32], .. Enumerable.Repeat((byte)0x5A, 400)], ErrorCode.DecodeFailed),
            ("lying-size.heic", WithIspe(new HeifBuilder().Build([single], 1, 1, 120, 96), 200, 200), ErrorCode.DecodeFailed),
        };
        foreach (var (name, bytes, expected) in cases)
        {
            var src = _dir.Write(name, bytes);
            var r = Run("convert.to-png", src);
            Assert.False(r.Success, name);
            Assert.True(r.Error == expected || name == "empty.heic" && r.Error == ErrorCode.DecodeFailed, $"{name}: {r.Error} {r.Message}");
            Assert.DoesNotContain(_dir.Names(), n => n.EndsWith(".png", StringComparison.Ordinal) && n.StartsWith(Path.GetFileNameWithoutExtension(name), StringComparison.Ordinal));
            Assert.DoesNotContain(_dir.Names(), n => n.StartsWith("~wintools", StringComparison.Ordinal));
            File.Delete(src);
        }
    }

    /// <summary>The same file with every ispe box claiming another size.</summary>
    private static byte[] WithIspe(byte[] file, int width, int height)
    {
        var copy = (byte[])file.Clone();
        for (var i = 0; i + 20 <= copy.Length; i++)
            if (copy[i] == 'i' && copy[i + 1] == 's' && copy[i + 2] == 'p' && copy[i + 3] == 'e')
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(i + 8), (uint)width);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(i + 12), (uint)height);
            }
        return copy;
    }

    [Fact]
    public void A_grid_larger_than_the_limit_is_refused_before_the_engine_runs()
    {
        var tiles = Enumerable.Repeat(new CodedTile(HeifSamples.Encode(_dir.Path, ReferencePng(), "crop=64:64:0:0,format=yuv420p").Config, "hvc1", 8192, 8192, [1, 2, 3]), 1).ToList();
        var src = _dir.Write("big.heic", new HeifBuilder().Build([tiles[0], tiles[0]], 2, 1, 16_000, 8_000)); // 128 MP

        var r = Run("convert.to-png", src);

        Assert.Equal(ErrorCode.ImageTooLarge, r.Error);
    }

    [Fact]
    public void A_file_named_heic_that_holds_a_jpeg_is_treated_as_the_jpeg_it_is()
    {
        var jpeg = WicCodec.Encode(new RasterImage(32, 24, new byte[32 * 24 * 4].Select((_, i) => (byte)(i % 4 == 3 ? 255 : 90)).ToArray()), ImageFormat.Jpeg);
        var src = _dir.Write("not really.heic", jpeg);

        var r = Run("convert.to-png", src);

        Assert.True(r.Success, r.Message);
        Assert.Equal("not really.png", Path.GetFileName(r.OutputPath));
    }

    [Fact]
    public void The_structure_reader_survives_mutated_files()
    {
        var reference = ReferencePng();
        var file = IphoneLike("seed.heic", reference);
        var bytes = File.ReadAllBytes(file);
        var random = new Random(31);
        for (var n = 0; n < 2000; n++)
        {
            var copy = (byte[])bytes.Clone();
            var limit = Math.Min(copy.Length, 1200); // the boxes, not the coded pictures
            for (var k = 0; k < 1 + n % 4; k++) copy[random.Next(limit)] = (byte)random.Next(256);
            _ = Heif.Read(copy, out _); // never throws: an answer or a reason
        }
        // A few mutated files all the way through the engine: an answer, never a crash, never an output for a refusal.
        for (var n = 0; n < 8; n++)
        {
            var copy = (byte[])bytes.Clone();
            copy[random.Next(64, Math.Min(copy.Length, 1200))] ^= 0xFF;
            var src = _dir.Write($"m{n}.heic", copy);
            var r = Run("convert.to-png", src);
            Assert.True(r.Success || r.OutputPath is null, r.Message);
        }
    }
}
