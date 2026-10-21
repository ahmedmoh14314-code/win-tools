using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Settings;
using WinTools.Host;
using WinTools.Host.Jobs;

namespace WinTools.Worker.Tests;

/// <summary>Findings of the metadata/batch/settings evaluator, each pinned by a test.</summary>
public sealed class MetadataHardeningTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    private static byte[] Sample(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", name));

    [Fact]
    public void Png_with_only_content_credentials_is_stripped_not_reported_as_clean()
    {
        var png = PngWriter.InsertChunkAfterIhdr(Sample("quadrants-64x48.png"), "caBX", "c2pa: creator Jane Doe"u8.ToArray());
        var src = _dir.Write("c.png", png);

        var r = ConversionJob.Run(new ConversionRequest("h1", "metadata.strip", src));

        Assert.True(r.Success, r.Message);
        var outBytes = File.ReadAllBytes(r.OutputPath!);
        Assert.DoesNotContain("caBX", PngWriter.ChunkTypes(outBytes));
        Assert.Empty(Metadata.Residue(outBytes, ImageFormat.Png));
        Assert.Equal(Pixels.Load(src).Bgra, Pixels.Load(r.OutputPath!).Bgra);
    }

    [Fact]
    public void Jpeg_comment_and_data_after_the_end_are_removed_from_a_real_photo()
    {
        var jpeg = Sample("quadrants-64x48.jpg");
        var src = _dir.Write("t.jpg", [.. jpeg, .. "appended secret"u8]);

        var r = ConversionJob.Run(new ConversionRequest("h2", "metadata.strip", src));

        Assert.True(r.Success, r.Message);
        var outBytes = File.ReadAllBytes(r.OutputPath!);
        Assert.Equal(-1, outBytes.AsSpan().IndexOf("secret"u8));
        Assert.Empty(Metadata.Residue(outBytes, ImageFormat.Jpeg));
    }

    [Fact]
    public void Image_info_reports_other_metadata()
    {
        var png = PngWriter.InsertChunkAfterIhdr(Sample("quadrants-64x48.png"), "prVW", [1, 2, 3]);
        var r = ConversionJob.Run(new ConversionRequest("h3", "metadata.info", _dir.Write("o.png", png)));
        Assert.True(r.Info!.HasOtherMetadata);
    }

    [Fact]
    public void Believability_check_survives_hostile_output_paths()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "s.png");
        var request = new ConversionRequest("h4", "convert.to-jpeg", src);
        var sha = new string('A', 64);
        Assert.False(WorkerClient.IsBelievable(request, ConversionResult.Ok("h4", "", sha)));
        Assert.False(WorkerClient.IsBelievable(request, ConversionResult.Ok("h4", Path.Combine(_dir.Path, "s\0.jpg"), sha)));
    }
}

