using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Jobs;
using WinTools.Core.Interaction;
using WinTools.Core.Settings;
using WinTools.Windows.Feedback;
using WinTools.Windows.Overlay;
using WinTools.Windows.Rendering;

// Renders the radial menu's states, the toasts and the info panel off screen to PNG files, at the DPIs given (default 96 and 144).
// Usage: WinTools.Preview <output folder> [dpi ...]
//        WinTools.Preview --icon <file.ico>   writes the product icon (the mark on a dark tile) at 16–256 px
if (args.Length == 2 && args[0] == "--icon") { ProductIcon.Write(Path.GetFullPath(args[1])); return 0; }
if (args.Length >= 1 && args[0] == "--bench") { Bench.Run(args.Length > 1 ? uint.Parse(args[1]) : 120); return 0; }
if (args.Length >= 1 && args[0] == "--live") { Live.Run(); return 0; }
if (args.Length >= 2 && args[0] == "--measure")
{
    foreach (var px in new[] { 11, 12, 13 })
        Console.WriteLine($"{px}px: " + string.Join("  ", args[1..].Select(t => $"{t}={Fonts.Width(t, px, RadialRenderer.LabelWeight)}")));
    return 0;
}
if (args.Length == 3 && args[0] == "--settings-shot") return SettingsShot.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
if (args.Length < 1) { Console.Error.WriteLine("Usage: WinTools.Preview <output folder> [dpi ...] | --icon <file.ico>"); return 64; }
var outDir = Path.GetFullPath(args[0]);
Directory.CreateDirectory(outDir);
var dpis = args.Length > 1 ? args[1..].Select(uint.Parse).ToArray() : [96u, 144u];

var catalog = RadialMenu.Catalog(new AppSettings().CustomResizeId());
var menu = RadialMenu.Build(new CapabilityResolver(catalog.Select(ImageCapabilities.Describe).ToArray()).Resolve([@"C:\images\photo.jpg"]));
var renderer = new RadialRenderer();
using var surface = new Surface();

void Ring(string name, uint dpi, MenuView view, Action<RingAnimation> setup)
{
    var anim = new RingAnimation(reducedMotion: false);
    anim.Start(view.Labels.Count, TimeSpan.Zero);
    setup(anim);
    var size = RadialRenderer.WindowSize(dpi);
    using (var p = surface.Begin(size, size, dpi)) renderer.Draw(p, size, view, anim);
    surface.SavePng(Path.Combine(outDir, $"radial-{name}-{dpi}.png"));
}

static void Settle(RingAnimation a, int hot, bool armed)
{
    a.Target(hot, armed, TimeSpan.FromMilliseconds(1));
    a.Advance(TimeSpan.FromSeconds(5));
}

foreach (var dpi in dpis)
{
    var nav = new MenuNavigator(menu, dpi);
    var top = nav.View(TimeSpan.Zero);
    Ring("waiting", dpi, RadialRenderer.Waiting, a => Settle(a, -1, false));
    Ring("top-appear", dpi, top, _ => { });
    Ring("top", dpi, top, a => Settle(a, -1, false));
    Ring("top-hover-convert", dpi, top, a => Settle(a, 0, false));
    for (var g = 0; g < menu.Count; g++)
    {
        var count = menu[g].Commands.Count;
        var labels = new[] { MenuNavigator.BackLabel }.Concat(menu[g].Commands.Select(c => c.Label)).ToArray();
        MenuView Level(int hot, bool armed) => new(labels, MenuNavigator.LevelStart(g, menu.Count, count), hot, armed, menu[g].Label,
            MenuNavigator.BackSweep(menu.Count, count), null, menu[g].Category);
        var key = menu[g].Label.ToLowerInvariant();
        Ring($"{key}", dpi, Level(-1, false), a => Settle(a, -1, false));
        Ring($"{key}-pending", dpi, Level(2, false), a => Settle(a, 2, false));
        Ring($"{key}-armed", dpi, Level(2, true), a => Settle(a, 2, true));
        Ring($"{key}-back", dpi, Level(0, false), a => Settle(a, 0, false));
    }
    Ring("no-actions", dpi, new MenuView([], 0, -1, false, ""), a => Settle(a, -1, false));

    void Toast(string name, ToastContent content)
    {
        var (w, h) = ToastRenderer.WindowSize(dpi);
        using (var p = surface.Begin(w, h, dpi)) ToastRenderer.Draw(p, w, h, content);
        surface.SavePng(Path.Combine(outDir, $"toast-{name}-{dpi}.png"));
    }
    var webp = OperationCatalog.Convert(WinTools.Core.Files.ImageFormat.WebP);
    Toast("success", ToastContent.From(FeedbackText.For(webp, [new("photo.jpg", ConversionResult.Ok("1", @"C:\x\photo.webp", "0"), 2_480_000, 612_000)])));
    Toast("batch", ToastContent.From(FeedbackText.For(webp, [.. Enumerable.Range(1, 48).Select(i => new JobItem($"p{i}.jpg", ConversionResult.Ok("1", $@"C:\x\p{i}.webp", "0"), 2_000_000, 500_000)),
        new JobItem("a.jpg", ConversionResult.Fail("1", ErrorCode.DecodeFailed, "")), new JobItem("b.jpg", ConversionResult.Fail("1", ErrorCode.DecodeFailed, ""))])));
    Toast("failure", ToastContent.From(FeedbackText.For(webp, [new("scan.tif", ConversionResult.Fail("1", ErrorCode.ImageTooLarge, ""))])));
    Toast("nothing", ToastContent.From(FeedbackText.For(OperationCatalog.Get("resize.box-1920x1080"), [new("small.png", ConversionResult.Fail("1", ErrorCode.NothingToDo, ""))])));
    Toast("progress", new ToastContent(FeedbackKind.Success, "Converting 48 images", "12 of 48", 0.25));

    var info = new ImageInfo("JPEG", 4032, 3024, 2_480_000, 72, 72, false, 6, true, true, false, true, false, false);
    var info2 = new ImageInfo("PNG", 1920, 1080, 845_000, 96, 96, true, 0, false, false, false, false, true, false);
    foreach (var (name, sections, more) in new[] { ("one", new[] { new InfoSection("IMG_2041.jpg", InfoRows.For(info)) }, 0),
        ("three", new[] { new InfoSection("IMG_2041.jpg", InfoRows.For(info)), new InfoSection("screenshot with a rather long file name.png", InfoRows.For(info2)) }, 3) })
    {
        var (w, h) = InfoPanelRenderer.WindowSize(sections, more, dpi);
        using (var p = surface.Begin(w, h, dpi)) InfoPanelRenderer.Draw(p, w, h, sections, more);
        surface.SavePng(Path.Combine(outDir, $"info-{name}-{dpi}.png"));
    }
}
Console.WriteLine($"Rendered to {outDir}");
return 0;

