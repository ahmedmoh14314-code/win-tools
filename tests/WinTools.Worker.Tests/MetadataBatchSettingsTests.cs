using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Settings;
using WinTools.Host.Jobs;
using WinTools.Windows.Settings;
using WinTools.Worker.Imaging;

namespace WinTools.Worker.Tests;

public sealed class MetadataOperationTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private static int _seq;
    public void Dispose() => _dir.Dispose();

    private static ConversionResult Run(string op, string source) => ConversionJob.Run(new ConversionRequest($"md{Interlocked.Increment(ref _seq)}", op, source));

    private static byte[] Exif(int orientation) =>
        MetadataTestData.RichExif(orientation);

    /// <summary>The real JPEG sample with a rich EXIF block (camera, GPS, orientation), an XMP packet and a comment.</summary>
    private string TaggedJpeg(string name, int orientation)
    {
        var jpeg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", "quadrants-64x48.jpg"));
        static byte[] Seg(byte m, byte[] p) => [0xFF, m, (byte)((p.Length + 2) >> 8), (byte)(p.Length + 2), .. p];
        byte[] extra = [.. Seg(0xE1, [.. "Exif\0\0"u8, .. Exif(orientation)]), .. Seg(0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. "<x:xmpmeta/>"u8]), .. Seg(0xFE, "shot by me"u8.ToArray())];
        return _dir.Write(name, [.. jpeg[..2], .. extra, .. jpeg[2..]]);
    }

    [Fact]
    public void Remove_metadata_from_a_jpeg_is_lossless_and_keeps_the_orientation()
    {
        var src = TaggedJpeg("photo.jpg", orientation: 6);
        var before = Hash.Of(src);
        Assert.True(Metadata.Inspect(File.ReadAllBytes(src), ImageFormat.Jpeg).Gps);

        var r = Run("metadata.strip", src);

        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(_dir.Path, "photo (no metadata).jpg"), r.OutputPath);
        var outBytes = File.ReadAllBytes(r.OutputPath!);
        Assert.False(Metadata.Inspect(outBytes, ImageFormat.Jpeg).AnyRemovable);
        Assert.Equal(-1, outBytes.AsSpan().IndexOf("shot by me"u8));
        // Same scan data: the entropy-coded bytes are copied, not re-encoded.
        var original = File.ReadAllBytes(src);
        var sos = original.AsSpan().IndexOf([(byte)0xFF, (byte)0xDA]);
        Assert.True(outBytes.AsSpan().IndexOf(original.AsSpan(sos)) > 0);
        // Still shown rotated: the orientation tag survived on its own.
        var (img, orientation) = ImagePipeline.Decode(outBytes, ImageFormat.Jpeg);
        Assert.Equal((6, 64, 48), (orientation, img.Width, img.Height));
        Assert.Equal(before, Hash.Of(src));
    }

    [Fact]
    public void Remove_metadata_from_png_and_webp()
    {
        var png = PngWriter.InsertChunkAfterIhdr(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", "quadrants-64x48.png")), "tEXt", "Author\0me"u8.ToArray());
        var r = Run("metadata.strip", _dir.Write("p.png", png));
        Assert.True(r.Success, r.Message);
        Assert.Equal(Pixels.Load(Path.Combine(_dir.Path, "p.png")).Bgra, Pixels.Load(r.OutputPath!).Bgra);
        Assert.DoesNotContain("tEXt", PngWriter.ChunkTypes(File.ReadAllBytes(r.OutputPath!)));

        var webp = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", "quadrants-64x48.webp"));
        var tagged = SkiaCodec.WithIccProfile(webp, File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"spool\drivers\color\sRGB Color Space Profile.icm")), 64, 48, false);
        var withExif = WebPWithExif(tagged, Exif(1));
        var w = Run("metadata.strip", _dir.Write("w.webp", withExif));
        Assert.True(w.Success, w.Message);
        var outBytes = File.ReadAllBytes(w.OutputPath!);
        var found = Metadata.Inspect(outBytes, ImageFormat.WebP);
        Assert.False(found.AnyRemovable);
        Assert.True(found.Icc); // colour stays
    }

    private static byte[] WebPWithExif(byte[] webp, byte[] exif)
    {
        var body = webp.AsSpan(8).ToArray().ToList();
        body[12] |= 0x08; // VP8X EXIF flag (the file already has VP8X from the ICC step)
        body.AddRange("EXIF"u8.ToArray()); body.AddRange(BitConverter.GetBytes(exif.Length)); body.AddRange(exif);
        if (exif.Length % 2 == 1) body.Add(0);
        return [.. "RIFF"u8, .. BitConverter.GetBytes(body.Count), .. body];
    }

    [Fact]
    public void File_without_metadata_reports_nothing_to_do()
    {
        var r = Run("metadata.strip", _dir.CopySample("quadrants-64x48.png", "clean.png"));
        Assert.Equal(ErrorCode.NothingToDo, r.Error);
        Assert.Equal(["clean.png"], _dir.Names());
    }

    [Fact]
    public void Image_info_reports_facts_and_writes_nothing()
    {
        var src = TaggedJpeg("photo.jpg", orientation: 6);
        var r = Run("metadata.info", src);
        Assert.True(r.Success, r.Message);
        Assert.Null(r.OutputPath);
        var i = r.Info!;
        Assert.Equal(("JPEG", 48, 64, 6, true, true, true, true), (i.Format, i.Width, i.Height, i.Orientation, i.HasExif, i.HasGps, i.HasXmp, i.HasTextOrComments));
        Assert.Equal(new FileInfo(src).Length, i.FileBytes);
        Assert.Equal(["photo.jpg"], _dir.Names());
        Assert.True(WorkerClient.IsBelievable(new ConversionRequest("x", "metadata.info", src), r with { Id = "x" }));
    }
}

