using System.Security.AccessControl;
using System.Security.Principal;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Tests;

/// <summary>Real files through the real WIC codecs, in-process (ConversionJob is what the worker runs per request).</summary>
public sealed class ConversionJobTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private static int _seq;

    public void Dispose() => _dir.Dispose();

    private static ConversionResult Run(Operation op, string source, JobHooks? hooks = null) =>
        ConversionJob.Run(new ConversionRequest($"t{Interlocked.Increment(ref _seq)}", op.Id, source), hooks);

    private void AssertNoLeftovers(params string[] expected) => Assert.Equal(expected.Order(StringComparer.Ordinal), _dir.Names());

    private static void AssertColor((int R, int G, int B, int A) actual, (int R, int G, int B) expected, int tolerance)
    {
        Assert.True(Math.Abs(actual.R - expected.R) <= tolerance && Math.Abs(actual.G - expected.G) <= tolerance && Math.Abs(actual.B - expected.B) <= tolerance,
            $"pixel {actual} is not within {tolerance} of {expected}");
    }

    private static void AssertQuadrants(Pixels p, int tolerance)
    {
        AssertColor(p.At(8, 6), (255, 0, 0), tolerance);                       // top-left red
        AssertColor(p.At(p.Width - 9, 6), (0, 255, 0), tolerance);             // top-right green
        AssertColor(p.At(8, p.Height - 7), (0, 0, 255), tolerance);            // bottom-left blue
        AssertColor(p.At(p.Width - 9, p.Height - 7), (255, 255, 0), tolerance); // bottom-right yellow
    }

    [Fact]
    public void PngToJpeg_writes_a_decodable_jpeg_next_to_the_source_and_leaves_the_source_untouched()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "photo.png");
        var before = Hash.Of(src);

        var r = Run(OperationCatalog.PngToJpeg, src);

        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(_dir.Path, "photo.jpg"), r.OutputPath);
        var px = Pixels.Load(r.OutputPath!);
        Assert.Equal((64, 48), (px.Width, px.Height));
        AssertQuadrants(px, tolerance: 40);
        Assert.Equal(0xFF, File.ReadAllBytes(r.OutputPath!)[0]);
        Assert.Equal(before, Hash.Of(src));
        AssertNoLeftovers("photo.jpg", "photo.png");
        Assert.False(File.GetAttributes(r.OutputPath!).HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void JpegToPng_writes_a_decodable_png()
    {
        var src = _dir.CopySample("quadrants-64x48.jpg", "photo.JPG");
        var before = Hash.Of(src);

        var r = Run(OperationCatalog.JpegToPng, src);

        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(_dir.Path, "photo.png"), r.OutputPath);
        var px = Pixels.Load(r.OutputPath!);
        Assert.Equal((64, 48), (px.Width, px.Height));
        AssertQuadrants(px, tolerance: 40);
        Assert.Equal(before, Hash.Of(src));
        AssertNoLeftovers("photo.JPG", "photo.png");
    }

    [Fact]
    public void Transparent_png_is_composited_onto_white()
    {
        var src = _dir.CopySample("alpha-32x32.png");

        var r = Run(OperationCatalog.PngToJpeg, src);

        Assert.True(r.Success, r.Message);
        var px = Pixels.Load(r.OutputPath!);
        AssertColor(px.At(4, 16), (255, 255, 255), tolerance: 6);  // alpha 0 -> white, not black
        AssertColor(px.At(27, 16), (127, 127, 255), tolerance: 12); // 50% blue over white
    }

    [Fact]
    public void Compositing_formula_is_exact_at_the_extremes()
    {
        byte[] bgra = [10, 20, 30, 0, 10, 20, 30, 255, 0, 0, 255, 128];
        var bgr = new byte[9];
        Imaging.ImageTranscoder.CompositeOnWhite(bgra, bgr);
        Assert.Equal(new byte[] { 255, 255, 255, 10, 20, 30, 127, 127, 255 }, bgr);
    }

    [Theory]
    [InlineData((ushort)1, 64, 48)]
    [InlineData((ushort)2, 64, 48)]
    [InlineData((ushort)3, 64, 48)]
    [InlineData((ushort)4, 64, 48)]
    [InlineData((ushort)5, 48, 64)]
    [InlineData((ushort)6, 48, 64)]
    [InlineData((ushort)7, 48, 64)]
    [InlineData((ushort)8, 48, 64)]
    public void Jpeg_exif_orientation_is_applied_to_the_png_pixels(ushort orientation, int width, int height)
    {
        var jpeg = ExifWriter.WithOrientation(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", "quadrants-64x48.jpg")), orientation);
        var src = _dir.Write("rotated.jpg", jpeg);

        var r = Run(OperationCatalog.JpegToPng, src);

        Assert.True(r.Success, r.Message);
        var px = Pixels.Load(r.OutputPath!);
        Assert.Equal((width, height), (px.Width, px.Height));
        // Stored quadrants: TL red, TR green, BL blue, BR yellow. Where each one must appear after display orientation:
        var (red, green, blue, yellow) = orientation switch
        {
            1 => ("TL", "TR", "BL", "BR"),
            2 => ("TR", "TL", "BR", "BL"),
            3 => ("BR", "BL", "TR", "TL"),
            4 => ("BL", "BR", "TL", "TR"),
            5 => ("TL", "BL", "TR", "BR"), // transpose
            6 => ("TR", "BR", "TL", "BL"), // 90° clockwise
            7 => ("BR", "TR", "BL", "TL"), // transverse
            _ => ("BL", "TL", "BR", "TR"), // 270° clockwise
        };
        (int, int) Corner(string c) => c switch
        {
            "TL" => (6, 6), "TR" => (px.Width - 7, 6), "BL" => (6, px.Height - 7), _ => (px.Width - 7, px.Height - 7),
        };
        void At(string corner, (int, int, int) rgb) { var (x, y) = Corner(corner); AssertColor(px.At(x, y), rgb, 40); }
        At(red, (255, 0, 0)); At(green, (0, 255, 0)); At(blue, (0, 0, 255)); At(yellow, (255, 255, 0));
    }

    [Fact]
    public void Truncated_png_fails_cleanly()
    {
        var full = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", "quadrants-64x48.png"));
        var src = _dir.Write("broken.png", full[..(full.Length / 2)]);
        var before = Hash.Of(src);

        var r = Run(OperationCatalog.PngToJpeg, src);

        Assert.False(r.Success);
        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        Assert.Null(r.OutputPath);
        Assert.Equal(before, Hash.Of(src));
        AssertNoLeftovers("broken.png");
    }

    [Fact]
    public void Corrupt_jpeg_fails_cleanly()
    {
        var garbage = new byte[2048];
        new Random(7).NextBytes(garbage);
        var src = _dir.Write("broken.jpg", [0xFF, 0xD8, 0xFF, 0xE0, .. garbage]);

        var r = Run(OperationCatalog.JpegToPng, src);

        Assert.False(r.Success);
        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        AssertNoLeftovers("broken.jpg");
    }

    [Fact]
    public void Truncated_jpeg_fails_cleanly()
    {
        // The JPEG decoder pads a truncated scan instead of failing; the structural check must catch it.
        var full = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", "quadrants-64x48.jpg"));
        var src = _dir.Write("cut.jpg", full[..(full.Length * 2 / 3)]);

        var r = Run(OperationCatalog.JpegToPng, src);

        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        AssertNoLeftovers("cut.jpg");
    }

    [Fact]
    public void Mislabelled_file_is_refused_by_content()
    {
        var src = _dir.CopySample("quadrants-64x48.jpg", "really-a-jpeg.png");

        var r = Run(OperationCatalog.PngToJpeg, src);

        Assert.Equal(ErrorCode.ContentMismatch, r.Error);
        AssertNoLeftovers("really-a-jpeg.png");
    }

    [Fact]
    public void Zero_byte_file_fails_cleanly()
    {
        var src = _dir.Write("empty.png", []);
        var r = Run(OperationCatalog.PngToJpeg, src);
        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        AssertNoLeftovers("empty.png");
    }

    [Fact]
    public void Huge_declared_dimensions_are_refused_before_decoding()
    {
        var src = _dir.Write("bomb.png", PngWriter.HeaderOnly(60_000, 60_000));
        var r = Run(OperationCatalog.PngToJpeg, src);
        Assert.Equal(ErrorCode.ImageTooLarge, r.Error);
        AssertNoLeftovers("bomb.png");
    }

    [Fact]
    public void Unicode_spaces_and_emoji_in_the_name_are_preserved()
    {
        const string name = "ملف عربي 日本語 ✓ émoji 🚀 (copy).png";
        var src = _dir.CopySample("quadrants-64x48.png", name);

        var r = Run(OperationCatalog.PngToJpeg, src);

        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(_dir.Path, "ملف عربي 日本語 ✓ émoji 🚀 (copy).jpg"), r.OutputPath);
        var px = Pixels.Load(r.OutputPath!);
        Assert.Equal((64, 48), (px.Width, px.Height));
    }

    [Fact]
    public void Long_paths_beyond_MAX_PATH_work()
    {
        var deep = Path.Combine(_dir.Path, new string('d', 120), new string('e', 120), new string('f', 40));
        Directory.CreateDirectory(deep);
        var src = Path.Combine(deep, "photo.png");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "samples", "quadrants-64x48.png"), src);
        Assert.True(src.Length > 260);

        var r = Run(OperationCatalog.PngToJpeg, src);

        Assert.True(r.Success, r.Message);
        Assert.True(File.Exists(r.OutputPath));
    }

    [Fact]
    public void Existing_destination_is_never_overwritten()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "photo.png");
        var existing = _dir.Write("photo.jpg", "not an image, must survive"u8.ToArray());
        _dir.Write("photo (1).jpg", "also taken"u8.ToArray());
        var existingHash = Hash.Of(existing);

        var r = Run(OperationCatalog.PngToJpeg, src);

        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(_dir.Path, "photo (2).jpg"), r.OutputPath);
        Assert.Equal(existingHash, Hash.Of(existing));
        Assert.Equal("also taken", File.ReadAllText(Path.Combine(_dir.Path, "photo (1).jpg")));
    }

    [Fact]
    public void Destination_created_concurrently_is_not_overwritten()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "photo.png");
        var racer = Path.Combine(_dir.Path, "photo.jpg");

        var r = Run(OperationCatalog.PngToJpeg, src, new JobHooks(BeforeFinalize: () => File.WriteAllText(racer, "created by someone else")));

        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(_dir.Path, "photo (1).jpg"), r.OutputPath);
        Assert.Equal("created by someone else", File.ReadAllText(racer));
        AssertNoLeftovers("photo (1).jpg", "photo.jpg", "photo.png");
    }

    [Fact]
    public void Output_that_fails_validation_is_deleted_and_reported()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "photo.png");

        var r = Run(OperationCatalog.PngToJpeg, src, new JobHooks(AfterTempWritten: temp =>
        {
            var b = File.ReadAllBytes(temp);
            File.SetAttributes(temp, FileAttributes.Normal);
            File.WriteAllBytes(temp, b[..(b.Length / 3)]);
        }));

        Assert.False(r.Success);
        Assert.Equal(ErrorCode.OutputValidationFailed, r.Error);
        AssertNoLeftovers("photo.png");
    }

    [Fact]
    public void Missing_source_is_reported()
    {
        var r = Run(OperationCatalog.PngToJpeg, Path.Combine(_dir.Path, "gone.png"));
        Assert.Equal(ErrorCode.SourceNotFound, r.Error);
        AssertNoLeftovers();
    }

    [Fact]
    public void Source_deleted_after_it_was_read_still_yields_a_complete_output_or_a_clean_failure()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "photo.png");

        var r = Run(OperationCatalog.PngToJpeg, src, new JobHooks(AfterSourceRead: () => File.Delete(src)));

        // The bytes were read in full before the deletion, so the conversion of what the user dropped completes.
        Assert.True(r.Success, r.Message);
        var px = Pixels.Load(r.OutputPath!);
        Assert.Equal((64, 48), (px.Width, px.Height));
        AssertNoLeftovers("photo.jpg");
    }

    [Fact]
    public void Unwritable_destination_folder_fails_without_touching_the_source()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "photo.png");
        var before = Hash.Of(src);
        var info = new DirectoryInfo(_dir.Path);
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles | FileSystemRights.WriteData, AccessControlType.Deny);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        try
        {
            var r = Run(OperationCatalog.PngToJpeg, src);

            Assert.False(r.Success);
            Assert.Equal(ErrorCode.OutputDirectoryNotWritable, r.Error);
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            info.SetAccessControl(acl);
        }
        Assert.Equal(before, Hash.Of(src));
        AssertNoLeftovers("photo.png");
    }

    [Theory]
    [InlineData("bad id!", "convert.png-to-jpeg", ErrorCode.InvalidRequest)]
    [InlineData("ok", "convert.png-to-webp", ErrorCode.UnsupportedOperation)]
    public void Invalid_requests_are_refused(string id, string op, ErrorCode expected)
    {
        var src = _dir.CopySample("quadrants-64x48.png");
        var r = ConversionJob.Run(new ConversionRequest(id, op, src));
        Assert.Equal(expected, r.Error);
    }

    [Fact]
    public void Png_with_intact_chunks_but_cut_image_data_fails()
    {
        // Valid chunk lengths and CRCs; the zlib stream inside IDAT stops at 30 %. The PNG decoder would fill the rest.
        var raw = Enumerable.Range(0, 64 * 64).Select(i => ((byte)i, (byte)(i >> 3), (byte)(i >> 6), (byte)255)).ToArray();
        var good = PngWriter.Rgba(64, 64, (x, y) => raw[y * 64 + x]);
        var src = _dir.Write("cut.png", PngWriter.WithTruncatedImageData(good, 0.3));

        var r = Run(OperationCatalog.PngToJpeg, src);

        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        Assert.Contains("truncated", r.Message, StringComparison.Ordinal);
        AssertNoLeftovers("cut.png");
    }

    [Fact]
    public void Folder_with_a_trailing_dot_is_refused_instead_of_converting_its_twin()
    {
        var twin = Directory.CreateDirectory(Path.Combine(_dir.Path, "dir")).FullName;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "samples", "quadrants-64x48.png"), Path.Combine(twin, "photo.png"));
        Directory.CreateDirectory(@"\\?\" + Path.Combine(_dir.Path, "dir."));
        var dotted = Path.Combine(_dir.Path, "dir.") + @"\photo.png";
        File.Copy(Path.Combine(AppContext.BaseDirectory, "samples", "alpha-32x32.png"), @"\\?\" + dotted);

        var r = Run(OperationCatalog.PngToJpeg, dotted);

        Assert.Equal(ErrorCode.InvalidRequest, r.Error);
        Assert.Equal(["photo.png"], Directory.GetFiles(twin).Select(Path.GetFileName));
        Directory.Delete(@"\\?\" + Path.Combine(_dir.Path, "dir."), recursive: true);
    }

    [Fact]
    public void Lone_surrogate_in_the_name_is_refused()
    {
        var r = Run(OperationCatalog.PngToJpeg, Path.Combine(_dir.Path, "a" + '\uD800' + ".png"));
        Assert.Equal(ErrorCode.InvalidRequest, r.Error);
    }

    [Fact]
    public void A_pre_existing_file_with_the_temp_name_is_never_deleted()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "photo.png");
        var squatter = _dir.Write("~wintools-abc.tmp", "not ours"u8.ToArray());

        var r = ConversionJob.Run(new ConversionRequest("abc", OperationCatalog.PngToJpeg.Id, src));

        Assert.False(r.Success);
        Assert.Equal("not ours", File.ReadAllText(squatter));
        AssertNoLeftovers("photo.png", "~wintools-abc.tmp");
    }

    [Fact]
    public void All_candidate_names_taken_reports_NoFreeOutputName()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "p.png");
        foreach (var name in WinTools.Core.Output.OutputNaming.Candidates(src, WinTools.Core.Files.ImageFormat.Jpeg)) File.WriteAllBytes(name, [7]);

        var r = Run(OperationCatalog.PngToJpeg, src);

        Assert.Equal(ErrorCode.NoFreeOutputName, r.Error);
        Assert.Equal(WinTools.Core.Output.OutputNaming.MaxCollisionIndex + 2, Directory.GetFiles(_dir.Path).Length); // nothing added
        Assert.All(Directory.GetFiles(_dir.Path, "*.jpg"), f => Assert.Equal([7], File.ReadAllBytes(f)));
    }

    [Fact]
    public void Input_larger_than_the_limit_is_refused_without_reading_it()
    {
        var src = Path.Combine(_dir.Path, "big.png");
        using (var fs = new FileStream(src, FileMode.CreateNew)) { fs.Write([0x89, 0x50, 0x4E, 0x47]); fs.SetLength(ConversionJob.MaxInputBytes + 1); }

        var r = Run(OperationCatalog.PngToJpeg, src);

        Assert.Equal(ErrorCode.InputTooLarge, r.Error);
        AssertNoLeftovers("big.png");
    }

    [Fact]
    public void Source_locked_by_another_process_is_reported_as_not_readable()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "photo.png");
        using var exclusive = new FileStream(src, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var r = Run(OperationCatalog.PngToJpeg, src);

        Assert.Equal(ErrorCode.SourceNotReadable, r.Error);
    }

    [Fact]
    public void Rotated_12_megapixel_jpeg_converts_in_seconds_not_minutes()
    {
        // Evaluator B1: a flip-rotator directly over the JPEG decoder re-reads scanlines per pixel (~5 minutes here).
        var png = _dir.Write("big.png", PngWriter.Rgba(4000, 3000, (x, y) => ((byte)x, (byte)y, (byte)(x ^ y), 255)));
        var jpg = Run(OperationCatalog.PngToJpeg, png);
        Assert.True(jpg.Success, jpg.Message);
        var rotated = _dir.Write("portrait.jpg", ExifWriter.WithOrientation(File.ReadAllBytes(jpg.OutputPath!), 6));

        var started = System.Diagnostics.Stopwatch.StartNew();
        var r = Run(OperationCatalog.JpegToPng, rotated);

        Assert.True(r.Success, r.Message);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"took {started.Elapsed}");
        var px = Pixels.Load(r.OutputPath!);
        Assert.Equal((3000, 4000), (px.Width, px.Height));
    }

    [Fact]
    public void Relative_source_path_is_refused()
    {
        var r = ConversionJob.Run(new ConversionRequest("x1", OperationCatalog.PngToJpeg.Id, "photo.png"));
        Assert.Equal(ErrorCode.InvalidRequest, r.Error);
    }
}
