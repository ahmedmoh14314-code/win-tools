using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using WinTools.Core.Files;
using WinTools.EndToEnd;

// Explorer end-to-end harness for the product host (local and interactive only; it drives the real mouse and keyboard).
// Usage: WinTools.EndToEnd <png|jpg|bmp|tif|escape|outside|unsupported|fast|worker-crash> [--target jpeg|png|webp|bmp|tiff] [--cycles N] [--fast-ms N] [--results file.jsonl]
// Each scenario: fresh folder with copies of the committed samples, fresh host with its own log, one Explorer window on
// that folder. Before every injection the window is re-verified; a release happens only over our overlay or over the
// folder's own background, otherwise the drag is cancelled with Escape. After every cycle the folder must contain
// exactly the expected files, the source must be byte-identical, and no temporary file may remain.

var scenario = args.Length > 0 ? args[0] : "png";
var cycles = 10; var fastMs = 25; string? resultsPath = null; string? targetArg = null;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--cycles": cycles = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--fast-ms": fastMs = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--target": targetArg = args[++i]; break;   // jpeg|png|webp|bmp|tiff: which command to drop on
        case "--results": resultsPath = Path.GetFullPath(args[++i]); break;
        default: Console.Error.WriteLine($"Unknown option {args[i]}"); return 64;
    }
}

var (sample, sourceName, defaultTarget) = scenario switch
{
    "jpg" => ("quadrants-64x48.jpg", "photo.jpg", ImageFormat.Png),
    "bmp" => ("quadrants-64x48.bmp", "photo.bmp", ImageFormat.WebP),
    "tif" => ("quadrants-64x48.tif", "photo.tif", ImageFormat.Jpeg),
    "unsupported" => ("", "notes.txt", ImageFormat.Unknown),
    _ => ("quadrants-64x48.png", "photo.png", ImageFormat.Jpeg),
};
var outputFormat = targetArg is null ? defaultTarget : Enum.Parse<ImageFormat>(targetArg, ignoreCase: true);
var outputName = outputFormat == ImageFormat.Unknown ? null : Path.GetFileNameWithoutExtension(sourceName) + ImageFormats.Extension(outputFormat);
var expectOutput = scenario is "png" or "jpg" or "bmp" or "tif";
var folder = Path.Combine(Path.GetTempPath(), "wintools-e2e", $"{scenario}-{DateTime.Now:HHmmss}");
Directory.CreateDirectory(folder);
var source = Path.Combine(folder, sourceName);
if (sample.Length > 0) File.Copy(Path.Combine(AppContext.BaseDirectory, "samples", sample), source);
else File.WriteAllText(source, "plain text, not an image");
var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
var commands = WinTools.Core.Capabilities.CapabilityResolver.Default.Resolve([source]);
var hostLog = Path.Combine(folder, "..", $"host-{scenario}-{DateTime.Now:HHmmss}.jsonl");
resultsPath ??= Path.Combine(folder, "..", $"e2e-{scenario}-{DateTime.Now:HHmmss}.jsonl");
using var results = new StreamWriter(resultsPath, append: false) { AutoFlush = true };
void Record(object o) { var line = JsonSerializer.Serialize(o); results.WriteLine(line); Console.WriteLine(line); }

var hostInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "WinTools.Host.exe")) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--log", Path.GetFullPath(hostLog) } };
if (scenario == "worker-crash") hostInfo.Environment["WINTOOLS_WORKER_FAULT"] = "crash-after-temp";
using var host = Process.Start(hostInfo)!;
Thread.Sleep(1500);
if (host.HasExited) { Console.Error.WriteLine($"Host exited early ({host.ExitCode})."); return 3; }

