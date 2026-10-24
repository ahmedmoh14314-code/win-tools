using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Interaction;
using WinTools.Core.Output;
using WinTools.Core.Settings;
using WinTools.EndToEnd;

// Explorer end-to-end harness for the product host (local and interactive only; it drives the real mouse and keyboard).
// Usage: WinTools.EndToEnd <png|jpg|bmp|tif|webp|batch|escape|outside|unsupported|category|fast|edge|worker-crash|file|folder>
//          [--op <operation id>] [--target jpeg|png|webp|bmp|tiff] [--batch N] [--cycles N] [--fast-ms N] [--results file.jsonl]
//          [--animations on|off|system]   (default on: the menu's animations run, the heavier path for latency)
//          [--host <WinTools.Host.exe>]   (default: the host next to the harness; e.g. an installed copy)
//          [--select ctrl-a|ctrl-click|shift-range] [--hold none|ctrl|shift] [--settle-ms N]   (batch selection;
//          defaults ctrl-a, none, 250 as before; the release matrix uses 0: drag at once after selecting)
// "alt": a drag with Alt held, then the trigger: nothing may open or run, and the refusal toast must appear.
// "file" and "folder" (V2 PR 1.4, UX-009): a text file and a folder, each with File info by default (--op files.sha256
// for a file): the info panel must appear, nothing may be written. Copy path is not run here: it would overwrite the
// user's clipboard (unit-tested in FilesModuleTests; manual protocol in docs/testing/release-matrix.md).
// Each scenario: fresh folder with copies of the committed samples, fresh host with its own log and default settings,
// one Explorer window on that folder. Before every injection the window is re-verified; a release happens only over our
// overlay or over the folder's own background, otherwise the drag is cancelled with Escape. After every cycle the folder
// must contain exactly the expected files, the source must be byte-identical, and no temporary file may remain.
// Navigation follows the product's two-level menu: point at the category, then move out to the command and wait.
// Start each run from a fresh shell: a second harness started from the same shell has no foreground rights, Windows
// ignores its raise of the Explorer window, and the guard (correctly) refuses to inject.
// "edge": the window sits in the bottom-right corner of its monitor's work area and the trigger comes deep inside a
// sector of the ring that the corner pushed aside; nothing may open until the pointer has been to the ring's centre.
// UI feedback (V1 UI phase): every producing cycle must show the result toast, "Image info" must show the info panel,
// and the Explorer window must still be the foreground window when they appear (no focus stealing). Every navigation
// sound the host logged must follow the rules: one level sound per level change, never more ticks than sector changes.

var scenario = args.Length > 0 ? args[0] : "png";
var cycles = 10; var fastMs = 25; string? resultsPath = null; string? targetArg = null; string? opArg = null; var batchSize = 12; var animations = "On"; string? hostArg = null; var selectMode = "ctrl-a"; var holdKey = "none"; var settleMs = 250;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--cycles": cycles = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--fast-ms": fastMs = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--target": targetArg = args[++i]; break;                // jpeg|png|webp|bmp|tiff: which conversion to drop on
        case "--op": opArg = args[++i]; break;                        // any menu operation id, e.g. resize.percent-50
        case "--batch": batchSize = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break; // files for "batch"
        case "--results": resultsPath = Path.GetFullPath(args[++i]); break;
        case "--host": hostArg = Path.GetFullPath(args[++i]); break;
        case "--select": selectMode = args[++i] is "ctrl-a" or "ctrl-click" or "shift-range" ? args[i] : throw new ArgumentException($"--select {args[i]}"); break;
        case "--hold": holdKey = args[++i] is "none" or "ctrl" or "shift" ? args[i] : throw new ArgumentException($"--hold {args[i]}"); break;
        case "--settle-ms": settleMs = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--animations": animations = args[++i] switch { "on" => "On", "off" => "Off", "system" => "System", var a => throw new ArgumentException($"--animations {a}") }; break;
        default: Console.Error.WriteLine($"Unknown option {args[i]}"); return 64;
    }
}

