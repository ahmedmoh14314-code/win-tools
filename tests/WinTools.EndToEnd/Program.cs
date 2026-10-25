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
// Usage: WinTools.EndToEnd <png|jpg|bmp|tif|webp|photo|batch|escape|outside|unsupported|category|fast|edge|worker-crash|alt|file|folder>
//          [--op <operation id>] [--target jpeg|png|webp|bmp|tiff] [--batch N] [--cycles N] [--fast-ms N] [--results file.jsonl]
//          [--animations on|off|system]   (default on: the menu's animations run, the heavier path for latency)
//          [--trigger Space|F2|...]       (default Space: the host's trigger key, written to its settings and pressed here)
//          [--host <WinTools.Host.exe>]   (default: the host next to the harness; e.g. an installed copy)
//          [--select ctrl-a|ctrl-click|shift-range] [--hold none|ctrl|shift] [--settle-ms N]   (batch selection;
//          defaults ctrl-a, none, 250 as before; the release matrix uses 0: drag at once after selecting)
//          [--path-style plain|unicode|long]   (unicode: Arabic, emoji and spaces in the folder and file names; long: the
//          source's full path is longer than 260 characters, its folder just under 248 so Explorer can open it)
// "alt": a drag with Alt held, then the trigger: nothing may open or run, and the refusal toast must appear.
// "pdf" (V2 PR 2.1/2.3): a 3-page PDF (tests/samples/pages-3.pdf) with a PDF command (default pdf.rotate-right; --op
// pdf.optimize, pdf.linearize, pdf.info...): a new PDF with the command's name, or the info panel; the source unchanged.
// "zip" (V2 PR 5.1): tests/samples/photos-3.zip with an archive command (default archive.extract; --op archive.test,
// archive.info): a new folder "photos" holding exactly the archive's tree, or the info panel; the archive unchanged.
// "srt" (V2 PR 7.1): an Arabic SRT the harness writes, with a subtitle command (default subtitles.to-vtt; --op
// subtitles.shift-plus-1000, subtitles.info...): a new subtitle file with the module's name, or the info panel.
// "docx" (V2 PR 6.1): a small Word document the harness builds, with Extract text (default) or Document info (--op
// documents.info): a new "report (text).txt" holding its text, or the info panel; the document unchanged.
// "file" and "folder" (V2 PR 1.4, UX-009): a text file and a folder, each with File info by default (--op files.sha256
// for a file): the info panel must appear, nothing may be written. Copy path is not run here: it would overwrite the
// user's clipboard (unit-tested in FilesModuleTests; manual protocol in docs/testing/release-matrix.md).
// "keyprobe --key K" (SPIKE, ADR-0021): during a drag, K is pressed instead of the trigger; Explorer must not act on it
// (selection, focus, title unchanged during and after the drag). Decides which keys may become trigger keys.
// "keycontrol --key K" (SPIKE, the probe's positive control): the same window and files, K pressed with the item
// selected and NO drag; Explorer is expected to react (F2 rename, F3 search, a letter's type-ahead), which shows that the
// probe sees a reaction when there is one. Passes when the state changed.
// "photo": a 960x720 photograph-like JPEG (tests/samples/New-PhotoSample.ps1), compressed (default compress.balanced):
// the copy must be valid and save at least 8 %, the rule Compress promises (ADR-0017). With --expect already-optimized
// the honest outcome is the other one: no file, a "Nothing to change" toast, the job reported as NothingToDo.
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
if (Desktop.ButtonsSwapped)
{
    Console.Error.WriteLine("The mouse buttons are swapped in Windows settings; injected clicks would be right-clicks. Not running.");
    return 6;
}
string probeKey = "F5";
var triggerKey = "Space";
var cycles = 10; var fastMs = 25; string? resultsPath = null; string? targetArg = null; string? opArg = null; var batchSize = 12; var animations = "On"; string? hostArg = null; var selectMode = "ctrl-a"; var holdKey = "none"; var settleMs = 250; var pathStyle = "plain"; var expectNothing = false;
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
        case "--key": probeKey = args[++i]; break;                   // keyprobe: F2..F12, A..Z or 0..9
        case "--trigger": triggerKey = args[++i]; break;
        case "--expect": expectNothing = args[++i] switch { "copy" => false, "already-optimized" => true, var x => throw new ArgumentException($"--expect {x}") }; break;
        case "--path-style": pathStyle = args[++i] is "plain" or "unicode" or "long" ? args[i] : throw new ArgumentException($"--path-style {args[i]}"); break;
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
    "photo" => ("photo-960x720.jpg", "photo.jpg", ImageFormat.Jpeg),
    "unsupported" or "file" => ("", "notes.txt", ImageFormat.Unknown),
    "folder" => ("", "holiday photos", ImageFormat.Unknown),
    "pdf" => ("pages-3.pdf", "report.pdf", ImageFormat.Unknown),
    "zip" => ("photos-3.zip", "photos.zip", ImageFormat.Unknown),
    "srt" => ("", "film.srt", ImageFormat.Unknown),
    "docx" => ("", "report.docx", ImageFormat.Unknown),
    _ => ("quadrants-64x48.png", "photo.png", ImageFormat.Jpeg),
};
// The host runs with default settings, so the harness sees the same menu.
var catalog = ImageCapabilities.Operations(new AppSettings().CustomResizeId());
var target = targetArg is null ? defaultTarget : Enum.Parse<ImageFormat>(targetArg, ignoreCase: true);
if (scenario == "photo") opArg ??= "compress.balanced";
if (opArg is null && scenario is "file" or "folder") opArg = FilesCapabilities.InfoId;
if (opArg is null && scenario == "pdf") opArg = PdfCapabilities.RotateRightId;
if (opArg is null && scenario == "zip") opArg = ArchiveCapabilities.ExtractId;
if (opArg is null && scenario == "srt") opArg = SubtitleCapabilities.ToVttId;
if (opArg is null && scenario == "docx") opArg = DocumentCapabilities.ExtractTextId;
var op = opArg is not null ? catalog.FirstOrDefault(o => o.Id == opArg) : target == ImageFormat.Unknown ? null : OperationCatalog.Convert(target);
// Every module, as the host resolves them: the ring positions the harness aims at must be the host's.
var resolver = CapabilityResolver.For(new AppSettings().CustomResizeId());
var capability = resolver.Catalog.FirstOrDefault(c => c.Id == (opArg ?? op?.Id));
if (opArg is not null && capability is null) { Console.Error.WriteLine($"Unknown operation {opArg}"); return 64; }
var facts = op is { WritesFile: false } || capability?.Produces == CapabilityOutput.Facts; // the info panel, no file
var sourceFormat = FormatHint.FromPath(sourceName);
var outputFormat = op is null || !op.WritesFile ? ImageFormat.Unknown : op.OutputFormat(sourceFormat);
// A PDF command's output is a PDF named by the module (V2 PR 2.1); Split's folder is not driven here (worker tests).
var pdfFile = scenario == "pdf" && capability is { Module: PdfCapabilities.Module, Produces: CapabilityOutput.File };
// Extract makes a folder named after the archive.
var zipFolder = scenario == "zip" && capability?.Id == ArchiveCapabilities.ExtractId;
var subtitleFile = scenario == "srt" && capability is { Module: SubtitleCapabilities.Module, Produces: CapabilityOutput.File };
var documentText = scenario == "docx" && capability?.Id == DocumentCapabilities.ExtractTextId;
string? OutputNameFor(string path) => zipFolder ? Path.GetFileNameWithoutExtension(path)
    : subtitleFile ? Path.GetFileName(SubtitleCapabilities.Candidates(capability!.Id, path, null).First())
    : documentText ? Path.GetFileName(DocumentCapabilities.Candidates(path, null).First())
    : pdfFile ? Path.GetFileName(OutputNaming.CandidatesWithExtension(path, ".pdf", PdfCapabilities.NameSuffix(capability!.Id)).First())
    : outputFormat == ImageFormat.Unknown ? null : Path.GetFileName(OutputNaming.Candidates(path, outputFormat, op!.NameSuffix).First());