public sealed class BatchTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private static readonly string WorkerExe = Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.exe");
    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Hundred_small_files_convert_in_parallel_with_one_result_each_in_order()
    {
        var files = Enumerable.Range(0, 120).Select(i => _dir.CopySample("quadrants-64x48.png", $"img {i:000} ✓.png")).ToArray();
        var requests = files.Select((f, i) => new ConversionRequest($"b{i}", "convert.to-webp", f)).ToArray();

        var results = await new WorkerClient(WorkerExe, TimeSpan.FromSeconds(30), parallelism: 4).RunAsync(requests, CancellationToken.None);

        Assert.Equal(requests.Select(r => r.Id), results.Select(r => r.Id));
        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(240, _dir.Names().Length);
    }

    [Fact]
    public async Task One_corrupt_file_does_not_affect_the_others()
    {
        var good = Enumerable.Range(0, 9).Select(i => _dir.CopySample("quadrants-64x48.png", $"g{i}.png")).ToList();
        var bad = _dir.Write("bad.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);
        good.Insert(4, bad);
        var results = await new WorkerClient(WorkerExe, TimeSpan.FromSeconds(30)).RunAsync(good.Select((f, i) => new ConversionRequest($"c{i}", "convert.to-jpeg", f)).ToArray(), CancellationToken.None);
        Assert.Equal(9, results.Count(r => r.Success));
        Assert.Equal(ErrorCode.DecodeFailed, results[4].Error);
    }

    [Fact]
    public async Task Same_stem_in_one_folder_gets_distinct_outputs_even_in_parallel()
    {
        var a = _dir.CopySample("quadrants-64x48.png", "same.png");
        var b = _dir.CopySample("quadrants-64x48.jpg", "same.jpg");
        var c = _dir.CopySample("quadrants-64x48.bmp", "same.bmp");
        var results = await new WorkerClient(WorkerExe, TimeSpan.FromSeconds(30), parallelism: 3)
            .RunAsync([new("d1", "convert.to-webp", a), new("d2", "convert.to-webp", b), new("d3", "convert.to-webp", c)], CancellationToken.None);
        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(3, results.Select(r => r.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("same (2).webp", _dir.Names());
    }

#if DEBUG
    [Fact]
    public async Task A_worker_crash_on_one_file_fails_only_that_file()
    {
        var files = Enumerable.Range(0, 6).Select(i => _dir.CopySample("quadrants-64x48.png", i == 2 ? "boom.png" : $"ok{i}.png")).ToArray();
        var env = new Dictionary<string, string> { ["WINTOOLS_WORKER_FAULT"] = "crash-after-temp", ["WINTOOLS_WORKER_FAULT_MATCH"] = "boom" };
        var results = await new WorkerClient(WorkerExe, TimeSpan.FromSeconds(30), env, parallelism: 1)
            .RunAsync(files.Select((f, i) => new ConversionRequest($"x{i}", "convert.to-jpeg", f)).ToArray(), CancellationToken.None);
        Assert.Equal(ErrorCode.WorkerCrashed, results[2].Error);
        Assert.Equal(5, results.Count(r => r.Success));
        Assert.DoesNotContain(_dir.Names(), n => n.StartsWith("~wintools-", StringComparison.Ordinal) || n.StartsWith("boom.jpg", StringComparison.Ordinal));
    }
#endif

    [Fact]
    public async Task Configured_output_folder_receives_the_outputs()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "p.png");
        var outDir = Directory.CreateDirectory(Path.Combine(_dir.Path, "out")).FullName;
        var results = await new WorkerClient(WorkerExe, TimeSpan.FromSeconds(30))
            .RunAsync([new("o1", "convert.to-jpeg", src, outDir, JpegQuality: 50), new("o2", "resize.percent-50", src, outDir)], CancellationToken.None);
        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(Path.Combine(outDir, "p.jpg"), results[0].OutputPath);
        Assert.Equal(Path.Combine(outDir, "p (resized 50%).png"), results[1].OutputPath);
        Assert.Equal(["p.png"], _dir.Names());

        var missing = ConversionJob.Run(new ConversionRequest("o3", "convert.to-jpeg", src, Path.Combine(_dir.Path, "nope")));
        Assert.Equal(ErrorCode.OutputDirectoryNotWritable, missing.Error);
    }

    [Fact]
    public void Jpeg_quality_from_the_settings_changes_the_output()
    {
        var png = _dir.Write("n.png", PngWriter.Rgba(300, 200, (x, y) => ((byte)(x ^ y), (byte)(x * 3), (byte)(y * 7), 255)));
        var low = ConversionJob.Run(new ConversionRequest("q1", "convert.to-jpeg", png, JpegQuality: 20));
        var lowSize = new FileInfo(low.OutputPath!).Length;
        File.Delete(low.OutputPath!);
        var high = ConversionJob.Run(new ConversionRequest("q2", "convert.to-jpeg", png, JpegQuality: 98));
        Assert.True(lowSize * 2 < new FileInfo(high.OutputPath!).Length);
    }
}

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();
    private string PathOf => Path.Combine(_dir.Path, "settings.json");

    [Fact]
    public void Missing_file_gives_defaults_and_save_round_trips()
    {
        var store = new SettingsStore(PathOf);
        Assert.Equal(new AppSettings(), store.Load());
        Assert.True(store.Save(new AppSettings { TriggerKey = "F8", JpegQuality = 70 }));
        var loaded = new SettingsStore(PathOf).Load();
        Assert.Equal(("F8", 70), (loaded.TriggerKey, loaded.JpegQuality));
        Assert.Equal(["settings.json"], _dir.Names()); // no temp left behind
    }

    [Fact]
    public void Corrupt_file_is_moved_aside_and_defaults_are_used()
    {
        File.WriteAllText(PathOf, "{ this is not json");
        var store = new SettingsStore(PathOf);
        Assert.Equal(new AppSettings(), store.Load());
        Assert.Contains("corrupt", store.LastLoad, StringComparison.Ordinal);
        Assert.Single(_dir.Names(), n => n.StartsWith("settings.corrupt-", StringComparison.Ordinal));
        Assert.True(store.Save(new AppSettings()));
    }

    [Fact]
    public void Newer_schema_is_read_but_never_overwritten()
    {
        File.WriteAllText(PathOf, "{\"schema\":99,\"triggerKey\":\"F9\",\"futureField\":1}");
        var store = new SettingsStore(PathOf);
        Assert.Equal("F9", store.Load().TriggerKey);
        Assert.True(store.ReadOnly);
        Assert.False(store.Save(new AppSettings()));
        Assert.Contains("futureField", File.ReadAllText(PathOf), StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_saves_leave_one_file_and_no_temp()
    {
        var store = new SettingsStore(PathOf);
        for (var i = 1; i <= 20; i++) Assert.True(store.Save(new AppSettings { JpegQuality = i }));
        Assert.Equal(20, new SettingsStore(PathOf).Load().JpegQuality);
        Assert.Equal(["settings.json"], _dir.Names());
    }
}