/// <summary>
/// The product icon, generated from the vector mark (<see cref="Icons.Mark"/>) so it is original and reproducible:
/// a dark rounded tile with the five-segment ring, one segment lit in the accent. Written as an .ico of PNG images.
/// </summary>
internal static class ProductIcon
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    public static void Write(string path)
    {
        var images = new List<byte[]>();
        using var surface = new Surface();
        var temp = Path.Combine(Path.GetTempPath(), $"wintools-icon-{Environment.ProcessId}.png");
        foreach (var size in Sizes)
        {
            using (var p = surface.Begin(size, size, 96))
            {
                var s = (float)size;
                var inset = size <= 24 ? 0.5f : s * 0.03f;
                var radius = s * 0.22f;
                p.FillRoundRect(inset, inset, s - 2 * inset, s - 2 * inset, radius, Palette.Base);
                if (size >= 32) p.StrokeRoundRect(inset + 0.5f, inset + 0.5f, s - 2 * inset - 1, s - 2 * inset - 1, radius, Palette.Edge, Math.Max(1, s / 64));
                Icons.Mark(p, s / 2, s / 2, s * (size <= 20 ? 0.42f : 0.36f), Palette.Green, Argb.Rgb(0x3D4D44));
            }
            surface.SavePng(temp);
            images.Add(File.ReadAllBytes(temp));
        }
        File.Delete(temp);
        using var o = new BinaryWriter(File.Create(path));
        o.Write((ushort)0); o.Write((ushort)1); o.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        for (var i = 0; i < images.Count; i++)
        {
            var size = Sizes[i];
            o.Write((byte)(size >= 256 ? 0 : size)); o.Write((byte)(size >= 256 ? 0 : size));
            o.Write((byte)0); o.Write((byte)0); o.Write((ushort)1); o.Write((ushort)32);
            o.Write(images[i].Length); o.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) o.Write(img);
        Console.WriteLine($"Icon written: {path}");
    }
}

/// <summary>Frame cost of the radial renderer (off screen): median and p95 of 300 frames per state.</summary>
internal static class Bench
{
    public static void Run(uint dpi)
    {
        var catalog = RadialMenu.Catalog(new AppSettings().CustomResizeId());
        var menu = RadialMenu.Build(new CapabilityResolver(catalog.Select(ImageCapabilities.Describe).ToArray()).Resolve([@"C:\images\photo.jpg"]));
        var nav = new MenuNavigator(menu, dpi);
        var renderer = new RadialRenderer();
        using var surface = new Surface();
        var size = RadialRenderer.WindowSize(dpi);
        var g = 0;
        var labels = new[] { MenuNavigator.BackLabel }.Concat(menu[g].Commands.Select(c => c.Label)).ToArray();
        var level = new MenuView(labels, MenuNavigator.LevelStart(g, menu.Count, menu[g].Commands.Count), 2, true, menu[g].Label,
            MenuNavigator.BackSweep(menu.Count, menu[g].Commands.Count), null, menu[g].Category);
        foreach (var (name, view) in new[] { ("top", nav.View(TimeSpan.Zero)), ("level-armed", level) })
        {
            var anim = new RingAnimation(false);
            anim.Start(view.Labels.Count, TimeSpan.Zero);
            anim.Target(view.Hot, view.Armed, TimeSpan.Zero);
            var times = new List<double>();
            for (var i = 0; i < 330; i++)
            {
                var t = System.Diagnostics.Stopwatch.GetTimestamp();
                using (var p = surface.Begin(size, size, dpi)) renderer.Draw(p, size, view, anim);
                if (i >= 30) times.Add(System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            }
            times.Sort();
            Console.WriteLine($"{name} @{dpi}: p50 {times[times.Count / 2]:0.00} ms, p95 {times[(int)(times.Count * 0.95)]:0.00} ms, max {times[^1]:0.00} ms");
        }
    }
}

/// <summary>Shows the real overlay for half a second (non-activating, no input) and reports how many frames its animation painted.</summary>
internal static class Live
{
    private sealed class NoEvents : IOverlayEvents
    {
        public bool DragEntered(IReadOnlyList<string> paths, bool pending, int dx, int dy) => false;
        public bool DragMoved(int dx, int dy) => false;
        public bool FilesResolved(IReadOnlyList<string> paths, int dx, int dy) => false;
        public void DropData(DropDataEvent report) { }
        public void DragLeft() { }
        public bool Dropped(IReadOnlyList<string> paths, int dx, int dy, bool copyAllowed) => false;
    }