var producing = scenario is "png" or "jpg" or "bmp" or "tif" or "webp" or "photo" or "batch" or "edge" || pdfFile || zipFolder || subtitleFile || documentText;
var expectOutput = producing && (outputFormat != ImageFormat.Unknown || pdfFile || zipFolder || subtitleFile || documentText);
var stamp = DateTime.Now.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture);
var folder = Path.Combine(Path.GetTempPath(), "wintools-e2e", pathStyle == "unicode" ? $"{scenario} مجلد 😀 {stamp}" : $"{scenario}-{stamp}");
if (pathStyle == "long")
{
    // Nest until the folder is just under 248 characters (the longest folder Explorer opens); the file name then takes
    // the source's full path past 260, which the product must handle (\\?\ paths).
    while (folder.Length < 220) folder = Path.Combine(folder, "long path segment 0123456789");
    // Pad to 246 when there is room; a temp path that already lands at 245-248 needs no pad (evaluator, PR #14: the pad
    // went negative for some profile-name lengths).
    if (folder.Length <= 244) folder = Path.Combine(folder, new string('d', 246 - folder.Length - 1));
}
if (pathStyle != "plain")
    sourceName = (pathStyle == "unicode" ? "صورة 😀 " : "a long file name for the 260 character test ") + sourceName;