var (sample, sourceName, defaultTarget) = scenario switch
{
    "jpg" => ("quadrants-64x48.jpg", "photo.jpg", ImageFormat.Png),
    "bmp" => ("quadrants-64x48.bmp", "photo.bmp", ImageFormat.WebP),
    "tif" => ("quadrants-64x48.tif", "photo.tif", ImageFormat.Jpeg),
    "webp" => ("quadrants-64x48.webp", "photo.webp", ImageFormat.Png),
    "unsupported" or "file" => ("", "notes.txt", ImageFormat.Unknown),
    "folder" => ("", "holiday photos", ImageFormat.Unknown),
    _ => ("quadrants-64x48.png", "photo.png", ImageFormat.Jpeg),
};
// The host runs with default settings, so the harness sees the same menu.
var catalog = ImageCapabilities.Operations(new AppSettings().CustomResizeId());
var target = targetArg is null ? defaultTarget : Enum.Parse<ImageFormat>(targetArg, ignoreCase: true);
if (opArg is null && scenario is "file" or "folder") opArg = FilesCapabilities.InfoId;
var op = opArg is not null ? catalog.FirstOrDefault(o => o.Id == opArg) : target == ImageFormat.Unknown ? null : OperationCatalog.Convert(target);
// Every module, as the host resolves them: the ring positions the harness aims at must be the host's.
var resolver = CapabilityResolver.For(new AppSettings().CustomResizeId());
var capability = resolver.Catalog.FirstOrDefault(c => c.Id == (opArg ?? op?.Id));
if (opArg is not null && capability is null) { Console.Error.WriteLine($"Unknown operation {opArg}"); return 64; }
var facts = op is { WritesFile: false } || capability?.Produces == CapabilityOutput.Facts; // the info panel, no file
var sourceFormat = FormatHint.FromPath(sourceName);
var outputFormat = op is null || !op.WritesFile ? ImageFormat.Unknown : op.OutputFormat(sourceFormat);
string? OutputNameFor(string path) => outputFormat == ImageFormat.Unknown ? null : Path.GetFileName(OutputNaming.Candidates(path, outputFormat, op!.NameSuffix).First());
var producing = scenario is "png" or "jpg" or "bmp" or "tif" or "webp" or "batch" or "edge";
var expectOutput = producing && outputFormat != ImageFormat.Unknown;
var stamp = DateTime.Now.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture);
var folder = Path.Combine(Path.GetTempPath(), "wintools-e2e", $"{scenario}-{stamp}");
Directory.CreateDirectory(folder);
var source = Path.Combine(folder, sourceName);
var isFolder = scenario == "folder";
if (isFolder) { Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "inside.txt"), "a file in the dropped folder"); }
else if (sample.Length > 0) File.Copy(Path.Combine(AppContext.BaseDirectory, "samples", sample), source);
else File.WriteAllText(source, "plain text, not an image");
// Remove metadata on a JPEG: give the copy a comment segment right after SOI, which the allow-list must drop.
var metadataMarker = "wintools-e2e comment"u8.ToArray();
var strip = op is { Kind: OperationKind.StripMetadata } && sourceFormat == ImageFormat.Jpeg;
if (strip)
{
    var original = File.ReadAllBytes(source);
    var len = metadataMarker.Length + 2;
    File.WriteAllBytes(source, [.. original[..2], 0xFF, 0xFE, (byte)(len >> 8), (byte)len, .. metadataMarker, .. original[2..]]);
}
// A folder's "hash" is its listing: a dropped folder must keep its contents.
string HashOf(string path) => isFolder ? string.Join("|", Directory.GetFileSystemEntries(path).Order(StringComparer.Ordinal)) : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
var sourceHash = HashOf(source);
var outputName = OutputNameFor(source);
// Batch: the source plus copies with Unicode names, all selected with Ctrl+A and dragged together.
var batchSources = new List<string> { source };
if (scenario == "batch")
    for (var b = 2; b <= batchSize; b++)
    {
        var copy = Path.Combine(folder, $"photo {b:00} صورة.png");
        File.Copy(source, copy);
        batchSources.Add(copy);
    }
var batchHashes = batchSources.ToDictionary(f => f, HashOf);
// The drop target marks a folder with a trailing separator (ADR-0019); the menu is built from the paths as marked.
var menu = RadialMenu.Build(resolver, [.. batchSources.Select(f => isFolder ? f + Path.DirectorySeparatorChar : f)]);
var groupIndex = capability is null ? -1 : menu.ToList().FindIndex(g => g.Commands.Any(c => c.Id == capability.Id));
var commandIndex = groupIndex < 0 ? -1 : menu[groupIndex].Commands.ToList().FindIndex(c => c.Id == capability!.Id);
if ((producing || facts) && capability is not null && groupIndex < 0) { Console.Error.WriteLine($"{capability.Id} is not offered for {sourceName}"); return 4; }

var hostLog = Path.Combine(folder, "..", $"host-{scenario}-{stamp}.jsonl");
var settingsFile = Path.Combine(folder, "..", $"settings-{scenario}-{stamp}.json");
// Default settings except the animations (on unless asked otherwise), so the menu's commands are the defaults' ones.
File.WriteAllText(settingsFile, new AppSettings { Animations = animations }.ToJson());
resultsPath ??= Path.Combine(folder, "..", $"e2e-{scenario}-{stamp}.jsonl");
using var results = new StreamWriter(resultsPath, append: false) { AutoFlush = true };
void Record(object o) { var line = JsonSerializer.Serialize(o); results.WriteLine(line); Console.WriteLine(line); }

var hostInfo = new ProcessStartInfo(hostArg ?? Path.Combine(AppContext.BaseDirectory, "WinTools.Host.exe"))
{
    UseShellExecute = false, CreateNoWindow = true,
    ArgumentList = { "--log", Path.GetFullPath(hostLog), "--settings", Path.GetFullPath(settingsFile) },
};
if (scenario == "worker-crash") hostInfo.Environment["WINTOOLS_WORKER_FAULT"] = "crash-after-temp";
using var host = Process.Start(hostInfo)!;
Thread.Sleep(1500);
if (host.HasExited) { Console.Error.WriteLine($"Host exited early ({host.ExitCode})."); return 3; }

