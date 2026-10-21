using System.Text.Json;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Windows;

namespace WinTools.Worker.Tests;

/// <summary>The pixel limit, the per-worker memory cap and the memory-bounded pool (ADR-0015).</summary>
public sealed class ResourceLimitTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private static readonly string WorkerExe = Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.exe");
    public void Dispose() => _dir.Dispose();

    private static Task<IReadOnlyList<ConversionResult>> RunAsync(string op, string source, IReadOnlyDictionary<string, string>? env = null) =>
        new WorkerClient(WorkerExe, TimeSpan.FromSeconds(120), env, parallelism: 1).RunAsync([new ConversionRequest("r1", op, source)], CancellationToken.None);

    [Fact]
    public void The_limit_is_exactly_the_pixel_count_not_one_more()
    {
        // Header-only files: the limit is decided before any pixel is allocated.
        var at = _dir.Write("at.png", PngWriter.HeaderOnly(10_000, 5_000));
        var over = _dir.Write("over.png", PngWriter.HeaderOnly(10_000, 5_001));
        Assert.Equal(ImageLimits.MaxPixels, 10_000L * 5_000);
        Assert.NotEqual(ErrorCode.ImageTooLarge, ConversionJob.Run(new ConversionRequest("a1", "convert.to-jpeg", at)).Error);
        Assert.Equal(ErrorCode.ImageTooLarge, ConversionJob.Run(new ConversionRequest("o1", "convert.to-jpeg", over)).Error);
    }

    /// <summary>The costliest operation at the largest allowed size fits under the cap (measured peak 1.06 GB of 1.5).</summary>
    [Fact]
    public async Task An_image_at_the_pixel_limit_is_processed_under_the_worker_memory_cap()
    {
        var source = _dir.Write("big.bmp", Bmp24(10_000, 5_000));
        var r = Assert.Single(await RunAsync("rotate.right", source));
        Assert.True(r.Success, $"{r.Error}: {r.Message}");
        Assert.Equal(ImageFormat.Bmp, FileSignature.Detect(File.ReadAllBytes(r.OutputPath!)));
    }

#if DEBUG
    /// <summary>Native memory is outside the GC heap limit: only the job object can stop this worker.</summary>
    [Fact]
    public async Task A_worker_cannot_commit_past_the_memory_cap()
    {
        var source = _dir.CopySample("quadrants-64x48.png", "p.png");
        var client = new WorkerClient(WorkerExe, TimeSpan.FromSeconds(60), new Dictionary<string, string> { ["WINTOOLS_WORKER_FAULT"] = "alloc-after-read" }, parallelism: 1);
        var r = Assert.Single(await client.RunAsync([new ConversionRequest("m1", "convert.to-jpeg", source)], CancellationToken.None));
        Assert.True(client.JobObjectAssigned);
        Assert.Equal(ErrorCode.ImageTooLarge, r.Error);
        Assert.Equal(["p.png"], _dir.Names());
    }
#endif

    [Theory]
    [InlineData(16, 8L << 30, 4)]        // plenty of memory: half the processors, at most 4
    [InlineData(16, 4_400_000_000L, 2)]  // a busy 16 GB machine: two workers can each reach the cap
    [InlineData(16, 1L << 30, 1)]        // less than one cap: still one worker
    [InlineData(2, 64L << 30, 1)]        // few processors
    [InlineData(16, -1L, 4)]             // memory unknown
    public void Workers_are_bounded_by_processors_and_available_memory(int processors, long available, int expected) =>
        Assert.Equal(expected, WorkerClient.Lanes(processors, available < 0 ? null : (ulong)available));

    [Fact]
    public void The_worker_gc_heap_limit_equals_the_job_memory_cap()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.runtimeconfig.json")));
        var limit = config.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties").GetProperty("System.GC.HeapHardLimit").GetInt64();
        Assert.Equal(WorkerJobObject.WorkerMemoryLimitBytes, limit);
    }

    /// <summary>A 24-bit bottom-up BMP with a smooth pattern (width × 3 must be a multiple of 4: no row padding).</summary>
    private static byte[] Bmp24(int width, int height)
    {
        var row = width * 3;
        var size = 54 + (long)row * height;
        var bmp = new byte[size];
        "BM"u8.CopyTo(bmp);
        BitConverter.TryWriteBytes(bmp.AsSpan(2), (uint)size);
        BitConverter.TryWriteBytes(bmp.AsSpan(10), 54u);
        BitConverter.TryWriteBytes(bmp.AsSpan(14), 40u);
        BitConverter.TryWriteBytes(bmp.AsSpan(18), width);
        BitConverter.TryWriteBytes(bmp.AsSpan(22), height);
        BitConverter.TryWriteBytes(bmp.AsSpan(26), (ushort)1);
        BitConverter.TryWriteBytes(bmp.AsSpan(28), (ushort)24);
        for (var y = 0; y < height; y++)
        {
            var line = bmp.AsSpan(54 + y * row, row);
            for (var x = 0; x < width; x++) { line[x * 3] = (byte)x; line[x * 3 + 1] = (byte)y; line[x * 3 + 2] = (byte)(x + y); }
        }
        return bmp;
    }
}
