using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Jobs;
using WinTools.Core.Settings;
using WinTools.Host;
using WinTools.Host.Jobs;

namespace WinTools.Worker.Tests;

/// <summary>What the job pipeline tells the feedback layer: progress counts, file names and sizes, through the real worker.</summary>
public sealed class JobFeedbackTests
{
    private static readonly string WorkerExe = Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.exe");

    private sealed class Recorder : IJobFeedback
    {
        public readonly List<int> Progress = [];
        public IReadOnlyList<JobItem>? Items;
        public int Starts, Crashes;
        public void Started(CapabilityDescriptor capability, IReadOnlyList<string> paths) => Interlocked.Increment(ref Starts);
        void IJobFeedback.Progress(CapabilityDescriptor capability, int done, int total) { lock (Progress) Progress.Add(done); }
        public void Finished(CapabilityDescriptor capability, IReadOnlyList<JobItem> items) => Items = items;
        public void Crashed(CapabilityDescriptor capability, int files) => Interlocked.Increment(ref Crashes);
        public void SecretNeeded(CapabilityDescriptor capability) { }
    }

    [Fact]
    public async Task Progress_counts_every_file_once_and_the_summary_carries_names_and_sizes()
    {
        using var folder = new TempFolder();
        var good = Enumerable.Range(1, 5).Select(i => folder.CopySample("quadrants-64x48.png", $"p{i}.png")).ToArray();
        var bad = folder.Write("broken.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);
        var recorder = new Recorder();
        using var log = HostLog.Open(Path.Combine(folder.Path, "host.jsonl"));
        var scheduler = Scheduler(log, recorder);
        await scheduler.Enqueue(ImageCapabilities.Describe(OperationCatalog.Get("convert.to-jpeg")), [.. good, bad])!.Completion;

        Assert.Equal(1, recorder.Starts);
        Assert.Equal(0, recorder.Crashes);
        Assert.Equal([1, 2, 3, 4, 5, 6], recorder.Progress.Order());
        var items = recorder.Items!;
        Assert.Equal(["p1.png", "p2.png", "p3.png", "p4.png", "p5.png", "broken.png"], items.Select(i => i.SourceName));
        Assert.All(items.Take(5), i => Assert.True(i.Result.Success && i.SourceBytes > 0 && i.OutputBytes > 0));
        Assert.False(items[5].Result.Success);
        Assert.Null(items[5].OutputBytes);
        var message = FeedbackText.For(OperationCatalog.Get("convert.to-jpeg"), items);
        Assert.Equal((FeedbackKind.Partial, "5 images of 6 completed"), (message.Kind, message.Title));
        Assert.DoesNotContain(folder.Path, message.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static JobScheduler Scheduler(HostLog log, IJobFeedback feedback) =>
        new([new JobModuleRunner(ImageJobModule.Instance, ImageJobsCompat.Images(WorkerExe, TimeSpan.FromSeconds(30), parallelism: 2), ImageJobModule.Parameters)],
            log, () => new AppSettings(), feedback);
}
