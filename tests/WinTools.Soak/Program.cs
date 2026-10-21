using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinTools.Core.Interaction;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Windows;
using WinTools.Windows.Overlay;

// Local soak runs (TASK 5): resource leaks of the overlay and of the job pipeline, and overlay latency while a batch runs.
// Usage: WinTools.Soak <overlay|jobs|stall> [--cycles N] [--results file.jsonl] [--inject-leak]
// --inject-leak (overlay only) leaks one kernel handle and 4 KB per cycle, to show that the verdict catches a real leak.
// After warm-up and after the run the process is sampled with a forced GC: private bytes, kernel handles, GDI and USER
// objects. No input is injected; the overlay is a non-activating window near the bottom-right corner of the screen.

var mode = args.Length > 0 ? args[0] : "overlay";
var cycles = 1000; string? resultsPath = null; var injectLeak = false;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--cycles": cycles = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--results": resultsPath = Path.GetFullPath(args[++i]); break;
        case "--inject-leak": injectLeak = true; break;
        default: Console.Error.WriteLine($"Unknown option {args[i]}"); return 64;
    }
}
using var results = resultsPath is null ? null : new StreamWriter(resultsPath, append: false) { AutoFlush = true };
void Record(object o) { var line = JsonSerializer.Serialize(o); results?.WriteLine(line); Console.WriteLine(line); }

var workerExe = Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.exe");
var folder = Path.Combine(Path.GetTempPath(), "wintools-soak", $"{mode}-{DateTime.Now:HHmmss}");
Directory.CreateDirectory(folder);
var exit = 0;
try
{
    exit = mode switch
    {
        "overlay" => OnSta(() => Overlay(cycles, injectLeak, Record)),
        "jobs" => Jobs(cycles, folder, workerExe, Record).GetAwaiter().GetResult(),
        "stall" => OnSta(() => Stall(cycles, folder, workerExe, Record)),
        _ => 64,
    };
}
finally
{
    try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}
return exit;

static int OnSta(Func<int> body)
{
    var code = 0;
    var t = new Thread(() => { OleApartment.Initialize(); try { code = body(); } finally { OleApartment.Uninitialize(); } });
    t.SetApartmentState(ApartmentState.STA);
    t.Start();
    t.Join();
    return code;
}

// 1000 show → paint → move → paint → hide cycles; the overlay must give back everything it takes.
static int Overlay(int cycles, bool injectLeak, Action<object> record)
{
    var leaked = new List<object>();
    using var overlay = new RadialOverlay(new NoEvents());
    var (x, y) = Native.Corner();
    var times = new List<double>(cycles);
    var samples = new List<Native.Snapshot>();
    for (var i = -250; i < cycles; i++) // 250 warm-up cycles: OLE and the runtime allocate once on first use
    {
        if (i >= 0 && i % Math.Max(1, cycles / 40) == 0) samples.Add(Native.Sample());
        var t = Stopwatch.GetTimestamp();
        overlay.ShowAt(x, y);
        overlay.SetView(Views.Top(i % 5));
        Native.Pump();
        overlay.MoveTo(x - 10, y - 10);
        overlay.SetView(Views.Level(i % 4 + 1, armed: i % 2 == 0));
        Native.Pump();
        overlay.Hide();
        Native.Pump();
        if (injectLeak && i >= 0) leaked.Add((new EventWaitHandle(false, EventResetMode.ManualReset), new byte[4096]));
        if (i >= 0) times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
    }
    samples.Add(Native.Sample());
    return Verdict("overlay", cycles, samples, times, record);
}

// Batches of 50 small conversions through the real worker pool; the host side must not grow, and no worker may remain.
static async Task<int> Jobs(int files, string folder, string workerExe, Action<object> record)
{
    var sources = Enumerable.Range(0, 50).Select(i =>
    {
        var p = Path.Combine(folder, $"photo {i:00}.png");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "samples", "quadrants-64x48.png"), p);
        return p;
    }).ToArray();
    var samples = new List<Native.Snapshot>();
    var times = new List<double>();
    var failed = 0;
    for (var round = -1; round < files / sources.Length; round++)
    {
        if (round >= 0) samples.Add(Native.Sample());
        var client = new WorkerClient(workerExe, TimeSpan.FromSeconds(60));
        var t = Stopwatch.GetTimestamp();
        var batch = await client.RunAsync(sources.Select((s, i) => new ConversionRequest($"r{round + 1}n{i}", "convert.to-jpeg", s)).ToArray(), CancellationToken.None);
        if (round >= 0) times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        failed += batch.Count(r => !r.Success);
        foreach (var r in batch) if (r.OutputPath is not null) File.Delete(r.OutputPath);
        if (round >= 0 && round % 10 == 9) record(new { mode = "jobs", files = (round + 1) * sources.Length, workers = client.LastLaneCount });
    }
    samples.Add(Native.Sample());
    await Task.Delay(500);
    var leftover = Process.GetProcessesByName("WinTools.Worker").Length;
    record(new { mode = "jobs", failed, workersLeft = leftover, msPerBatchOf50 = Stats(times) });
    return Verdict("jobs", files, samples, times, record) | (failed > 0 || leftover > 0 ? 1 : 0);
}

