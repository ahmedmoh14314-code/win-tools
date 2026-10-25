using System.Runtime.InteropServices;
using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Interaction;
using WinTools.Core.Settings;
using WinTools.Windows.Feedback;
using WinTools.Windows.Overlay;
using WinTools.Windows.Rendering;
using WinTools.Windows.Settings;

namespace WinTools.Worker.Tests;

/// <summary>
/// The native UI surfaces, rendered off screen with the real fonts and renderer (no input, no desktop interaction):
/// the overlay's drop-target invariant, label fitting, contrast, and the toast's no-focus window styles.
/// </summary>
public sealed partial class UiSurfaceTests
{
    public static TheoryData<uint> Dpis => [96, 120, 144, 192];

    private static IReadOnlyList<MenuGroup> MenuFor(params string[] names)
    {
        var resolver = CapabilityResolver.For(new AppSettings().CustomResizeId());
        return RadialMenu.Build(resolver, names.Select(n => $@"C:\images\{n}").ToArray());
    }

    /// <summary>Every ring the menu can show, for single files of each format and mixed selections.</summary>
    private static IEnumerable<MenuView> AllViews()
    {
        // With General Files (V2 PR 1.4): a file no module reads (one category) and a folder.
        foreach (var names in new[] { new[] { "a.png" }, ["a.jpg"], ["a.webp"], ["a.bmp"], ["a.tif"], ["a.png", "b.jpg"], ["a.png", "b.jpg", "c.webp"], ["notes.txt"], ["holiday\\"] })
        {
            var menu = MenuFor(names);
            var nav = new MenuNavigator(menu, 96);
            yield return nav.View(TimeSpan.Zero);
            for (var g = 0; g < menu.Count; g++)
            {
                var count = menu[g].Commands.Count;
                var labels = new[] { MenuNavigator.BackLabel }.Concat(menu[g].Commands.Select(c => c.Label)).ToArray();
                yield return new MenuView(labels, MenuNavigator.LevelStart(g, menu.Count, count), 1, true, menu[g].Label,
                    MenuNavigator.BackSweep(menu.Count, count), null, menu[g].Category.Icon);
            }
        }
        yield return new MenuView([], 0, -1, false, "");
        yield return RadialRenderer.Waiting;
    }

    [Theory]
    [MemberData(nameof(Dpis))]
    public void Every_label_fits_its_box_without_an_ellipsis(uint dpi)
    {
        var renderer = new RadialRenderer();
        var px = (int)Math.Round(RingLabelMetrics.LabelText96 * dpi / 96);
        foreach (var view in AllViews().Where(v => v.Labels.Count > 0))
        {
            var top = view.Open is null;
            var boxes = renderer.Layout(view.Labels.Count, view.Start, view.FirstSweep, dpi, withIcons: top);
            for (var i = 0; i < view.Labels.Count; i++)
            {
                if (!top && i == 0) continue; // Back: icon and a short fixed word
                var fit = Fonts.Fit(view.Labels[i], (int)(2 * boxes[i].HalfWidth), px, RadialRenderer.LabelWeight, twoLines: !top);
                Assert.False(fit.Ellipsis, $"\"{view.Labels[i]}\" does not fit at {dpi} DPI (box {2 * boxes[i].HalfWidth:0} px)");
                Assert.True(fit.SizePx >= Math.Round(11.0 * dpi / 96), $"\"{view.Labels[i]}\" shrank below 11 px at 96 DPI ({fit.SizePx} px at {dpi})");
                if (top)
                {
                    // The categories' boxes may reach into the gap before the hub; the drawn text line must not.
                    var s = dpi / 96.0;
                    var line = RingLabelMetrics.LineHeight96 * s;
                    var width = Fonts.Width(fit.Lines[0], fit.SizePx, RadialRenderer.LabelWeight);
                    var b = boxes[i];
                    var text = new LabelBox(b.X, b.Y + b.HalfHeight - (line + 2 * s) / 2, width / 2.0, (line + 2 * s) / 2);
                    Assert.True(text.Reach().Near >= RingLabelMetrics.BandInner96 * s, $"\"{view.Labels[i]}\" reaches {text.Reach().Near:0.0} px from the centre at {dpi} DPI, inside the hub gap");
                }
            }
        }
    }