var exitCode = 0;
var passed = 0;
var hostLogLines = 0;
static string[] ReadShared(string path)
{
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(fs);
    return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
string[] HostLines() => File.Exists(hostLog) ? ReadShared(hostLog) : [];
// Host events since the previous cycle, parsed.
List<JsonElement> HostEventsSinceLastCycle()
{
    var lines = HostLines();
    var fresh = lines.Skip(hostLogLines).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    hostLogLines = lines.Length;
    return fresh;
}
static string Ev(JsonElement e) => e.TryGetProperty("ev", out var v) ? v.GetString() ?? "" : "";
// Host log lines are {"t","wall","ev","data":{...}}: event fields live under "data".
static JsonElement Data(JsonElement e) => e.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
static bool Is(JsonElement e, string name, bool value) => Data(e) is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(name, out var v) && v.ValueKind == (value ? JsonValueKind.True : JsonValueKind.False);
// The host's sound counters on every event that carries them: one level sound per level change, no more ticks than
// sector changes (the sounds are on with default settings).
static bool SoundsHeld(IEnumerable<JsonElement> events) => events.Where(e => Data(e) is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("sounds", out _))
    .All(e => Data(e).GetProperty("sounds") is var s && s.GetProperty("ticks").GetInt32() <= s.GetProperty("sectorChanges").GetInt32()
        && s.GetProperty("levels").GetInt32() == s.GetProperty("levelChanges").GetInt32());
// Waits for a visible window of the host's (the toast, the info panel) and reports whether Explorer still had the foreground.
(bool Shown, bool FocusKept) WaitForSurface(string className, nint explorerHwnd, TimeSpan timeout)
{
    var waited = Stopwatch.GetTimestamp();
    while (Stopwatch.GetElapsedTime(waited) < timeout)
    {
        if (Desktop.VisibleWindowsOf(host.Id, className).Count > 0) return (true, Desktop.GetForegroundWindow() == explorerHwnd);
        Thread.Sleep(50);
    }
    return (false, Desktop.GetForegroundWindow() == explorerHwnd);
}
// The centre and DPI of the ring on screen in this cycle, from the host log: the menu re-centres when it changes level.
(int X, int Y, uint Dpi)? LastOverlay()
{
    foreach (var l in HostLines().Skip(hostLogLines).Reverse())
    {
        if (!l.Contains("\"ev\":\"overlay.show\"", StringComparison.Ordinal) && !l.Contains("\"ev\":\"menu.open\"", StringComparison.Ordinal)) continue;
        var e = Data(JsonDocument.Parse(l).RootElement);
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("centre", out var centre) || !e.TryGetProperty("dpi", out var dpi)) return null;
        var c = centre.GetString()!.Split(',');
        return (int.Parse(c[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(c[1], System.Globalization.CultureInfo.InvariantCulture), dpi.GetUInt32());
    }
    return null;
}

using (var explorer = ExplorerWindow.Open(folder))
{
    var explorerScale = Math.Max(Desktop.GetDpiForWindow(explorer.Hwnd), 96u) / 96.0;
    int Px(int v) => (int)Math.Round(v * explorerScale);
    var work = Desktop.WorkAreaOf(explorer.Hwnd);
    if (scenario == "edge")
    {
        Desktop.SetWindowPos(explorer.Hwnd, 0, work.Right - Px(900), work.Bottom - Px(620), Px(900), Px(620), Desktop.SwpNoZOrder);
        Thread.Sleep(800);
    }
    for (var cycle = 1; cycle <= cycles; cycle++)
    {
        try
        {
            // Batch: grab a copy (the view may sort the plain "photo" out of the realised, visible range).
            var grabName = scenario == "batch" ? Path.GetFileName(batchSources[1]) : sourceName;
            // An item position outside the window is stale (the window or its layout had not settled after opening or
            // moving): read it again, a few times, and record that it happened.
            var item = explorer.ItemCentre(grabName);
            for (var reread = 1; reread <= 10 && item is { } stale && !Desktop.Inside(explorer.Hwnd, stale.X, stale.Y, Px(8)); reread++)
            {
                Record(new { cycle, reread, staleItem = $"{stale.X},{stale.Y}", window = Desktop.StateOf(explorer.Hwnd) });
                Thread.Sleep(300);
                item = explorer.ItemCentre(grabName);
            }
            if (item is not { } found || !Desktop.Inside(explorer.Hwnd, found.X, found.Y, Px(8))) { Record(new { cycle, abort = "item not found inside the window", items = explorer.DescribeItems() }); exitCode = 4; break; }
            var (cx, cy) = found;
            // A window that is in front for a moment gets a few chances to go away; every
            // refusal is recorded, and nothing is injected unless this window is in front and uncovered.
            var foreground = false;
            for (var attempt = 1; attempt <= 5 && !(foreground = explorer.EnsureForeground(cx, cy)); attempt++)
            {
                Record(new { cycle, attempt, refused = "covered", underPoint = Desktop.ClassOf(Desktop.RootAt(cx, cy)), foreground = Desktop.ClassOf(Desktop.GetForegroundWindow()),
                    explorer = Desktop.StateOf(explorer.Hwnd), raise = explorer.LastRaise, stillShowsFolder = explorer.StillShowsFolder(), item = $"{cx},{cy}" });
                Thread.Sleep(1000);
            }
            if (cycle == 1) Record(new { cycle, foregroundOk = foreground, raise = explorer.LastRaise });
            if (!foreground)
            {
                Record(new { cycle, abort = "Explorer window not foreground or covered; refusing to inject",
                    underPoint = Desktop.ClassOf(Desktop.RootAt(cx, cy)), foreground = Desktop.ClassOf(Desktop.GetForegroundWindow()), at = $"{cx},{cy}" });
                exitCode = 4; break;
            }

            var heldVk = holdKey switch { "ctrl" => Desktop.VkControl, "shift" => Desktop.VkShift, _ => 0 };
            (int X, int Y)? lastClick = null; var lastClickAt = 0L;
            if (scenario == "batch")
            {
                // Select the harness folder's files (guarded), then drag one of the selected items. With --hold the modifier
                // used for the selection stays down through the drag and the drop, as a user selecting with Ctrl-click does.
                Desktop.MoveTo(cx, cy); Thread.Sleep(40);
                Desktop.LeftDown(); Thread.Sleep(30); Desktop.LeftUp();
                Thread.Sleep((int)Desktop.GetDoubleClickTime() + 200);
                if (!explorer.EnsureForeground(cx, cy)) { Record(new { cycle, abort = "foreground lost before selecting" }); exitCode = 4; break; }
                switch (selectMode)
                {
                    case "ctrl-a":
                        Desktop.KeyDown(Desktop.VkControl); Desktop.Tap('A');
                        if (holdKey != "ctrl") Desktop.KeyUp(Desktop.VkControl);
                        break;
                    case "ctrl-click":
                        // The first item is already selected by the plain click; add the others one by one with Ctrl held.
                        Desktop.KeyDown(Desktop.VkControl);
                        foreach (var other in batchSources.Where(f => Path.GetFileName(f) != grabName))
                        {
                            if (explorer.ItemCentre(Path.GetFileName(other)) is not { } oc || !Desktop.Inside(explorer.Hwnd, oc.X, oc.Y, Px(8)))
                            {
                                Desktop.KeyUp(Desktop.VkControl);
                                Record(new { cycle, abort = "item to Ctrl-click not visible", item = Path.GetFileName(other) });
                                exitCode = 4; break;
                            }
                            Desktop.MoveTo(oc.X, oc.Y); Thread.Sleep(30);
                            Desktop.LeftDown(); Thread.Sleep(30); Desktop.LeftUp(); Thread.Sleep(60);
                            (lastClick, lastClickAt) = (oc, Stopwatch.GetTimestamp());
                        }
                        if (exitCode == 4) break;
                        if (holdKey != "ctrl") Desktop.KeyUp(Desktop.VkControl);
                        break;
                    case "shift-range":
                        // The whole list as one range, as a user makes it: click the first item, Shift-click the last.
                        if (explorer.EndItemCentre(last: false) is not { } first || !Desktop.Inside(explorer.Hwnd, first.X, first.Y, Px(8))
                            || explorer.EndItemCentre(last: true) is not { } end || !Desktop.Inside(explorer.Hwnd, end.X, end.Y, Px(8)))
                        {
                            Record(new { cycle, abort = "first or last item not visible for the Shift range" }); exitCode = 4; break;
                        }
                        Desktop.MoveTo(first.X, first.Y); Thread.Sleep(30);
                        Desktop.LeftDown(); Thread.Sleep(30); Desktop.LeftUp(); Thread.Sleep(60);
                        Desktop.KeyDown(Desktop.VkShift);
                        Desktop.MoveTo(end.X, end.Y); Thread.Sleep(30);
                        Desktop.LeftDown(); Thread.Sleep(30); Desktop.LeftUp(); Thread.Sleep(60);
                        (lastClick, lastClickAt) = (end, Stopwatch.GetTimestamp());
                        if (holdKey != "shift") Desktop.KeyUp(Desktop.VkShift);
                        break;
                }
                if (exitCode == 4) break;
                if (heldVk != 0 && !Desktop.IsDown(heldVk)) Desktop.KeyDown(heldVk);
                if (settleMs > 0) Thread.Sleep(settleMs);
                // Drag from a selected item that is visible now. With Shift held, Explorer treats the press as a Shift-click and
                // re-ranges the selection from its anchor, so press on the range's end, as a user keeping Shift down would.
                // Never press where the last click landed within the double-click time: Explorer would open the file.
                var visible = batchSources.Select(f => explorer.ItemCentre(Path.GetFileName(f)))
                    .Where(p => p is { } v && Desktop.Inside(explorer.Hwnd, v.X, v.Y, Px(8))).Select(p => p!.Value).ToList();
                if (visible.Count == 0) { if (heldVk != 0) Desktop.KeyUp(heldVk); Record(new { cycle, abort = "no selected item visible to drag" }); exitCode = 4; break; }
                var vis = selectMode == "shift-range" && lastClick is { } rangeEnd ? rangeEnd
                    : visible.FirstOrDefault(v => lastClick is not { } lc || Math.Abs(v.X - lc.X) + Math.Abs(v.Y - lc.Y) > Px(8), visible[0]);
                var sinceClick = Stopwatch.GetElapsedTime(lastClickAt).TotalMilliseconds;
                if (lastClick is { } l && Math.Abs(vis.X - l.X) + Math.Abs(vis.Y - l.Y) <= Px(8) && sinceClick < Desktop.GetDoubleClickTime() + 50)
                    Thread.Sleep((int)(Desktop.GetDoubleClickTime() + 50 - sinceClick));
                (cx, cy) = vis;
            }
            Desktop.MoveTo(cx, cy); Thread.Sleep(60);
            Desktop.LeftDown(); Thread.Sleep(80);
            Desktop.GlideTo(cx + Px(40), cy + Px(30));
            if (scenario == "edge")
            {
                // Into the corner, still over this window (its list or status bar), never released there.
                Desktop.GlideTo(work.Right - Px(30), work.Bottom - Px(45), steps: 12);
                Desktop.GetCursorPos(out var c);
                if (Desktop.RootAt(c.X, c.Y) != explorer.Hwnd)
                {
                    Desktop.Tap(Desktop.VkEscape); Thread.Sleep(150); Desktop.LeftUp();
                    Record(new { cycle, abort = "corner is not over the harness window", underPoint = Desktop.ClassOf(Desktop.RootAt(c.X, c.Y)), at = $"{c.X},{c.Y}" });
                    exitCode = 4; break;
                }
            }
            if (scenario == "alt") { Desktop.KeyDown(Desktop.VkAlt); Thread.Sleep(60); }
            Thread.Sleep(250);
            Desktop.GetCursorPos(out var at);

            // Points of the wanted category and command, as the product lays them out around the centre of the ring on screen.
            (int X, int Y) Around((int X, int Y, uint Dpi) o, double angle, double radius96)
            {
                var (dx, dy) = RingGeometry.PointAt(angle, radius96 * o.Dpi / 96);
                return (o.X + (int)Math.Round(dx), o.Y + (int)Math.Round(dy));
            }
            var categoryAngle = RingGeometry.SectorCentre(Math.Max(0, groupIndex), Math.Max(1, menu.Count));
            // Deep into the category opens it; halfway only points at it (the "category" drop that must run nothing).
            (int X, int Y) CategoryOpenPoint((int X, int Y, uint Dpi) o) => Around(o, categoryAngle, (RingGeometry.OpenRadius96 + RingGeometry.OuterRadius96) / 2.0);
            (int X, int Y) CategoryPoint((int X, int Y, uint Dpi) o) => Around(o, categoryAngle, (RingGeometry.InnerRadius96 + RingGeometry.OpenRadius96) / 2.0);
            // On the open category's ring (re-centred on the pointer): sector 0 is Back, commands follow.
            (int X, int Y) CommandPoint((int X, int Y, uint Dpi) o)
            {
                if (groupIndex < 0) return CategoryPoint(o);
                var count = menu[groupIndex].Commands.Count;
                return Around(o, RingGeometry.SectorCentre(commandIndex + 1, count + 1, MenuNavigator.LevelStart(groupIndex, menu.Count, count), MenuNavigator.BackSweep(menu.Count, count)), 62);
            }
            var assumed = (at.X, at.Y, Desktop.GetDpiForWindow(explorer.Hwnd));
            (int X, int Y) release = (at.X, at.Y);
            string? releasedOver;
            double? releaseAfterMs = null;
            // edge: the trigger's offset from the pushed-aside centre, menu changes before the pointer reached it, and
            // whether the category's ring was pushed aside too.
            double? startOffset = null; int? opensBeforeCentre = null; bool? levelPushed = null;
            bool? categorySeen = null; // fast: the host had opened the category before the move to the command

            // Escape with a modifier held is a different shortcut (Ctrl+Esc opens Start, Alt+Esc switches windows): let go first.
            void CancelDrag()
            {
                if (heldVk != 0) Desktop.KeyUp(heldVk);
                if (scenario == "alt") Desktop.KeyUp(Desktop.VkAlt);
                Thread.Sleep(30);
                Desktop.Tap(Desktop.VkEscape);
            }
            var tKey = Stopwatch.GetTimestamp();
            Desktop.KeyDown(Desktop.VkSpace);
            if (scenario == "fast")
            {
                // As fast as a hand can: category and command right after the overlay appears, release at --fast-ms.
                while (Desktop.ClassOf(Desktop.RootAt(at.X, at.Y)) != "WinTools.RadialOverlay" && Stopwatch.GetElapsedTime(tKey).TotalMilliseconds < fastMs) Thread.SpinWait(50);
                var o = LastOverlay() ?? assumed;
                var cat = CategoryOpenPoint(o);
                Desktop.MoveTo(cat.X, cat.Y);
                // The overlay sees the pointer only from OLE's first DragEnter on (up to ~70 ms after it appears on a cold
                // first drag); a move before that is never seen, so wait (within the same budget) until the host has
                // opened the category before turning to the command.
                Thread.Sleep(8);
                while (Stopwatch.GetElapsedTime(tKey).TotalMilliseconds < fastMs && !HostLines().Skip(hostLogLines).Any(l => l.Contains("\"ev\":\"menu.open\"", StringComparison.Ordinal))) Thread.Sleep(2);
                categorySeen = HostLines().Skip(hostLogLines).Any(l => l.Contains("\"ev\":\"menu.open\"", StringComparison.Ordinal));
                if (categorySeen == true)
                {
                    var level = LastOverlay() is { } lv && (lv.X, lv.Y) != (o.X, o.Y) ? lv : (cat.X, cat.Y, o.Dpi); // re-centred on the pointer
                    release = CommandPoint(level);
                    Desktop.MoveTo(release.X, release.Y);
                }
                else release = cat; // not seen in time: released on the category, where nothing may run
                while (Stopwatch.GetElapsedTime(tKey).TotalMilliseconds < fastMs) Thread.SpinWait(50);
                releasedOver = explorer.ReleaseTarget(release.X, release.Y); // what was under the pointer just before the release
                releaseAfterMs = Math.Round(Stopwatch.GetElapsedTime(tKey).TotalMilliseconds, 2);
                if (releasedOver is null) { CancelDrag(); Thread.Sleep(150); }
                Desktop.LeftUp();
                Thread.Sleep(40);
                Desktop.KeyUp(Desktop.VkSpace);
            }
            else
            {
                Thread.Sleep(40);
                Desktop.KeyUp(Desktop.VkSpace);
                Thread.Sleep(250);
                var o = LastOverlay() ?? assumed;
                if (scenario == "escape")
                {
                    releasedOver = "escape";
                    Desktop.Tap(Desktop.VkEscape); Thread.Sleep(150);
                }
                else if (scenario == "alt")
                {
                    // Nothing may have opened; cancel the drag the way a user would, then let go of Alt.
                    releasedOver = Desktop.ClassOf(Desktop.RootAt(at.X, at.Y)) == "WinTools.RadialOverlay" ? "overlay" : "alt-refused";
                    CancelDrag(); Thread.Sleep(150);
                }
                else
                {
                    if (scenario == "outside")
                    {
                        // Every deep stroke on the categories opens one, so the way out is through the open category's ring:
                        // open it, then leave through a command, beyond the ring, to a point over the folder's empty background
                        // (checked before moving; the nearest to straight down first).
                        var cat = CategoryOpenPoint(o);
                        Desktop.GlideTo(cat.X, cat.Y);
                        Thread.Sleep(120);
                        var lv = LastOverlay() ?? (cat.X, cat.Y, o.Dpi);
                        var count = menu[groupIndex].Commands.Count;
                        var exits = Enumerable.Range(1, count)
                            .Select(i => RingGeometry.SectorCentre(i, count + 1, MenuNavigator.LevelStart(groupIndex, menu.Count, count), MenuNavigator.BackSweep(menu.Count, count)))
                            .OrderBy(a => Math.Abs(a - Math.PI))
                            .Select(a => Around(lv, a, 160)).ToList();
                        release = exits.FirstOrDefault(p => explorer.ReleaseTarget(p.X, p.Y) == "explorer", exits[0]);
                    }
                    else if (scenario is "category" or "unsupported")
                        release = CategoryPoint(o); // pointing at a category (or at "No actions"): a drop there must run nothing
                    else
                    {
                        var dead = RingGeometry.Scale(RingGeometry.InnerRadius96, o.Dpi);
                        if (scenario == "edge")
                        {
                            startOffset = Math.Round(Math.Sqrt(Math.Pow(at.X - o.X, 2) + Math.Pow(at.Y - o.Y, 2)), 1);
                            // Stay deep in the sector under the pointer for a while (DragOver keeps coming), then come to the centre.
                            Desktop.MoveTo(at.X - 2, at.Y - 2); Thread.Sleep(80); Desktop.MoveTo(at.X, at.Y); Thread.Sleep(200);
                            opensBeforeCentre = HostLines().Skip(hostLogLines).Count(l => l.Contains("\"ev\":\"menu.", StringComparison.Ordinal));
                            Desktop.GlideTo(o.X, o.Y, steps: 8);
                            Thread.Sleep(120);
                        }
                        var cat = CategoryOpenPoint(o);
                        Desktop.GlideTo(cat.X, cat.Y);
                        Thread.Sleep(120);
                        var lv = LastOverlay() ?? (cat.X, cat.Y, o.Dpi); // the category's ring, centred where it opened
                        levelPushed = Math.Pow(cat.X - lv.X, 2) + Math.Pow(cat.Y - lv.Y, 2) >= (double)dead * dead;
                        if (scenario == "edge" && levelPushed == true) { Desktop.GlideTo(lv.X, lv.Y); Thread.Sleep(120); } // pushed aside: centre first
                        release = CommandPoint(lv);
                    }
                    Desktop.GlideTo(release.X, release.Y);
                    Thread.Sleep(scenario == "outside" ? 120 : (int)MenuNavigator.ArmDelay.TotalMilliseconds + 150);
                    releasedOver = explorer.ReleaseTarget(release.X, release.Y);
                    var wanted = scenario == "outside" ? "explorer" : "overlay";
                    if (releasedOver != wanted) { CancelDrag(); Thread.Sleep(150); releasedOver = $"refused({releasedOver ?? "unsafe"})"; }
                }
                Desktop.LeftUp();
            }
            if (heldVk != 0) { Thread.Sleep(40); Desktop.KeyUp(heldVk); }
            if (scenario == "alt")
            {
                Thread.Sleep(40);
                Desktop.KeyUp(Desktop.VkAlt);
                Thread.Sleep(150);
                if (Desktop.ClassOf(Desktop.GetForegroundWindow()) == "#32768") Desktop.Tap(Desktop.VkEscape); // a window menu left open
            }

            if (scenario == "batch")
            {
                var wanted = batchSources.Select(f => Path.Combine(folder, OutputNameFor(f)!)).ToArray();
                var waited = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(waited) < TimeSpan.FromSeconds(60) && !wanted.All(File.Exists)) Thread.Sleep(200);
                Thread.Sleep(500);
                var allValid = wanted.All(w => File.Exists(w) && File.ReadAllBytes(w) is var wb && FileSignature.Detect(wb) == outputFormat && ContainerCheck.IsComplete(wb, outputFormat, out _));
                var intact = batchSources.All(f => File.Exists(f) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) == batchHashes[f]);
                var present = Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
                var expectedSet = batchSources.Concat(wanted).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
                var (btoast, bfocus) = WaitForSurface("WinTools.Toast", explorer.Hwnd, TimeSpan.FromSeconds(3));
                var bev = HostEventsSinceLastCycle();
                var baccepted = bev.Count(e => Ev(e) == "drop" && Is(e, "accepted", true));
                var bsounds = SoundsHeld(bev);
                var bok = allValid && intact && present.SequenceEqual(expectedSet) && baccepted == 1 && btoast && bfocus && bsounds;
                if (bok) passed++; else exitCode = 1;
                Record(new { scenario, cycle, op = op?.Id, files = batchSources.Count, outputs = wanted.Count(File.Exists), allValid, sourcesIntact = intact, cleanFolder = present.SequenceEqual(expectedSet), hostAccepted = baccepted,
                    toastShown = btoast, focusKept = bfocus, soundsHeld = bsounds, ok = bok });
                foreach (var w in wanted) if (File.Exists(w)) File.Delete(w);
                Thread.Sleep((int)Desktop.GetDoubleClickTime() + 300);
                continue;
            }

            // Outcome: wait for the output where one is expected; otherwise give a wrong job time to show up.
            string? output = null;
            var deadline = Stopwatch.GetTimestamp();
            var waitFor = expectOutput || scenario == "fast" ? TimeSpan.FromSeconds(scenario == "fast" ? 4 : 15) : TimeSpan.FromSeconds(3);
            var infoShown = false;
            bool? toastShown = null, focusKept = null;
            while (Stopwatch.GetElapsedTime(deadline) < waitFor)
            {
                var produced = Directory.GetFiles(folder).Where(f => !string.Equals(f, source, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (produced.Length > 0 && !produced.Any(f => Path.GetFileName(f).StartsWith("~wintools-", StringComparison.Ordinal))) { output = produced.Single(); break; }
                if (facts && Desktop.VisibleWindowsOf(host.Id, "WinTools.InfoPanel").Count > 0)
                {
                    // "Image info": the panel, no file. Explorer must still be in front; then close it as a user would.
                    infoShown = true;
                    focusKept = Desktop.GetForegroundWindow() == explorer.Hwnd;
                    Desktop.CloseWindowsOf(host.Id, "WinTools.InfoPanel");
                    break;
                }
                Thread.Sleep(100);
            }
            if (output is not null) (toastShown, focusKept) = WaitForSurface("WinTools.Toast", explorer.Hwnd, TimeSpan.FromSeconds(3));
            Thread.Sleep(300);
            var files = Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray(); // the "folder" scenario's source is a folder
            var sourceIntact = (isFolder ? Directory.Exists(source) : File.Exists(source)) && HashOf(source) == sourceHash;
            var outputValid = output is not null && File.ReadAllBytes(output) is var bytes && FileSignature.Detect(bytes) == outputFormat && ContainerCheck.IsComplete(bytes, outputFormat, out _)
                && (!strip || bytes.AsSpan().IndexOf(metadataMarker) < 0); // the comment is gone
            var ev = HostEventsSinceLastCycle();
            var enters = ev.Count(e => Ev(e) == "drag.enter");
            var drops = ev.Where(e => Ev(e) == "drop").ToArray();
            var accepted = drops.Count(e => Is(e, "accepted", true));
            double? openMs = drops.FirstOrDefault() is { ValueKind: JsonValueKind.Object } d && Data(d).TryGetProperty("openMs", out var om) ? om.GetDouble() : null;
            // The rule, per drop: it runs exactly when a command was under the pointer and its ring had been open for the arm delay.
            var ruleHeld = drops.All(e => Is(e, "accepted", true) == ((Data(e).TryGetProperty("op", out _) || (Data(e).TryGetProperty("hot", out var h) && h.ValueKind == JsonValueKind.String))
                && Data(e).TryGetProperty("openMs", out var ms) && ms.GetDouble() >= MenuNavigator.ArmDelay.TotalMilliseconds));
            // The drag ended on the overlay: a Drop, or a DragLeave after the release over a spot that offers nothing.
            bool HiddenFor(string why) => ev.Any(e => Ev(e) == "overlay.hide" && Data(e).TryGetProperty("reason", out var r) && r.GetString() == why);
            var endedOnOverlay = drops.Length > 0 || HiddenFor("dragLeave");
            var expectedFiles = output is null ? [sourceName] : new[] { sourceName, Path.GetFileName(output) }.Order(StringComparer.Ordinal).ToArray();
            var cleanFolder = files.SequenceEqual(expectedFiles);
            var menuOpens = ev.Where(e => Ev(e) == "menu.open").Select(e => Data(e).TryGetProperty("group", out var gr) ? gr.GetString() : null).ToArray();
            var menuBacks = ev.Count(e => Ev(e) == "menu.back");
            var soundsHeld = SoundsHeld(ev);
            bool? refusedLogged = scenario == "alt" ? ev.Any(e => Ev(e) == "trigger.refused") : null;
            bool? overlayShown = scenario == "alt" ? ev.Any(e => Ev(e) == "overlay.show") : null;
            if (scenario == "alt") (toastShown, focusKept) = (Desktop.VisibleWindowsOf(host.Id, "WinTools.Toast").Count > 0 || ev.Any(e => Ev(e) == "toast" && e.TryGetProperty("data", out var d) && d.TryGetProperty("kind", out var k) && k.GetString() == "triggerRefused" && d.TryGetProperty("shown", out var s) && s.GetBoolean()), Desktop.GetForegroundWindow() == explorer.Hwnd);
            // Every result the user can see came without taking the foreground from Explorer.
            var feedbackOk = output is null || (toastShown == true && focusKept == true);
            var ok = sourceIntact && cleanFolder && soundsHeld && feedbackOk && scenario switch
            {
                "png" or "jpg" or "bmp" or "tif" or "webp" or "file" or "folder" when facts => output is null && infoShown && focusKept == true && accepted == 1,
                "png" or "jpg" or "bmp" or "tif" or "webp" => output is not null && Path.GetFileName(output) == outputName && outputValid && accepted == 1,
                // Corner: the trigger really was off-centre and deep in a sector, nothing opened before the centre was
                // reached, then exactly the wanted category opened and the wanted command ran.
                "edge" => output is not null && Path.GetFileName(output) == outputName && outputValid && accepted == 1
                          && startOffset >= RingGeometry.Scale(RingGeometry.OpenRadius96, assumed.Item3) && opensBeforeCentre == 0
                          && menuOpens.SequenceEqual([menu[groupIndex].Label]) && menuBacks == 0,
                // Fast release, judged by the host. A pointer on a command gets COPY, so a drag that reached a command must
                // end in a Drop on the overlay (none lost to the window below, none silently cancelled); the drop runs
                // exactly when the ring had been open for the arm delay; an accepted drop produces a valid output and a
                // refused one nothing.
                "fast" => (enters == 0 || endedOnOverlay) && ruleHeld && (accepted == 0) == (output is null) && (output is null || outputValid)
                          && (menuOpens.Length == 0 || menuOpens.SequenceEqual([menu[groupIndex].Label])) // never a wrong category
                          && (releasedOver != "overlay" || categorySeen != true || drops.Length == 1)
                          // With time to spare the stroke must have been seen, and a release well after the delay must run.
                          && (fastMs < 200 || categorySeen == true) && (fastMs < 300 || accepted == 1),
                "worker-crash" => output is null && accepted == 1, // the drop ran; the crashed job left nothing behind
                // Nothing runs, and the release really happened where the scenario says.
                "outside" => output is null && accepted == 0 && releasedOver == "explorer" && menuOpens.SequenceEqual([menu[groupIndex].Label]),
                "escape" => output is null && accepted == 0 && HiddenFor("escape"), // the host saw the Escape
                // Alt held: no ring, nothing run, and the user was told why (the refusal toast), Explorer still in front.
                "alt" => output is null && accepted == 0 && refusedLogged == true && overlayShown == false && toastShown == true && focusKept == true,
                _ => output is null && accepted == 0 && releasedOver == "overlay" && menuOpens.Length == 0, // unsupported, category
            };
            if (ok) passed++; else exitCode = 1;
            Record(new { scenario, cycle, op = op?.Id, releasedOver, releaseAfterMs, hostEnters = enters, hostDrops = drops.Length, hostAccepted = accepted, openMs, ruleHeld, endedOnOverlay, infoShown,
                toastShown, focusKept, soundsHeld, refusedLogged, overlayShown,
                startOffset, opensBeforeCentre, levelPushed, categorySeen, menuOpens, menuBacks,
                triggerAt = $"{at.X},{at.Y}", releaseAt = $"{release.X},{release.Y}", output = output is null ? null : Path.GetFileName(output), outputValid, sourceIntact, cleanFolder, files, ok });
            if (output is not null) File.Delete(output); // the harness's own result file, so the next cycle starts clean
            Thread.Sleep((int)Desktop.GetDoubleClickTime() + 300);
        }
        catch
        {
            // Never leave a synthetic drag behind: cancel it, release everything, stop the host, then fail loudly.
            Desktop.KeyUp(Desktop.VkControl); Desktop.KeyUp(Desktop.VkShift); Desktop.KeyUp(Desktop.VkAlt); // before Escape
            Desktop.Tap(Desktop.VkEscape); Desktop.KeyUp(Desktop.VkSpace); Desktop.LeftUp();
            if (!host.HasExited) host.Kill();
            throw;
        }
    }
}

var hostAlive = !host.HasExited;
double? hostPrivateMb = null, hostWorkingSetMb = null;
if (hostAlive) { host.Refresh(); hostPrivateMb = Math.Round(host.PrivateMemorySize64 / 1048576.0, 1); hostWorkingSetMb = Math.Round(host.WorkingSet64 / 1048576.0, 1); }
if (hostAlive) { Desktop.CloseWindowsOf(host.Id, "WinTools.InfoPanel"); host.Kill(); host.WaitForExit(5000); }
// The harness must leave no injected input behind; say so in the summary and fail if it did.
var leftDownAtEnd = Desktop.IsDown(0x01);
var spaceDownAtEnd = Desktop.IsDown(Desktop.VkSpace);
var modifiersDownAtEnd = Desktop.IsDown(Desktop.VkControl) || Desktop.IsDown(Desktop.VkShift) || Desktop.IsDown(Desktop.VkAlt);
if (leftDownAtEnd || spaceDownAtEnd || modifiersDownAtEnd)
{
    Desktop.KeyUp(Desktop.VkControl); Desktop.KeyUp(Desktop.VkShift); Desktop.KeyUp(Desktop.VkAlt); // before Escape
    Desktop.Tap(Desktop.VkEscape); Desktop.KeyUp(Desktop.VkSpace); Desktop.LeftUp();
    exitCode = 5;
}
Record(new { scenario, summary = true, cycles, passed, hostAliveAtEnd = hostAlive, leftDownAtEnd, spaceDownAtEnd, modifiersDownAtEnd, hostPrivateMb, hostWorkingSetMb, hostLog = Path.GetFullPath(hostLog), folder });
if (!hostAlive) exitCode = 2;
try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
return exitCode;
