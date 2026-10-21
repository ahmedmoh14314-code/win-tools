using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;

namespace WinTools.Worker.Tests;

/// <summary>The worker as a separate process, driven by the host's client: batches, crash, hang, cleanup.</summary>
public sealed class WorkerProcessTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private static readonly string WorkerExe = Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.exe");

    public void Dispose() => _dir.Dispose();

    private static WorkerClient Client(TimeSpan? timeout = null, string? fault = null) =>
        new(WorkerExe, timeout ?? TimeSpan.FromSeconds(30), fault is null ? null : new Dictionary<string, string> { ["WINTOOLS_WORKER_FAULT"] = fault });

    [Fact]
    public async Task Batch_of_png_and_jpeg_converts_in_one_worker_process()
    {
        var png = _dir.CopySample("quadrants-64x48.png", "a.png");
        var jpg = _dir.CopySample("quadrants-64x48.jpg", "b.jpg");

        var results = await Client().RunAsync(
            [new("r1", OperationCatalog.Convert(WinTools.Core.Files.ImageFormat.Jpeg).Id, png), new("r2", OperationCatalog.Convert(WinTools.Core.Files.ImageFormat.Png).Id, jpg)], CancellationToken.None);

        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(Path.Combine(_dir.Path, "a.jpg"), results[0].OutputPath);
        Assert.Equal(Path.Combine(_dir.Path, "b.png"), results[1].OutputPath);
        Assert.Equal(["a.jpg", "a.png", "b.jpg", "b.png"], _dir.Names());
    }

    [Fact]
    public async Task Worker_failure_result_crosses_the_process_boundary_with_its_code()
    {
        var src = _dir.CopySample("quadrants-64x48.jpg", "fake.png");
        var results = await Client().RunAsync([new("r1", OperationCatalog.Convert(WinTools.Core.Files.ImageFormat.Jpeg).Id, src)], CancellationToken.None);
        Assert.Equal(ErrorCode.ContentMismatch, results.Single().Error);
    }

#if DEBUG
    [Fact]
    public async Task Worker_crash_after_writing_the_temp_file_leaves_no_output_and_no_temp()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "photo.png");
        var second = _dir.CopySample("quadrants-64x48.png", "second.png");
        var before = Hash.Of(src);

        var results = await Client(fault: "crash-after-temp").RunAsync(
            [new("r1", OperationCatalog.Convert(WinTools.Core.Files.ImageFormat.Jpeg).Id, src), new("r2", OperationCatalog.Convert(WinTools.Core.Files.ImageFormat.Jpeg).Id, second)], CancellationToken.None);

        Assert.Equal(ErrorCode.WorkerCrashed, results[0].Error);
        Assert.Equal(ErrorCode.WorkerCrashed, results[1].Error);
        Assert.Equal(before, Hash.Of(src));
        Assert.Equal(["photo.png", "second.png"], _dir.Names());
    }

    [Fact]
    public async Task Hung_worker_is_killed_at_the_timeout_and_cleaned_up()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "photo.png");

        var started = DateTime.UtcNow;
        var results = await Client(TimeSpan.FromSeconds(3), "hang-after-temp").RunAsync([new("r1", OperationCatalog.Convert(WinTools.Core.Files.ImageFormat.Jpeg).Id, src)], CancellationToken.None);

        Assert.Equal(ErrorCode.WorkerTimeout, results.Single().Error);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
        Assert.Equal(["photo.png"], _dir.Names());
    }