    /// <summary>
    /// ADR-0001: every pixel of the disc must reach OLE as part of the window (alpha 0 is click-through for a layered
    /// window), in every state and frame, including the first frame of an appearing ring; the corners outside it must not.
    /// </summary>
    [Theory]
    [MemberData(nameof(Dpis))]
    public void The_whole_disc_is_opaque_in_every_frame_and_the_corners_are_clear(uint dpi)
    {
        var renderer = new RadialRenderer();
        using var surface = new Surface();
        var size = RadialRenderer.WindowSize(dpi);
        var c = size / 2.0;
        foreach (var view in AllViews())
            foreach (var settled in new[] { false, true })
            {
                var anim = new RingAnimation(reducedMotion: false);
                anim.Start(view.Labels.Count, TimeSpan.Zero);
                anim.Target(view.Hot, view.Armed, TimeSpan.Zero);
                if (settled) anim.Advance(TimeSpan.FromSeconds(1));
                using (var p = surface.Begin(size, size, dpi)) renderer.Draw(p, size, view, anim);
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++)
                    {
                        var d = Math.Sqrt((x + 0.5 - c) * (x + 0.5 - c) + (y + 0.5 - c) * (y + 0.5 - c));
                        if (d < c - 1.5) Assert.True(surface.AlphaAt(x, y) == 255, $"{dpi} DPI, {string.Join("/", view.Labels)}: alpha {surface.AlphaAt(x, y)} at ({x},{y}), {d:0.0} px from the centre");
                        else if (d > c + 1) Assert.True(surface.AlphaAt(x, y) == 0, $"corner pixel ({x},{y}) is not clear");
                    }
            }
    }

    [Fact]
    public void Text_and_state_colours_have_enough_contrast()
    {
        // WCAG: 4.5 for normal text, 3 for large or bold text and for non-text indicators.
        Assert.True(Argb.Contrast(Palette.Text, Palette.Surface) >= 7);
        Assert.True(Argb.Contrast(Palette.Text, Palette.Raised) >= 7);
        Assert.True(Argb.Contrast(Palette.TextSecondary, Palette.Surface) >= 4.5);
        Assert.True(Argb.Contrast(Palette.TextSecondary, Palette.Sunken) >= 4.5);
        Assert.True(Argb.Contrast(Palette.TextOnGreen, Palette.GreenArmed) >= 7);
        Assert.True(Argb.Contrast(Palette.Warning, Palette.Surface) >= 4.5);
        Assert.True(Argb.Contrast(Palette.Error, Palette.Surface) >= 4.5);
        Assert.True(Argb.Contrast(Palette.GreenHover, Palette.Raised) >= 3, "the hover lip against the hovered sector");
        Assert.True(Argb.Contrast(Palette.GreenArmed, Palette.Surface) >= 3, "an armed sector against its neighbours");
    }

    [Fact]
    public void Toasts_and_the_info_panel_render_with_long_text()
    {
        using var surface = new Surface();
        foreach (var dpi in new uint[] { 96, 144, 192 })
        {
            var (w, h) = ToastRenderer.WindowSize(dpi);
            var long1 = new string('x', 300);
            foreach (var t in new[] { new ToastContent(FeedbackKind.Failure, long1, long1), new ToastContent(FeedbackKind.Success, "a", "b", 0.5) })
                using (var p = surface.Begin(w, h, dpi)) ToastRenderer.Draw(p, w, h, t);
            var info = new WinTools.Core.Jobs.ImageInfo("JPEG", 4000, 3000, 2_000_000, 72, 72, false, 6, true, true, true, true, true, true, true);
            var sections = new[] { new InfoSection(long1, InfoRows.For(info)), new InfoSection("b.png", InfoRows.For(info)) };
            var (iw, ih) = InfoPanelRenderer.WindowSize(sections, 5, dpi);
            using (var p = surface.Begin(iw, ih, dpi)) InfoPanelRenderer.Draw(p, iw, ih, sections, 5);
            Assert.Equal(255, surface.AlphaAt(iw / 2, ih / 2)); // the card is opaque under its text
        }
    }

    [Fact]
    public void The_toast_never_takes_focus_or_input()
    {
        RunSta(() =>
        {
            var before = GetForegroundWindow();
            using var toast = new ToastWindow();
            toast.Show(new ToastContent(FeedbackKind.Success, "photo.webp created", "Converted to WebP"), 100, 100);
            var hwnd = FindWindowW(ToastWindow.ClassName, null);
            Assert.NotEqual(0, hwnd);
            var ex = (uint)GetWindowLongPtrW(hwnd, -20 /* GWL_EXSTYLE */);
            const uint NoActivate = 0x08000000, Transparent = 0x20, Layered = 0x80000, Topmost = 0x8, ToolWindow = 0x80;
            Assert.Equal(NoActivate | Transparent | Layered | Topmost | ToolWindow, ex & (NoActivate | Transparent | Layered | Topmost | ToolWindow));
            Assert.True(toast.IsVisible);
            Assert.Equal(before, GetForegroundWindow());
            toast.Hide();
            Assert.False(toast.IsVisible);
        });
    }

    [Fact]
    public void The_alt_refusal_never_replaces_a_running_batch_progress_toast()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"wintools-refusal-{Guid.NewGuid():N}.jsonl");
        try
        {
            RunSta(() =>
            {
                using var ui = new WinTools.Windows.MessageWindow((_, _, _) => false);
                using var toast = new ToastWindow();
                using var info = new InfoPanel();
                using var sounds = new SoundPlayer(enabled: false);
                using var log = WinTools.Host.HostLog.Open(logPath);
                var presenter = new WinTools.Host.FeedbackPresenter(ui, toast, info, sounds, log);

                var progress = new ToastContent(FeedbackKind.Success, "Converting 40 images", "3 of 40", 0.075);
                toast.Show(progress, 100, 100);
                presenter.TriggerRefused("Space", 100, 100);
                Assert.Same(progress, toast.Content); // still the batch's toast, so its progress updates keep landing

                toast.Hide();
                presenter.TriggerRefused("F9", 100, 100);
                Assert.Equal("Release Alt, then press F9", toast.Content?.Title);
                Assert.DoesNotContain("F9", toast.Content?.Detail, StringComparison.Ordinal); // no claim about a specific Windows shortcut
                toast.Hide();
            });
        }
        finally { File.Delete(logPath); }
    }

    [Fact]
    public void The_info_panel_never_takes_focus_and_closes_on_wm_close()
    {
        RunSta(() =>
        {
            var before = GetForegroundWindow();
            using var panel = new InfoPanel();
            var info = new WinTools.Core.Jobs.ImageInfo("PNG", 64, 48, 900, 0, 0, true, 0, false, false, false, false, false, false);
            panel.Show([new InfoSection("a.png", InfoRows.For(info))], 0, 200, 200);
            var hwnd = FindWindowW(InfoPanel.ClassName, null);
            var ex = (uint)GetWindowLongPtrW(hwnd, -20);
            Assert.Equal(0x08000000u, ex & 0x08000000u); // WS_EX_NOACTIVATE
            Assert.True(panel.IsVisible);
            Assert.Equal(before, GetForegroundWindow());
            SendMessageW(hwnd, 0x0010 /* WM_CLOSE */, 0, 0);
            Assert.False(panel.IsVisible);
        });
    }

    [Fact]
    public void Sounds_off_never_starts_the_audio_thread()
    {
        using var off = new SoundPlayer(enabled: false);
        off.Warm();
        foreach (var s in Enum.GetValues<UiSound>()) off.Play(s);
        Assert.Equal(0, off.Requested);
    }

    [Fact]
    public void Startup_registration_recognises_this_copy_another_copy_and_none()
    {
        var host = @"C:\Apps\WinTools\WinTools.Host.exe";
        // Signing in shows the tray icon, not the window (app shell): the stored command adds --background; a value an older
        // version wrote (the path alone) still counts as on.
        Assert.Equal("\"C:\\Apps\\WinTools\\WinTools.Host.exe\" --background", StartupRegistration.CommandFor(host));
        Assert.Equal(StartupState.On, StartupRegistration.State(host, "\"C:\\Apps\\WinTools\\WinTools.Host.exe\" --background"));
        Assert.Equal(StartupState.Off, StartupRegistration.State(host, null));
        Assert.Equal(StartupState.Off, StartupRegistration.State(host, " "));
        Assert.Equal(StartupState.On, StartupRegistration.State(host, "\"c:\\apps\\wintools\\WinTools.Host.exe\""));
        Assert.Equal(StartupState.OtherCopy, StartupRegistration.State(host, "\"D:\\Old\\WinTools.Host.exe\""));
    }

    [Fact]
    public void A_saved_change_reaches_a_listener_through_the_signal()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(Path.Combine(folder.Path, "settings.json"));
        using var got = new ManualResetEventSlim();
        var name = $@"Local\WinTools.Tests.{Guid.NewGuid():N}"; // never the real host's event
        using (SettingsSignal.TryListen(got.Set, name))
        {
            Assert.True(store.Save(new AppSettings { UiSounds = false, TriggerKey = "F9" }));
            Assert.True(SettingsSignal.Raise(name));
            Assert.True(got.Wait(TimeSpan.FromSeconds(5)));
        }
        var loaded = store.Load();
        Assert.Equal((false, "F9"), (loaded.UiSounds, loaded.TriggerKey));
    }

    [Fact]
    public void The_host_never_loads_wpf()
    {
        var host = typeof(WinTools.Host.Jobs.WorkerClient).Assembly;
        var names = host.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain("PresentationFramework", names);
        Assert.DoesNotContain("PresentationCore", names);
        Assert.DoesNotContain("WindowsBase", names);
        Assert.DoesNotContain("WinTools.Settings", names);
        var config = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "WinTools.Host.runtimeconfig.json"));
        Assert.DoesNotContain("WindowsDesktop", config, StringComparison.Ordinal);
    }

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint FindWindowW(string? cls, string? title);
    [LibraryImport("user32.dll")] private static partial nint GetWindowLongPtrW(nint hwnd, int index);
    [LibraryImport("user32.dll")] private static partial nint SendMessageW(nint hwnd, uint msg, nuint w, nint l);
}