    public static void Run()
    {
        var t = new Thread(() =>
        {
            WinTools.Windows.OleApartment.Initialize();
            using var overlay = new RadialOverlay(new NoEvents());
            var menu = RadialMenu.Build(CapabilityResolver.Default.Resolve([@"C:\images.jpg"]));
            overlay.ShowAt(300, 300);
            overlay.SetView(new MenuNavigator(menu, 96).View(TimeSpan.Zero));
            var until = DateTime.UtcNow.AddMilliseconds(500);
            while (DateTime.UtcNow < until) { Pump(); Thread.Sleep(1); }
            Console.WriteLine($"frames after 500 ms: {overlay.Frames}");
            overlay.Hide();
            WinTools.Windows.OleApartment.Uninitialize();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }

    private static void Pump()
    {
        while (PeekMessageW(out var m, 0, 0, 0, 1)) { TranslateMessage(m); DispatchMessageW(m); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int x, y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG m, nint h, uint a, uint b, uint remove);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool TranslateMessage(in MSG m);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint DispatchMessageW(in MSG m);
}

/// <summary>
/// Starts the settings window with a temporary settings file, captures it with PrintWindow and closes it. The window
/// appears on screen for about two seconds; no input is injected.
/// </summary>
internal static partial class SettingsShot
{
    public static int Run(string exe, string png)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"wintools-shot-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "settings.json");
        File.WriteAllText(file, new AppSettings { OutputPolicy = OutputPolicy.Folder, OutputFolder = @"D:\Pictures\Converted" }.ToJson());
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { "--settings", file } })!;
        nint hwnd = 0;
        for (var i = 0; i < 100 && hwnd == 0; i++) { Thread.Sleep(100); hwnd = FindWindowW(null, "WinTools Settings"); }
        if (hwnd == 0) { Console.Error.WriteLine("Settings window not found."); p.Kill(); return 1; }
        Thread.Sleep(1500);
        Capture(hwnd, png);
        // The lower half: scrolled to the end through UI Automation's scroll pattern (no input is injected).
        var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
        var scroller = root.FindFirst(System.Windows.Automation.TreeScope.Descendants,
            new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.IsScrollPatternAvailableProperty, true));
        if (scroller?.GetCurrentPattern(System.Windows.Automation.ScrollPattern.Pattern) is System.Windows.Automation.ScrollPattern scroll)
        {
            scroll.SetScrollPercent(System.Windows.Automation.ScrollPattern.NoScroll, 100);
            Thread.Sleep(600);
            Capture(hwnd, Path.ChangeExtension(png, null) + "-end.png");
        }
        PostMessageW(hwnd, 0x0010, 0, 0);
        p.WaitForExit(5000);
        Directory.Delete(dir, recursive: true);
        Console.WriteLine($"Saved {png}");
        return 0;
    }

    private static void Capture(nint hwnd, string png)
    {
        GetWindowRect(hwnd, out var r);
        using var surface = new Surface();
        using (surface.Begin(r.Right - r.Left, r.Bottom - r.Top, 96)) { }
        PrintWindow(hwnd, surface.Dc, 2 /* PW_RENDERFULLCONTENT */);
        surface.MakeOpaque(0, 0, surface.Width, surface.Height);
        surface.SavePng(png);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.LibraryImport("user32.dll", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)] private static partial nint FindWindowW(string? c, string t);
    [System.Runtime.InteropServices.LibraryImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] private static partial bool GetWindowRect(nint h, out RECT r);
    [System.Runtime.InteropServices.LibraryImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] private static partial bool PrintWindow(nint h, nint dc, uint flags);
    [System.Runtime.InteropServices.LibraryImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] private static partial bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [System.Runtime.InteropServices.LibraryImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] private static partial bool PostMessageW(nint h, uint m, nuint w, nint l);
}