// Overlay cycles idle, then while a batch keeps every worker busy: the overlay thread never waits for jobs.
static int Stall(int cycles, string folder, string workerExe, Action<object> record)
{
    using var overlay = new RadialOverlay(new NoEvents());
    var (x, y) = Native.Corner();
    double Cycle(int i)
    {
        var t = Stopwatch.GetTimestamp();
        overlay.ShowAt(x, y);
        overlay.SetView(Views.Top(i % 5));
        Native.Pump();
        var ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        overlay.Hide();
        Native.Pump();
        Thread.Sleep(10);
        return ms;
    }
    for (var i = 0; i < 30; i++) Cycle(i);
    var idle = Enumerable.Range(0, cycles).Select(Cycle).ToList();

    // Large enough that the batch outlasts the measurement: 4000×3000 BMPs converted to WebP.
    var bmp = Native.Bmp24(4000, 3000);
    var sources = Enumerable.Range(0, 40).Select(i => { var p = Path.Combine(folder, $"big {i:00}.bmp"); File.WriteAllBytes(p, bmp); return p; }).ToArray();
    var client = new WorkerClient(workerExe, TimeSpan.FromSeconds(120));
    var batch = Task.Run(() => client.RunAsync(sources.Select((s, i) => new ConversionRequest($"b{i}", "convert.to-webp", s)).ToArray(), CancellationToken.None));
    Thread.Sleep(1500); // workers started and decoding
    var busy = new List<double>();
    for (var i = 0; i < cycles && !batch.IsCompleted; i++) busy.Add(Cycle(i));
    var finished = batch.IsCompleted;
    var done = batch.GetAwaiter().GetResult();
    record(new { mode = "stall", idle = Stats(idle), duringBatch = Stats(busy), measuredWhileBusy = busy.Count, batchStillRunningAtEnd = !finished,
        workers = client.LastLaneCount, converted = done.Count(r => r.Success), of = done.Count });
    return busy.Count >= cycles / 2 && done.All(r => r.Success) ? 0 : 1;
}

static object Stats(IReadOnlyList<double> ms)
{
    if (ms.Count == 0) return new { n = 0 };
    var s = ms.Order().ToArray();
    double Q(double p) => Math.Round(s[Math.Min(s.Length - 1, (int)Math.Round(p * (s.Length - 1)))], 2);
    return new { n = s.Length, p50 = Q(0.5), p95 = Q(0.95), p99 = Q(0.99), max = Math.Round(s[^1], 2) };
}

/// <summary>
/// A leak grows with the cycles, so the second and the last quarter of the samples are compared by their medians: a
/// one-off step (the runtime opening something on first use) or a single spike (the GC keeping memory it may reuse)
/// moves neither median, a slope moves the last one. A thread the runtime adds brings its own handles, so up to 8 per
/// new thread are not counted against the product. Allowances: GDI and USER objects 0, other handles 2, GC heap 0.5 MB,
/// private bytes 2 MB.
/// </summary>
static int Verdict(string mode, int cycles, IReadOnlyList<Native.Snapshot> samples, IReadOnlyList<double> ms, Action<object> record)
{
    const int handlesPerThread = 8;
    var q = samples.Count / 4;
    var early = samples.Skip(q).Take(q).ToArray();
    var late = samples.Skip(samples.Count - q).ToArray();
    static double Median(IEnumerable<double> v) { var s = v.Order().ToArray(); return s.Length == 0 ? 0 : s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2; }
    double Growth(Func<Native.Snapshot, double> f) => Math.Round(Median(late.Select(f)) - Median(early.Select(f)), 2);
    var growth = new
    {
        gdi = Growth(x => x.Gdi), user = Growth(x => x.User), handles = Growth(x => x.Handles), threads = Growth(x => x.Threads),
        heapMb = Growth(x => x.HeapMb), privateMb = Growth(x => x.PrivateMb),
    };
    var unexplainedHandles = growth.handles - handlesPerThread * Math.Max(0, growth.threads);
    var leak = growth.gdi > 0 || growth.user > 0 || unexplainedHandles > 2 || growth.heapMb > 0.5 || growth.privateMb > 2;
    record(new { mode, cycles, samples = samples.Count, first = samples[0], last = samples[^1], medianGrowthLastVsSecondQuarter = growth, unexplainedHandles,
        series = new { handles = samples.Select(x => x.Handles), user = samples.Select(x => x.User), heapMb = samples.Select(x => x.HeapMb), privateMb = samples.Select(x => x.PrivateMb) },
        cycleMs = Stats(ms), leak });
    return leak ? 1 : 0;
}