Directory.CreateDirectory(folder);
var source = Path.Combine(folder, sourceName);
var isFolder = scenario == "folder";
if (isFolder) { Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "inside.txt"), "a file in the dropped folder"); }
else if (sample.Length > 0) File.Copy(Path.Combine(AppContext.BaseDirectory, "samples", sample), source);
else if (scenario == "docx")
{
    using var zip = System.IO.Compression.ZipFile.Open(source, System.IO.Compression.ZipArchiveMode.Create);
    void Part(string name, string xml) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(xml); }
    Part("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""");
    Part("word/document.xml", """<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>تقرير الربع 😀</w:t></w:r></w:p><w:p><w:r><w:t>Second line</w:t></w:r></w:p></w:body></w:document>""");
    // Properties as Office writes them: app.xml carries vt:vector elements (V2 PR 6.2 found Info failing on them).
    Part("docProps/core.xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><dc:title>تقرير الربع</dc:title><dc:creator>WinTools E2E</dc:creator><dcterms:modified xsi:type="dcterms:W3CDTF">2026-10-25T03:00:00Z</dcterms:modified></cp:coreProperties>""");
    Part("docProps/app.xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties" xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes"><Template>Normal.dotm</Template><Pages>1</Pages><Words>5</Words><Application>Microsoft Office Word</Application><HeadingPairs><vt:vector size="2" baseType="variant"><vt:variant><vt:lpstr>Title</vt:lpstr></vt:variant><vt:variant><vt:i4>1</vt:i4></vt:variant></vt:vector></HeadingPairs><TitlesOfParts><vt:vector size="1" baseType="lpstr"><vt:lpstr></vt:lpstr></vt:vector></TitlesOfParts><Company></Company></Properties>""");
}
else if (scenario == "srt") File.WriteAllText(source, "1\r\n00:00:01,000 --> 00:00:02,500\r\nمرحبا بكم 😀\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nإلى اللقاء\r\n\r\n");
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
// keyprobe with a letter: a file that Explorer's type-ahead would select if the letter reached it during the drag.
if (scenario is "keyprobe" or "keycontrol" && probeKey.Length == 1)
    File.WriteAllText(Path.Combine(folder, $"{char.ToUpperInvariant(probeKey[0])}{char.ToUpperInvariant(probeKey[0])} probe.txt"), "type-ahead target");