var exitCode = 0;
var passed = 0;
var hostLogLines = 0;
// Host events since the previous cycle: how many DragEnters reached the overlay and how many drops it accepted.
(int Enters, int Accepted) HostEventsSinceLastCycle()
{
    var lines = File.Exists(hostLog) ? ReadShared(hostLog) : [];
    var fresh = lines.Skip(hostLogLines).ToArray();
    hostLogLines = lines.Length;
    return (fresh.Count(l => l.Contains("\"ev\":\"drag.enter\"", StringComparison.Ordinal)),
            fresh.Count(l => l.Contains("\"ev\":\"drop\"", StringComparison.Ordinal) && l.Contains("\"accepted\":true", StringComparison.Ordinal)));
}
static string[] ReadShared(string path)
{
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(fs);
    return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
using (var explorer = ExplorerWindow.Open(folder))
{
    var scale = Math.Max(Desktop.GetDpiForWindow(explorer.Hwnd), 96u) / 96.0;
    int Px(int v) => (int)Math.Round(v * scale);
    for (var cycle = 1; cycle <= cycles; cycle++)
    {
        if (explorer.ItemCentre(sourceName) is not { } item) { Record(new { cycle, abort = "item not found" }); exitCode = 4; break; }
        var (cx, cy) = item;
        if (!explorer.EnsureForeground(cx, cy)) { Record(new { cycle, abort = "Explorer window not foreground or covered; refusing to inject" }); exitCode = 4; break; }

        Desktop.MoveTo(cx, cy); Thread.Sleep(60);
        Desktop.LeftDown(); Thread.Sleep(80);
        Desktop.GlideTo(cx + Px(40), cy + Px(30));
        Thread.Sleep(250);
        Desktop.GetCursorPos(out var at);
        // Aim at the middle of the sector of the wanted command, as the product lays them out (clockwise from 12).
        var index = commands.ToList().FindIndex(o => o.Target == outputFormat);
        if (index < 0 && expectOutput) { Record(new { cycle, abort = $"{outputFormat} is not offered for {sourceName}" }); exitCode = 4; break; }
        index = Math.Max(0, index);
        var angle = (index + 0.5) * 2 * Math.PI / Math.Max(1, commands.Count);
        var drop = (X: at.X + (int)Math.Round(Px(70) * Math.Sin(angle)), Y: at.Y - (int)Math.Round(Px(70) * Math.Cos(angle)));
        var outside = (X: at.X, Y: at.Y + Px(200));           // beyond the ring, over the folder background
        string? releasedOver;
        double? releaseAfterMs = null;

        var tKey = Stopwatch.GetTimestamp();
        Desktop.KeyDown(Desktop.VkSpace);
        if (scenario == "fast")
        {
            Desktop.MoveTo(drop.X, drop.Y);
            while (Stopwatch.GetElapsedTime(tKey).TotalMilliseconds < fastMs) Thread.SpinWait(50);
            releasedOver = explorer.ReleaseTarget(drop.X, drop.Y); // what was under the pointer just before the release
            releaseAfterMs = Math.Round(Stopwatch.GetElapsedTime(tKey).TotalMilliseconds, 2);
            if (releasedOver is null) { Desktop.Tap(Desktop.VkEscape); Thread.Sleep(150); }
            Desktop.LeftUp();
            Thread.Sleep(40);
            Desktop.KeyUp(Desktop.VkSpace);
        }
        else
        {
            Thread.Sleep(40);
            Desktop.KeyUp(Desktop.VkSpace);
            Thread.Sleep(250);
            if (scenario == "escape")
            {
                releasedOver = "escape";
                Desktop.Tap(Desktop.VkEscape); Thread.Sleep(150);
            }
            else
            {
                var target = scenario == "outside" ? outside : drop;
                Desktop.GlideTo(target.X, target.Y);
                Thread.Sleep(120);
                releasedOver = explorer.ReleaseTarget(target.X, target.Y);
                var wanted = scenario == "outside" ? "explorer" : "overlay";
                if (releasedOver != wanted) { Desktop.Tap(Desktop.VkEscape); Thread.Sleep(150); releasedOver = $"refused({releasedOver ?? "unsafe"})"; }
            }
            Desktop.LeftUp();
        }

        // Outcome: wait for the output where one is expected; otherwise give a wrong job time to show up.
        string? output = null;
        var deadline = Stopwatch.GetTimestamp();
        var waitFor = expectOutput || scenario == "fast" ? TimeSpan.FromSeconds(scenario == "fast" ? 4 : 15) : TimeSpan.FromSeconds(3);
        while (Stopwatch.GetElapsedTime(deadline) < waitFor)
        {
            var produced = Directory.GetFiles(folder).Where(f => !string.Equals(f, source, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (produced.Length > 0 && !produced.Any(f => Path.GetFileName(f).StartsWith("~wintools-", StringComparison.Ordinal))) { output = produced.Single(); break; }
            Thread.Sleep(100);
        }
        Thread.Sleep(300);
        var files = Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        var sourceIntact = File.Exists(source) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))) == sourceHash;
        var outputValid = output is not null && File.ReadAllBytes(output) is var bytes && FileSignature.Detect(bytes) == outputFormat && ContainerCheck.IsComplete(bytes, outputFormat, out _);
        var (enters, accepted) = HostEventsSinceLastCycle();
        var expectedFiles = output is null ? [sourceName] : new[] { sourceName, Path.GetFileName(output) }.Order(StringComparer.Ordinal).ToArray();
        var cleanFolder = files.SequenceEqual(expectedFiles);
        var ok = sourceIntact && cleanFolder && scenario switch
        {
            "png" or "jpg" or "bmp" or "tif" => output is not null && Path.GetFileName(output) == outputName && outputValid && accepted == 1,
            // Fast release, judged by the host: a drag that entered the overlay must end in an accepted drop (no lost
            // drop), an accepted drop must produce a valid output, and no drag that missed the overlay may produce one.
            "fast" => (enters == 0 || accepted == 1) && (accepted == 0) == (output is null) && (output is null || outputValid),
            "worker-crash" => output is null && accepted == 1, // the drop ran; the crashed job left nothing behind
            _ => output is null && accepted == 0,
        };
        if (ok) passed++; else exitCode = 1;
        Record(new { scenario, cycle, releasedOver, releaseAfterMs, hostEnters = enters, hostAccepted = accepted, triggerAt = $"{at.X},{at.Y}", releaseAt = scenario == "outside" ? $"{outside.X},{outside.Y}" : $"{drop.X},{drop.Y}", output = output is null ? null : Path.GetFileName(output), outputValid, sourceIntact, cleanFolder, files, ok });
        if (output is not null) File.Delete(output); // the harness's own result file, so the next cycle starts clean
        Thread.Sleep((int)Desktop.GetDoubleClickTime() + 300);
    }
}

var hostAlive = !host.HasExited;
double? hostPrivateMb = null, hostWorkingSetMb = null;
if (hostAlive) { host.Refresh(); hostPrivateMb = Math.Round(host.PrivateMemorySize64 / 1048576.0, 1); hostWorkingSetMb = Math.Round(host.WorkingSet64 / 1048576.0, 1); }
if (hostAlive) { host.Kill(); host.WaitForExit(5000); }
Record(new { scenario, summary = true, cycles, passed, hostAliveAtEnd = hostAlive, hostPrivateMb, hostWorkingSetMb, hostLog = Path.GetFullPath(hostLog), folder });
if (!hostAlive) exitCode = 2;
try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
return exitCode;