public sealed class SettingsHardeningTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();
    private string PathOf => Path.Combine(_dir.Path, "settings.json");

    [Theory]
    [InlineData("{\"schema\":2,\"outputPolicy\":\"AskEachTime\"}")]   // a value this version cannot parse
    [InlineData("{\"Schema\":2,\"triggerKey\":\"F9\",\"futureField\":1}")] // other casing
    [InlineData("{\"schema\":\"3\",\"futureField\":1}")]                 // schema as a string
    public void Newer_schema_is_never_moved_aside_or_overwritten(string json)
    {
        File.WriteAllText(PathOf, json);
        var store = new SettingsStore(PathOf);
        _ = store.Load();
        Assert.True(store.ReadOnly, store.LastLoad);
        Assert.False(store.Save(new AppSettings()));
        Assert.Equal(json, File.ReadAllText(PathOf));
        Assert.Equal(["settings.json"], _dir.Names());
    }

    [Fact]
    public void A_folder_at_the_settings_path_gives_defaults_and_save_reports_false()
    {
        Directory.CreateDirectory(PathOf);
        var store = new SettingsStore(PathOf);
        Assert.Equal(new AppSettings(), store.Load());
        Assert.False(store.Save(new AppSettings()));
        Assert.True(Directory.Exists(PathOf));
    }

    [Fact]
    public void A_file_where_the_folder_should_be_makes_save_return_false()
    {
        var blocker = _dir.Write("WinTools", [1]);
        Assert.False(new SettingsStore(Path.Combine(blocker, "settings.json")).Save(new AppSettings()));
    }

    [Fact]
    public void Read_only_or_locked_settings_file_keeps_its_content_and_leaves_no_temp()
    {
        var store = new SettingsStore(PathOf);
        Assert.True(store.Save(new AppSettings { JpegQuality = 33 }));
        var before = File.ReadAllText(PathOf);

        File.SetAttributes(PathOf, FileAttributes.ReadOnly);
        try { Assert.False(store.Save(new AppSettings { JpegQuality = 44 })); }
        finally { File.SetAttributes(PathOf, FileAttributes.Normal); }

        using (new FileStream(PathOf, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(store.Save(new AppSettings { JpegQuality = 55 }));

        Assert.Equal(before, File.ReadAllText(PathOf));
        Assert.Equal(["settings.json"], _dir.Names());
    }

    [Fact]
    public void Invalid_output_folder_is_reported_in_the_load_note()
    {
        File.WriteAllText(PathOf, "{\"outputPolicy\":\"Folder\",\"outputFolder\":\"D:/Out\"}");
        var store = new SettingsStore(PathOf);
        Assert.Equal(OutputPolicy.SameFolder, store.Load().OutputPolicy);
        Assert.Contains("output folder invalid", store.LastLoad, StringComparison.Ordinal);
    }
}

public sealed class PoolHardeningTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private static readonly string WorkerExe = Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.exe");
    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Cancellation_stops_the_batch_and_says_cancelled()
    {
        var files = Enumerable.Range(0, 10).Select(i => _dir.CopySample("quadrants-64x48.png", $"k{i}.png")).ToArray();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var results = await new WorkerClient(WorkerExe, TimeSpan.FromSeconds(30), parallelism: 1)
            .RunAsync(files.Select((f, i) => new ConversionRequest($"k{i}", "convert.to-jpeg", f)).ToArray(), cts.Token);
        Assert.All(results, r => Assert.Equal(ErrorCode.Cancelled, r.Error));
        Assert.Equal(10, _dir.Names().Length); // nothing written
    }

#if DEBUG
    [Fact]
    public async Task Scattered_failures_do_not_end_a_long_batch()
    {
        // Every third file crashes its worker: more crashes than MaxRestartsPerLane, but never in a row.
        var files = Enumerable.Range(0, 15).Select(i => _dir.CopySample("quadrants-64x48.png", i % 3 == 0 ? $"boom{i}.png" : $"ok{i}.png")).ToArray();
        var env = new Dictionary<string, string> { ["WINTOOLS_WORKER_FAULT"] = "crash-after-temp", ["WINTOOLS_WORKER_FAULT_MATCH"] = "boom" };
        var results = await new WorkerClient(WorkerExe, TimeSpan.FromSeconds(30), env, parallelism: 1)
            .RunAsync(files.Select((f, i) => new ConversionRequest($"s{i}", "convert.to-jpeg", f)).ToArray(), CancellationToken.None);
        Assert.Equal(5, results.Count(r => r.Error == ErrorCode.WorkerCrashed));
        Assert.Equal(10, results.Count(r => r.Success));
    }

    [Fact]
    public async Task Failures_in_a_row_end_the_lane_and_the_rest_is_reported_not_run()
    {
        var files = Enumerable.Range(0, 8).Select(i => _dir.CopySample("quadrants-64x48.png", $"boom{i}.png")).ToArray();
        var env = new Dictionary<string, string> { ["WINTOOLS_WORKER_FAULT"] = "crash-after-temp" };
        var results = await new WorkerClient(WorkerExe, TimeSpan.FromSeconds(30), env, parallelism: 1)
            .RunAsync(files.Select((f, i) => new ConversionRequest($"n{i}", "convert.to-jpeg", f)).ToArray(), CancellationToken.None);
        Assert.Equal(WorkerClient.MaxRestartsPerLane + 1, results.Count(r => r.Message!.StartsWith("The conversion worker stopped", StringComparison.Ordinal)));
        Assert.All(results.Skip(WorkerClient.MaxRestartsPerLane + 1), r => Assert.StartsWith("Not run", r.Message, StringComparison.Ordinal));
        Assert.DoesNotContain(results, r => r.Success);
    }

    [Fact]
    public async Task A_hang_times_out_only_its_file_and_leaves_no_temp_in_the_output_folder()
    {
        var outDir = Directory.CreateDirectory(Path.Combine(_dir.Path, "out")).FullName;
        var files = new[] { _dir.CopySample("quadrants-64x48.png", "ok0.png"), _dir.CopySample("quadrants-64x48.png", "hang.png"), _dir.CopySample("quadrants-64x48.png", "ok2.png") };
        var env = new Dictionary<string, string> { ["WINTOOLS_WORKER_FAULT"] = "hang-after-temp", ["WINTOOLS_WORKER_FAULT_MATCH"] = "hang" };
        var results = await new WorkerClient(WorkerExe, TimeSpan.FromSeconds(3), env, parallelism: 1)
            .RunAsync(files.Select((f, i) => new ConversionRequest($"t{i}", "convert.to-jpeg", f, outDir)).ToArray(), CancellationToken.None);
        Assert.Equal([true, false, true], results.Select(r => r.Success));
        Assert.Equal(ErrorCode.WorkerTimeout, results[1].Error);
        Assert.Equal(["ok0.jpg", "ok2.jpg"], Directory.GetFiles(outDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }
#endif
}