// A folder's "hash" is its listing: a dropped folder must keep its contents.
string HashOf(string path) => isFolder ? string.Join("|", Directory.GetFileSystemEntries(path).Order(StringComparer.Ordinal)) : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
var sourceHash = HashOf(source);
var outputName = OutputNameFor(source);
// Batch: the source plus copies with Unicode names, all selected with Ctrl+A and dragged together.
var batchSources = new List<string> { source };
if (scenario == "batch")
    for (var b = 2; b <= batchSize; b++)
    {
        var copy = Path.Combine(folder, pathStyle == "long" ? $"a long file name for the 260 character test {b:000}.png" : $"photo {b:00} صورة.png");
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
if (!AppSettings.TriggerKeys.TryGetValue(triggerKey, out var triggerVk)) { Console.Error.WriteLine($"--trigger {triggerKey} is not an allowed trigger key."); return 64; }
Desktop.TriggerVk = triggerVk;
// Alt with another trigger is another shortcut (Alt+F4 closes the window): the "alt" scenario is Alt+Space only.
if (scenario == "alt" && triggerKey != "Space") { Console.Error.WriteLine("The alt scenario runs with the Space trigger only."); return 64; }
// SPIKE: the probe key's virtual-key code (F2..F24, A..Z, 0..9); released with the rest and checked at the end.
static int? ProbeVk(string key) => key is { Length: 1 } k && char.IsAsciiLetterOrDigit(k[0]) ? char.ToUpperInvariant(k[0])
    : key is ['F', ..] && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24 ? 0x6F + f : null;
if (scenario is "keyprobe" or "keycontrol" && ProbeVk(probeKey) is { } pvk) Desktop.ProbeVk = pvk;
File.WriteAllText(settingsFile, new AppSettings { Animations = animations, TriggerKey = triggerKey }.ToJson());
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

ExplorerWindow opened;
try { opened = ExplorerWindow.Open(folder); }
catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception
    or System.Windows.Automation.ElementNotAvailableException)
{
    // Never leave the harness's host running behind a failed start.
    if (!host.HasExited) { host.Kill(); host.WaitForExit(5000); }
    Console.Error.WriteLine(ex.Message);
    return 3;
}
using (var explorer = opened)
{
    // SPIKE: the letter types what the window's keyboard layout says ("ش" for A on an Arabic layout), so a type-ahead
    // target named with that character is added as well; otherwise a layout other than English hides any reaction.
    if (scenario is "keyprobe" or "keycontrol" && probeKey.Length == 1)
    {
        var typed = Desktop.CharOf(char.ToUpperInvariant(probeKey[0]), explorer.Hwnd);
        Record(new { probeKey, layoutChar = typed?.ToString() });
        if (typed is { } t && char.ToUpperInvariant(t) != char.ToUpperInvariant(probeKey[0]))
        {
            File.WriteAllText(Path.Combine(folder, $"{t}{t} probe.txt"), "type-ahead target");
            Thread.Sleep(1000);
        }
    }
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
            // A click at the cursor; returns the time of its button-up, the moment the selection input ends.
            static long Click() { Desktop.LeftDown(); Thread.Sleep(30); Desktop.LeftUp(); var at = Stopwatch.GetTimestamp(); Thread.Sleep(60); return at; }
            double? pressGapMs = null; string? selectionText = null; var candidateCount = 0;
            if (scenario == "batch")
            {
                // Select the harness folder's files (guarded), then drag one of the selected items. With --hold the modifier
                // used for the selection stays down through the drag and the drop, as a user selecting with Ctrl-click does.
                Desktop.MoveTo(cx, cy); Thread.Sleep(40);
                Desktop.LeftDown(); Thread.Sleep(30); Desktop.LeftUp();
                Thread.Sleep((int)Desktop.GetDoubleClickTime() + 200);
                if (!explorer.EnsureForeground(cx, cy)) { Record(new { cycle, abort = "foreground lost before selecting" }); exitCode = 4; break; }
                // Where to press is worked out BEFORE the last selection input: walking the UI Automation tree takes time, and
                // "--settle-ms 0" must mean a press right after selecting, as a quick user does (evaluator, PR #14). Positions
                // do not change with the selection; the gap from the last selection input to the press is recorded.
                var names = batchSources.Select(f => Path.GetFileName(f)!).ToArray();
                List<(int X, int Y)> Candidates() => explorer.RealisedItemCentres(names)
                    .Where(v => Desktop.Inside(explorer.Hwnd, v.X, v.Y, Px(8)) && explorer.HitsItem(v.X, v.Y, names)).ToList();
                List<(int X, int Y)> visible = [];
                var selectedAt = 0L;
                switch (selectMode)
                {
                    case "ctrl-a":
                        visible = Candidates();
                        Desktop.KeyDown(Desktop.VkControl); Desktop.Tap('A');
                        selectedAt = Stopwatch.GetTimestamp();
                        if (holdKey != "ctrl") Desktop.KeyUp(Desktop.VkControl);
                        break;
                    case "ctrl-click":
                        // The first item is already selected by the plain click; add the others one by one with Ctrl held.
                        visible = Candidates();
                        Desktop.KeyDown(Desktop.VkControl);
                        foreach (var other in batchSources.Where(f => Path.GetFileName(f) != grabName))
                        {
                            // The item itself must be under the point: a realised item can lie under the status bar or past the
                            // list, where the click selects nothing (evaluator, PR #14: the likely cause of a 9-of-10 selection).
                            var otherName = Path.GetFileName(other)!;
                            if (explorer.ItemCentre(otherName) is not { } oc || !Desktop.Inside(explorer.Hwnd, oc.X, oc.Y, Px(8))
                                || !explorer.HitsItem(oc.X, oc.Y, [otherName]))
                            {
                                Desktop.KeyUp(Desktop.VkControl);
                                Record(new { cycle, abort = "item to Ctrl-click not visible", item = otherName });
                                exitCode = 4; break;
                            }
                            Desktop.MoveTo(oc.X, oc.Y); Thread.Sleep(30);
                            (lastClick, lastClickAt) = (oc, Click());
                        }
                        if (exitCode == 4) break;
                        selectedAt = lastClickAt;
                        if (holdKey != "ctrl") Desktop.KeyUp(Desktop.VkControl);
                        break;
                    case "shift-range":
                        // The whole list as one range, as a user makes it: click the first item, Shift-click the last. In a list
                        // longer than the view, the user scrolls with the wheel in between (the wheel leaves the selection alone).
                        // Explorer's list ignores injected Home and End, so a keyboard range is not used. Shift is released
                        // before the press, because a Shift-press re-ranges from a different anchor (ADR-0003).
                        var allNames = batchSources.Select(f => Path.GetFileName(f)!).ToArray();
                        if (explorer.EndItemCentre(last: false) is not { } first || !explorer.HitsItem(first.X, first.Y, allNames))
                        {
                            Record(new { cycle, abort = "first item not visible" }); exitCode = 4; break;
                        }
                        // The folder holds only these files, named so that Explorer's name order is ordinal order.
                        var lastName = allNames.Order(StringComparer.Ordinal).Last();
                        bool IsLast((string Name, (int X, int Y) Centre)? item) =>
                            item is { } i && (i.Name == lastName || i.Name == Path.GetFileNameWithoutExtension(lastName)) && explorer.HitsItem(i.Centre.X, i.Centre.Y, allNames);
                        (int X, int Y)? end = explorer.EndItem(last: true) is var e0 && IsLast(e0) ? e0!.Value.Centre : null;
                        if (end is null && holdKey == "shift") { Record(new { cycle, abort = "Shift held needs the whole range on screen (Explorer re-ranges a Shift-press)" }); exitCode = 4; break; }
                        if (end is null)
                        {
                            Desktop.MoveTo(first.X, first.Y); Thread.Sleep(30);
                            Desktop.LeftDown(); Thread.Sleep(30); Desktop.LeftUp(); Thread.Sleep(60);
                            for (var turn = 0; turn < 60 && end is null; turn++)
                            {
                                Desktop.Wheel(-3); Thread.Sleep(60);
                                if (explorer.EndItem(last: true) is var e && IsLast(e)) end = e!.Value.Centre;
                            }
                            if (end is not { } scrolledEnd) { Record(new { cycle, abort = "last item never scrolled into view" }); exitCode = 4; break; }
                            visible = Candidates();
                            Thread.Sleep((int)Desktop.GetDoubleClickTime());
                            if (!explorer.EnsureForeground(scrolledEnd.X, scrolledEnd.Y)) { Record(new { cycle, abort = "foreground lost while scrolling" }); exitCode = 4; break; }
                            Desktop.KeyDown(Desktop.VkShift);
                            Desktop.MoveTo(scrolledEnd.X, scrolledEnd.Y); Thread.Sleep(30);
                            var shiftUpAt = Click();
                            Desktop.KeyUp(Desktop.VkShift);
                            (lastClick, lastClickAt) = (scrolledEnd, shiftUpAt);
                            selectedAt = lastClickAt;
                            break;
                        }
                        visible = Candidates();
                        Desktop.MoveTo(first.X, first.Y); Thread.Sleep(30);
                        Desktop.LeftDown(); Thread.Sleep(30); Desktop.LeftUp(); Thread.Sleep(60);
                        Desktop.KeyDown(Desktop.VkShift);
                        Desktop.MoveTo(end.Value.X, end.Value.Y); Thread.Sleep(30);
                        (lastClick, lastClickAt) = (end, Click());
                        selectedAt = lastClickAt;
                        if (holdKey != "shift") Desktop.KeyUp(Desktop.VkShift);
                        break;
                }
                if (exitCode == 4) break;
                if (heldVk != 0 && !Desktop.IsDown(heldVk)) Desktop.KeyDown(heldVk);
                if (settleMs > 0) Thread.Sleep(settleMs);
                // Drag from a selected item that is visible now. With Shift held, Explorer treats the press as a Shift-click and
                // re-ranges the selection from its anchor, so press on the range's end, as a user keeping Shift down would.
                // Never press where the last click landed within the double-click time: Explorer would open the file.
                if (visible.Count == 0) { if (heldVk != 0) Desktop.KeyUp(heldVk); Record(new { cycle, abort = "no selected item visible to drag" }); exitCode = 4; break; }
                candidateCount = visible.Count;
                var vis = selectMode == "shift-range" && holdKey == "shift" && lastClick is { } rangeEnd ? rangeEnd
                    // Shift released: any selected item will do, so take the middle one (the range's end may sit at the very
                    // bottom of the view, partly hidden, where a press starts no drag).
                    : selectMode == "shift-range" ? visible.OrderBy(v => v.Y).ElementAt(visible.Count / 2)
                    : visible.FirstOrDefault(v => lastClick is not { } lc || Math.Abs(v.X - lc.X) + Math.Abs(v.Y - lc.Y) > Px(8), visible[0]);
                var sinceClick = Stopwatch.GetElapsedTime(lastClickAt).TotalMilliseconds;
                if (lastClick is { } l && Math.Abs(vis.X - l.X) + Math.Abs(vis.Y - l.Y) <= Px(8) && sinceClick < Desktop.GetDoubleClickTime() + 50)
                    Thread.Sleep((int)(Desktop.GetDoubleClickTime() + 50 - sinceClick));
                (cx, cy) = vis;
                Desktop.MoveTo(cx, cy);
                pressGapMs = Math.Round(Stopwatch.GetElapsedTime(selectedAt).TotalMilliseconds, 1);
                Desktop.LeftDown(); Thread.Sleep(80);
            }
            else if (scenario == "keycontrol")
            {
                // SPIKE control: select the item with a click, no drag, then press the key as the probe does.
                Desktop.MoveTo(cx, cy); Thread.Sleep(60);
                Click();
                Thread.Sleep((int)Desktop.GetDoubleClickTime() + 200);
                var vkc = probeKey is { Length: 1 } l1 ? char.ToUpperInvariant(l1[0])
                    : probeKey is ['F', ..] && int.TryParse(probeKey[1..], out var fc) && fc is >= 2 and <= 24 ? 0x6F + fc
                    : throw new ArgumentException($"--key {probeKey}");
                var beforeC = explorer.ProbeState();
                Desktop.KeyDown(vkc); Thread.Sleep(60); Desktop.KeyUp(vkc);
                Thread.Sleep(700);
                var duringC = explorer.ProbeState();
                if (probeKey == "F11") { Desktop.Tap(vkc); Thread.Sleep(500); } // full screen is left with the same key
                Desktop.Tap(Desktop.VkEscape); Thread.Sleep(300); Desktop.Tap(Desktop.VkEscape); Thread.Sleep(300);
                var reacted = duringC != beforeC;
                Record(new { scenario, cycle, key = probeKey, reacted, before = beforeC, during = duringC, restored = explorer.ProbeState() == beforeC });
                if (reacted) passed++; else exitCode = 1;
                Thread.Sleep(400);
                continue;
            }
            else
            {
                Desktop.MoveTo(cx, cy); Thread.Sleep(60);
                Desktop.LeftDown(); Thread.Sleep(80);
            }
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
            if (scenario == "keyprobe")
            {
                // SPIKE (typing interference, ADR-0021, plan D8): press a candidate trigger key during a drag over this
                // window, as a user would press the trigger, and see whether Explorer acted on it: a changed selection
                // (type-ahead), a rename box, the search box or address bar, another pane, or a new title. The drag is
                // then cancelled and anything the key started is closed with Escape, so the window is left as it was.
                var vk = probeKey is { Length: 1 } letter ? char.ToUpperInvariant(letter[0])
                    : probeKey is ['F', ..] && int.TryParse(probeKey[1..], out var fn) && fn is >= 1 and <= 24 ? 0x6F + fn
                    : throw new ArgumentException($"--key {probeKey}");
                var before = explorer.ProbeState();
                Desktop.KeyDown(vk); Thread.Sleep(60); Desktop.KeyUp(vk);
                Thread.Sleep(700);
                var during = explorer.ProbeState();
                CancelDrag(); Thread.Sleep(150);
                Desktop.LeftUp(); Thread.Sleep(700);
                var after = explorer.ProbeState();
                var inert = during == before && after == before;
                if (!inert) { if (probeKey == "F11") { Desktop.Tap(vk); Thread.Sleep(500); } Desktop.Tap(Desktop.VkEscape); Thread.Sleep(300); }
                Record(new { scenario, cycle, key = probeKey, inert, before, during, after, restored = explorer.ProbeState() == before });
                if (inert) passed++; else exitCode = 1;
                Thread.Sleep(400);
                continue;
            }
            var tKey = Stopwatch.GetTimestamp();
            Desktop.KeyDown(triggerVk);
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
                Desktop.KeyUp(triggerVk);
            }
            else
            {
                Thread.Sleep(40);
                Desktop.KeyUp(triggerVk);
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
                selectionText = explorer.StatusText(); // diagnostics, read after the drop so it cannot delay the press
                // "At once" is a claim the record must prove: with --settle-ms 0 the press follows the last selection input
                // (its button-up, or the Ctrl+A tap) within 150 ms: the 60 ms pause after a click, the move to the item and the
                // input queue. A press on the item just clicked would need a double-click wait; the matrix rows never do that.
                var atOnce = settleMs > 0 || pressGapMs is <= 150;
                var bok = allValid && intact && present.SequenceEqual(expectedSet) && baccepted == 1 && btoast && bfocus && bsounds && atOnce;
                if (bok) passed++; else exitCode = 1;
                var dropped = bev.Where(e => Ev(e) == "drop" && Is(e, "accepted", true)).Select(e => Data(e).TryGetProperty("files", out var fc) ? fc.GetInt32() : -1).FirstOrDefault(-1);
                Record(new { scenario, cycle, op = op?.Id, select = selectMode, hold = holdKey, settleMs, pathStyle, files = batchSources.Count, pressAt = $"{cx},{cy}", pressGapMs, atOnce, candidates = candidateCount, selection = selectionText, dropped, outputs = wanted.Count(File.Exists), allValid, sourcesIntact = intact, cleanFolder = present.SequenceEqual(expectedSet), hostAccepted = baccepted,
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
                // Already optimized: the job ends without a file; stop waiting so the toast is still up when it is checked.
                if (expectNothing && HostLines().Skip(hostLogLines).Any(l => l.Contains("\"ev\":\"job.done\"", StringComparison.Ordinal))) break;
                var produced = (zipFolder ? Directory.GetFileSystemEntries(folder) : Directory.GetFiles(folder)).Where(f => !string.Equals(f, source, StringComparison.OrdinalIgnoreCase)).ToArray();
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
            if (output is not null || expectNothing) (toastShown, focusKept) = WaitForSurface("WinTools.Toast", explorer.Hwnd, TimeSpan.FromSeconds(3));
            Thread.Sleep(300);
            var files = Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray(); // the "folder" scenario's source is a folder
            var sourceIntact = (isFolder ? Directory.Exists(source) : File.Exists(source)) && HashOf(source) == sourceHash;
            // An extracted folder: exactly the sample's tree, with its content.
            bool ZipTreeValid(string dir) => Directory.Exists(dir)
                && Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(dir, p)).Order(StringComparer.Ordinal)
                    .SequenceEqual(["2026", @"2026\a.txt", @"2026\b.txt", "notes.txt"])
                && File.ReadAllText(Path.Combine(dir, "2026", "a.txt")) == "one\n";
            bool SubtitleValid(string file) => File.ReadAllText(file, System.Text.Encoding.UTF8) is var t && t.Contains("مرحبا بكم 😀", StringComparison.Ordinal)
                && (Path.GetExtension(file) == ".vtt" ? t.StartsWith("WEBVTT", StringComparison.Ordinal) : t.Contains(" --> ", StringComparison.Ordinal));
            var outputValid = output is not null && zipFolder ? ZipTreeValid(output!) : output is not null && subtitleFile ? SubtitleValid(output!)
                : output is not null && documentText ? File.ReadAllText(output!, System.Text.Encoding.UTF8) == "تقرير الربع 😀\r\nSecond line\r\n" : output is not null && File.ReadAllBytes(output) is var bytes && (pdfFile
                ? bytes.AsSpan().StartsWith("%PDF-"u8) && bytes.AsSpan(Math.Max(0, bytes.Length - 1024)).IndexOf("%%EOF"u8) >= 0 // a whole PDF
                : FileSignature.Detect(bytes) == outputFormat && ContainerCheck.IsComplete(bytes, outputFormat, out _)
                    && (!strip || bytes.AsSpan().IndexOf(metadataMarker) < 0)); // the comment is gone
            double? saved = op is { Kind: OperationKind.Compress } && output is not null ? Math.Round(1 - (double)new FileInfo(output).Length / new FileInfo(source).Length, 4) : null;
            if (saved is < 0.08) outputValid = false; // Compress writes a copy only when it saves at least 8 % (ADR-0017)
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
                "photo" or "pdf" when expectNothing => output is null && accepted == 1 && toastShown == true && focusKept == true
                    && ev.Any(e => Ev(e) == "toast" && Data(e).TryGetProperty("kind", out var tk) && tk.GetString() == "Nothing")
                    && ev.Any(e => Ev(e) == "job.result" && Data(e).TryGetProperty("error", out var er) && er.GetString() == "NothingToDo"),
                "png" or "jpg" or "bmp" or "tif" or "webp" or "photo" or "file" or "folder" or "pdf" or "zip" or "srt" or "docx" when facts => output is null && infoShown && focusKept == true && accepted == 1,
                "zip" or "srt" or "docx" => output is not null && Path.GetFileName(output) == outputName && outputValid && accepted == 1,
                "pdf" => output is not null && Path.GetFileName(output) == outputName && outputValid && accepted == 1,
                "png" or "jpg" or "bmp" or "tif" or "webp" or "photo" => output is not null && Path.GetFileName(output) == outputName && outputValid && accepted == 1,
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
                toastShown, focusKept, soundsHeld, refusedLogged, overlayShown, saved, pathStyle, sourcePathLength = source.Length,
                startOffset, opensBeforeCentre, levelPushed, categorySeen, menuOpens, menuBacks,
                triggerAt = $"{at.X},{at.Y}", releaseAt = $"{release.X},{release.Y}", output = output is null ? null : Path.GetFileName(output), outputValid, sourceIntact, cleanFolder, files, ok });
            // The harness's own result (a file, or an extracted folder inside its own temp folder), so the next cycle starts clean.
            if (output is not null) { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); else File.Delete(output); }
            Thread.Sleep((int)Desktop.GetDoubleClickTime() + 300);
        }
        catch
        {
            // Never leave a synthetic drag behind: cancel it, release everything (each on its own), stop the host, fail loudly.
            try { Desktop.ReleaseAll(); }
            finally { if (!host.HasExited) host.Kill(); }
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
var spaceDownAtEnd = Desktop.IsDown(Desktop.VkSpace) || Desktop.IsDown(triggerVk) || (Desktop.ProbeVk != 0 && Desktop.IsDown(Desktop.ProbeVk)); // the trigger or probe key
var modifiersDownAtEnd = Desktop.IsDown(Desktop.VkControl) || Desktop.IsDown(Desktop.VkShift) || Desktop.IsDown(Desktop.VkAlt);
if (leftDownAtEnd || spaceDownAtEnd || modifiersDownAtEnd)
{
    Desktop.ReleaseAll();
    exitCode = 5;
}
Record(new { scenario, summary = true, cycles, passed, hostAliveAtEnd = hostAlive, leftDownAtEnd, spaceDownAtEnd, modifiersDownAtEnd, hostPrivateMb, hostWorkingSetMb, hostLog = Path.GetFullPath(hostLog), folder });
if (!hostAlive) exitCode = 2;
try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
return exitCode;