#endif

    [Fact]
    public async Task Jpeg_whose_scan_is_cut_before_a_valid_EOI_fails_instead_of_inventing_pixels()
    {
        // Markers are complete (SOI ... SOS, half the scan, EOI), so only the codec notices; it says so on stderr only.
        var full = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", "quadrants-64x48.jpg"));
        var sos = full.AsSpan().IndexOf([(byte)0xFF, (byte)0xDA]);
        var scanStart = sos + 2 + (full[sos + 2] << 8 | full[sos + 3]);
        var cut = full[..(scanStart + (full.Length - 2 - scanStart) / 2)];
        var src = _dir.Write("cut.jpg", [.. cut, 0xFF, 0xD9]);

        var results = await Client().RunAsync([new("r1", OperationCatalog.Convert(WinTools.Core.Files.ImageFormat.Png).Id, src)], CancellationToken.None);

        Assert.Equal(ErrorCode.DecodeFailed, results.Single().Error);
        Assert.Equal(["cut.jpg"], _dir.Names());
    }

    [Fact]
    public async Task Missing_worker_executable_is_a_structured_failure()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "photo.png");
        var client = new WorkerClient(Path.Combine(_dir.Path, "no-such-worker.exe"), TimeSpan.FromSeconds(5));
        var results = await client.RunAsync([new("r1", OperationCatalog.Convert(WinTools.Core.Files.ImageFormat.Jpeg).Id, src)], CancellationToken.None);
        Assert.Equal(ErrorCode.WorkerCrashed, results.Single().Error);
    }

    [Fact]
    public void Success_claims_outside_the_naming_policy_are_not_believed()
    {
        var src = _dir.CopySample("quadrants-64x48.png", "photo.png");
        var elsewhere = _dir.Write("other.jpg", [1]);
        var req = new ConversionRequest("r1", OperationCatalog.Convert(WinTools.Core.Files.ImageFormat.Jpeg).Id, src);
        Assert.False(WorkerClient.IsBelievable(req, ConversionResult.Ok("r1", elsewhere, Hash.Of(elsewhere))));
        Assert.False(WorkerClient.IsBelievable(req, ConversionResult.Ok("r1", Path.Combine(_dir.Path, "photo.jpg"), new string('0', 64)))); // does not exist
        var real = _dir.Write("photo (3).jpg", [1, 2, 3]);
        Assert.False(WorkerClient.IsBelievable(req, ConversionResult.Ok("r1", real, new string('0', 64)))); // a file that is not the worker's output
        Assert.True(WorkerClient.IsBelievable(req, ConversionResult.Ok("r1", real, Hash.Of(real))));
    }

    [Fact]
    public void Host_assembly_does_not_reference_the_worker_assembly()
    {
        var host = typeof(WorkerClient).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain("WinTools.Worker", host);
        var core = typeof(OperationCatalog).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain(core, n => n!.StartsWith("WinTools.", StringComparison.Ordinal));
    }
}

/// <summary>SEC-003: only Microsoft's built-in codecs are used.</summary>
public sealed class CodecVendorTests
{
    [Theory]
    [InlineData("e018945b-aa86-4008-9bd4-6777a1e40c11", true)]  // PNG decoder
    [InlineData("9456a480-e88b-43ea-9e73-0b2d9b71b1ca", true)]  // JPEG decoder
    [InlineData("27949969-876a-41d7-9447-568f6a35a4dc", false)] // PNG encoder
    [InlineData("1a34f5c1-4a5a-46dc-b644-1f4567e7a676", false)] // JPEG encoder
    [InlineData("6b462062-7cbf-400d-9fdb-813dd10f2778", true)]  // BMP decoder
    [InlineData("69be8bb4-d66d-47c8-865a-ed1589433782", false)] // BMP encoder
    [InlineData("b54e85d9-fe23-499f-8b88-6acea713752b", true)]  // TIFF decoder
    [InlineData("0131be10-2001-4c5f-a9b0-cc88fab64ce8", false)] // TIFF encoder
    public void Codecs_created_by_clsid_are_the_Microsoft_ones(string clsid, bool decoder)
    {
        var id = new Guid(clsid);
        Assert.Contains(id, new[] { Imaging.WicIds.PngDecoder, Imaging.WicIds.JpegDecoder, Imaging.WicIds.PngEncoder, Imaging.WicIds.JpegEncoder,
            Imaging.WicIds.BmpDecoder, Imaging.WicIds.BmpEncoder, Imaging.WicIds.TiffDecoder, Imaging.WicIds.TiffEncoder });
        var factory = Imaging.WicNative.Create<Imaging.IWICImagingFactory>(Imaging.WicIds.ImagingFactory);
        var info = factory.CreateComponentInfo(id);
        Assert.Equal(Imaging.WicIds.VendorMicrosoft, info.GetVendorGUID());
        Assert.Equal(id, info.GetCLSID());
        Assert.Equal(decoder ? 1u : 2u, info.GetComponentType()); // WICDecoder = 1, WICEncoder = 2
    }
}