internal sealed class NoEvents : IOverlayEvents
{
    public bool DragEntered(IReadOnlyList<string> paths, int dx, int dy) => false;
    public bool DragMoved(int dx, int dy) => false;
    public void DragLeft() { }
    public bool Dropped(IReadOnlyList<string> paths, int dx, int dy, bool copyAllowed) => false;
}

internal static class Views
{
    private static readonly string[] Categories = ["Convert", "Compress", "Resize", "Rotate", "Tools"];
    private static readonly string[] Commands = ["Back", "To JPEG", "To WebP", "To BMP", "To TIFF"];
    public static MenuView Top(int hot) => new(Categories, 0, hot, false, "");
    public static MenuView Level(int hot, bool armed) => new(Commands, MenuNavigator.LevelStart(0, 5, 4), hot, armed, "Convert", MenuNavigator.BackSweep(5, 4));
}

internal static partial class Native
{
    public readonly record struct Snapshot(double PrivateMb, double HeapMb, int Handles, uint Gdi, uint User, int Threads);

    // Every sample is taken on this one thread. A thread's first user32 call (GetGuiResources itself) makes it a GUI thread,
    // which costs the process a USER object; sampling from whichever pool thread an await resumed on would count those.
    private static readonly System.Collections.Concurrent.BlockingCollection<TaskCompletionSource<Snapshot>> Requests = StartSampler();

    private static System.Collections.Concurrent.BlockingCollection<TaskCompletionSource<Snapshot>> StartSampler()
    {
        var requests = new System.Collections.Concurrent.BlockingCollection<TaskCompletionSource<Snapshot>>();
        new Thread(() => { foreach (var r in requests.GetConsumingEnumerable()) r.SetResult(Measure()); }) { IsBackground = true, Name = "sampler" }.Start();
        return requests;
    }

    public static Snapshot Sample()
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return Measure(); // the overlay thread: always the same one
        var request = new TaskCompletionSource<Snapshot>();
        Requests.Add(request);
        return request.Task.GetAwaiter().GetResult();
    }

    private static Snapshot Measure()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var p = Process.GetCurrentProcess();
        return new Snapshot(Math.Round(p.PrivateMemorySize64 / 1048576.0, 2), Math.Round(GC.GetTotalMemory(forceFullCollection: false) / 1048576.0, 2), p.HandleCount, GetGuiResources(p.Handle, 0), GetGuiResources(p.Handle, 1), p.Threads.Count);
    }

    /// <summary>Dispatches what is queued (paints, timers) without waiting.</summary>
    public static void Pump()
    {
        while (PeekMessageW(out var msg, 0, 0, 0, 1 /* PM_REMOVE */))
        {
            TranslateMessage(msg);
            DispatchMessageW(msg);
        }
    }

    public static (int X, int Y) Corner() => (GetSystemMetrics(0) - 200, GetSystemMetrics(1) - 260);

    public static byte[] Bmp24(int width, int height)
    {
        var row = width * 3;
        var bmp = new byte[54 + (long)row * height];
        "BM"u8.CopyTo(bmp);
        BitConverter.TryWriteBytes(bmp.AsSpan(2), (uint)bmp.Length);
        BitConverter.TryWriteBytes(bmp.AsSpan(10), 54u);
        BitConverter.TryWriteBytes(bmp.AsSpan(14), 40u);
        BitConverter.TryWriteBytes(bmp.AsSpan(18), width);
        BitConverter.TryWriteBytes(bmp.AsSpan(22), height);
        BitConverter.TryWriteBytes(bmp.AsSpan(26), (ushort)1);
        BitConverter.TryWriteBytes(bmp.AsSpan(28), (ushort)24);
        var rng = new Random(7);
        rng.NextBytes(bmp.AsSpan(54)); // noise: real encoding work
        return bmp;
    }

    [StructLayout(LayoutKind.Sequential)] public struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int x, y; }
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool TranslateMessage(in MSG msg);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(in MSG msg);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] private static partial uint GetGuiResources(nint process, uint flags);
}
