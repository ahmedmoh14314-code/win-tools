using WinTools.Core.Capabilities;
using WinTools.Core.Interaction;
using WinTools.Core.Settings;

namespace WinTools.Core.Tests;

public sealed class RadialMenuTests
{
    private static IReadOnlyList<MenuGroup> MenuFor(params string[] paths) =>
        RadialMenu.Build(new CapabilityResolver(RadialMenu.Catalog(new AppSettings().CustomResizeId())).Resolve(paths));

    [Fact]
    public void Jpeg_gets_all_five_categories_in_fixed_order()
    {
        var menu = MenuFor(@"C:\p\photo.jpg");
        Assert.Equal(["Convert", "Compress", "Resize", "Rotate", "Tools"], menu.Select(g => g.Label));
        Assert.Equal(["To PNG", "To WebP", "To BMP", "To TIFF"], menu[0].Commands.Select(c => c.Label));
        Assert.Equal(["High quality", "Balanced", "Smallest"], menu[1].Commands.Select(c => c.Label));
        Assert.Equal(["Remove metadata", "Image info"], menu[4].Commands.Select(c => c.Label));
    }

    [Fact]
    public void Exact_resize_from_the_settings_is_last_in_resize_and_never_duplicates_a_preset()
    {
        var resize = MenuFor(@"C:\p\photo.png").Single(g => g.Category == MenuCategory.Resize).Commands.Select(c => c.Id).ToArray();
        Assert.Equal("resize.box-1600x1600", resize[^1]);
        Assert.Equal(7, resize.Length);

        var preset = RadialMenu.Catalog(new AppSettings { ResizeWidth = 1920, ResizeHeight = 1080 }.CustomResizeId());
        Assert.Equal(OperationCatalog.All.Count, preset.Count);
        Assert.Same(OperationCatalog.All, RadialMenu.Catalog(null));
    }

    [Fact]
    public void Categories_without_commands_are_left_out()
    {
        var bmp = MenuFor(@"C:\p\scan.bmp");
        Assert.Equal(["Convert", "Resize", "Rotate", "Tools"], bmp.Select(g => g.Label)); // no compression, no metadata
        Assert.Equal(["Image info"], bmp[^1].Commands.Select(c => c.Label));
        Assert.Equal(["Lossless"], MenuFor(@"C:\p\a.png").Single(g => g.Category == MenuCategory.Compress).Commands.Select(c => c.Label));
    }

    [Fact]
    public void A_mixed_batch_gets_the_intersection()
    {
        var menu = MenuFor(@"C:\p\a.jpg", @"C:\p\b.png");
        Assert.DoesNotContain(menu, g => g.Category == MenuCategory.Compress); // JPEG levels vs PNG lossless: nothing common
        Assert.Equal(["To WebP", "To BMP", "To TIFF"], menu[0].Commands.Select(c => c.Label));
    }

    [Fact]
    public void Unsupported_input_gets_an_empty_menu()
    {
        Assert.Empty(MenuFor(@"C:\p\notes.txt"));
        Assert.Empty(MenuFor());
    }

    [Fact]
    public void A_new_operation_appears_without_any_menu_change()
    {
        var mirror = new Operation("rotate.mirror", "Mirror", OperationKind.Rotate);
        var menu = RadialMenu.Build(new CapabilityResolver([.. OperationCatalog.All, mirror]).Resolve([@"C:\p\photo.webp"]));
        Assert.Equal(["Left", "Right", "180°", "Mirror"], menu.Single(g => g.Category == MenuCategory.Rotate).Commands.Select(c => c.Label));
    }

    [Theory]
    [InlineData(OperationKind.Convert, MenuCategory.Convert)]
    [InlineData(OperationKind.Compress, MenuCategory.Compress)]
    [InlineData(OperationKind.Resize, MenuCategory.Resize)]
    [InlineData(OperationKind.Rotate, MenuCategory.Rotate)]
    [InlineData(OperationKind.StripMetadata, MenuCategory.Tools)]
    [InlineData(OperationKind.Info, MenuCategory.Tools)]
    public void Each_operation_kind_has_its_category(OperationKind kind, MenuCategory expected)
    {
        Assert.Equal(expected, RadialMenu.CategoryOf(kind));
    }

    [Theory]
    [InlineData(1920, 1080, false, true)]  // the same size as a preset, but allowed to enlarge
    [InlineData(1280, 720, true, false)]   // the same size as a preset, but stretched
    public void Exact_size_that_differs_from_a_preset_only_in_behaviour_gets_a_distinct_label(int w, int h, bool noUpscaleOff, bool keepAspect)
    {
        var settings = new AppSettings { ResizeWidth = w, ResizeHeight = h, NoUpscale = !noUpscaleOff, KeepAspectRatio = keepAspect };
        var resize = RadialMenu.Build(new CapabilityResolver(RadialMenu.Catalog(settings.CustomResizeId())).Resolve([@"C:\p\a.png"]))
            .Single(g => g.Category == MenuCategory.Resize).Commands;
        Assert.Equal(resize.Count, resize.Select(c => c.Label).Distinct().Count());
    }
}

public sealed class MenuNavigatorTests
{
    private static readonly TimeSpan Late = MenuNavigator.ArmDelay + TimeSpan.FromMilliseconds(1);
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static IReadOnlyList<MenuGroup> MenuFor(params string[] paths) =>
        RadialMenu.Build(new CapabilityResolver(RadialMenu.Catalog(new AppSettings().CustomResizeId())).Resolve(paths));

    private static (int, int) At(double angle, double radius96, uint dpi = 96)
    {
        var (dx, dy) = RingGeometry.PointAt(angle, radius96 * dpi / 96.0);
        return ((int)Math.Round(dx), (int)Math.Round(dy));
    }

    /// <summary>Opens category g the way a hand does: a stroke out of the hub, then the ring re-centres on the pointer.</summary>
    private static void OpenCategory(MenuNavigator n, int g, TimeSpan t, uint dpi = 96)
    {
        var (x, y) = At(RingGeometry.SectorCentre(g, n.Groups.Count), RingGeometry.OpenRadius96 + 10, dpi);
        n.Move(0, 0, t);
        Assert.True(n.Move(x, y, t));
        n.Shown(0, 0, t, dpi);
    }

    private static (int, int) Command(MenuNavigator n, int g, int c, double radius96 = 69, uint dpi = 96)
    {
        var count = n.Groups[g].Commands.Count;
        return At(RingGeometry.SectorCentre(c + 1, count + 1, MenuNavigator.LevelStart(g, n.Groups.Count, count)), radius96, dpi);
    }

    private static void Move(MenuNavigator n, (int X, int Y) p, TimeSpan t) => n.Move(p.X, p.Y, t);

    [Fact]
    public void The_top_level_shows_categories_and_arms_nothing()
    {
        var n = new MenuNavigator(MenuFor(@"C:\p\photo.jpg"), 96);
        n.Move(0, 0, Ms(0));
        Move(n, At(RingGeometry.SectorCentre(0, 5), 60), Ms(500));
        Assert.Null(n.Open);                       // a graze into a category does not open it
        Assert.Null(n.Armed(TimeSpan.FromSeconds(5)));
        Assert.Equal(["Convert", "Compress", "Resize", "Rotate", "Tools"], n.View(Ms(0)).Labels);
        Assert.Equal(0, n.View(Ms(500)).Hot);
    }

    [Fact]
    public void A_deep_stroke_opens_the_category_and_the_owner_recentres()
    {
        var n = new MenuNavigator(MenuFor(@"C:\p\photo.jpg"), 96);
        OpenCategory(n, 0, Ms(100));
        Assert.Equal(MenuCategory.Convert, n.Open!.Category);
        var view = n.View(Ms(100));
        Assert.Equal(["Back", "To PNG", "To WebP", "To BMP", "To TIFF"], view.Labels);
        Assert.Equal("Convert", view.Hub);
        // Back points the way the stroke came (opposite to Convert).
        Assert.Equal(RingGeometry.Normalize(RingGeometry.SectorCentre(0, 5) + Math.PI), RingGeometry.SectorCentre(0, 5, view.Start), 9);
    }

    [Fact]
    public void A_command_runs_only_after_the_ring_has_been_visible_for_the_arm_delay()
    {
        var n = new MenuNavigator(MenuFor(@"C:\p\photo.jpg"), 96);
        OpenCategory(n, 0, Ms(100));
        Move(n, Command(n, 0, 1), Ms(150));
        Assert.Equal("convert.to-webp", n.Hot!.Id);
        Assert.Null(n.Armed(Ms(100 + 149)));
        Assert.Equal("convert.to-webp", n.Armed(Ms(100) + Late)!.Id);
        Assert.True(n.View(Ms(100) + Late).Armed);
        Assert.False(n.View(Ms(200)).Armed);
    }

    [Fact]
    public void Back_returns_to_the_categories_and_nothing_is_armed_there()
    {
        var n = new MenuNavigator(MenuFor(@"C:\p\photo.jpg"), 96);
        OpenCategory(n, 2, Ms(0));
        var view = n.View(Ms(0));
        var back = RingGeometry.SectorCentre(0, view.Labels.Count, view.Start);
        Move(n, At(back, 50), Ms(300));
        Assert.Null(n.Armed(Ms(400)));               // on Back, not deep: nothing runs, still in Resize
        Assert.Equal(MenuCategory.Resize, n.Open!.Category);
        var (bx, by) = At(back, 90);
        Assert.True(n.Move(bx, by, Ms(350)));
        n.Shown(0, 0, Ms(350), 96);
        Assert.Null(n.Open);
        OpenCategory(n, 3, Ms(500));                   // and another category opens from there
        Assert.Equal(MenuCategory.Rotate, n.Open!.Category);
    }

    [Fact]
    public void Dead_zone_and_outside_the_ring_run_nothing()
    {
        var n = new MenuNavigator(MenuFor(@"C:\p\photo.jpg"), 96);
        OpenCategory(n, 0, Ms(0));
        n.Move(3, 4, Ms(500));
        Assert.Null(n.Armed(Ms(500)));
        Move(n, At(1.0, 130), Ms(600));
        Assert.Null(n.Armed(Ms(600)));
    }

    [Fact]
    public void Near_a_screen_edge_the_pushed_ring_reacts_only_after_the_pointer_reaches_its_centre()
    {
        // The ring was clamped to the work area, so the pointer starts 90 px left of its centre (evaluator finding M2).
        var n = new MenuNavigator(MenuFor(@"C:\p\photo.jpg"), 96);
        n.Shown(-90, 0, Ms(0), 96);
        n.Move(-90, 0, Ms(10));
        Assert.False(n.Move(-95, 0, Ms(20)));          // deeper toward the edge: still nothing
        Assert.Null(n.Open);
        Assert.Equal(-1, n.View(Ms(20)).Hot);
        n.Move(0, 0, Ms(300));                          // to the centre: now the ring works
        Assert.True(n.Move(-95, 0, Ms(400)));

        // The same holds inside a category whose ring was pushed aside.
        n.Shown(70, 0, Ms(400), 96);
        var (cx, cy) = Command(n, n.Groups.ToList().IndexOf(n.Open!), 0);
        n.Move(cx + 70, cy, Ms(800));
        Assert.Null(n.Armed(Ms(900)));
    }

    /// <summary>
    /// Every command of every category, for every format and some mixed batches, at several DPIs, stroke directions
    /// and target positions: the straight stroke out of the hub always arms exactly the command aimed at (evaluator M1).
    /// </summary>
    [Theory]
    [InlineData(96u)]
    [InlineData(120u)]
    [InlineData(144u)]
    [InlineData(192u)]
    public void Every_straight_stroke_arms_exactly_the_command_aimed_at(uint dpi)
    {
        string[][] inputs = [[@"C:\p\a.jpg"], [@"C:\p\a.png"], [@"C:\p\a.webp"], [@"C:\p\a.bmp"], [@"C:\p\a.tif"], [@"C:\p\a.jpg", @"C:\p\b.png"], [@"C:\p\a.bmp", @"C:\p\b.tif", @"C:\p\c.webp"]];
        var checkedStrokes = 0;
        foreach (var paths in inputs)
        {
            var menu = MenuFor(paths);
            for (var g = 0; g < menu.Count; g++)
                for (var c = 0; c < menu[g].Commands.Count; c++)
                    foreach (var catSkew in new[] { -0.4, 0.0, 0.4 })          // fraction of half a category sector
                        foreach (var cmdSkew in new[] { -0.7, 0.0, 0.7 })      // fraction of half a command sector
                            foreach (var radius in new[] { 40.0, 69.0, 105.0 })
                            {
                                var n = new MenuNavigator(menu, dpi);
                                n.Move(0, 0, Ms(0));
                                var catAngle = RingGeometry.SectorCentre(g, menu.Count) + catSkew * Math.PI / menu.Count;
                                // the stroke out of the hub, sampled like DragOver
                                var opened = false;
                                for (var r = 30.0; r <= RingGeometry.OuterRadius96 && !opened; r += 6)
                                {
                                    var (x, y) = At(catAngle, r, dpi);
                                    opened = n.Move(x, y, Ms(r));
                                }
                                Assert.True(opened);
                                n.Shown(0, 0, Ms(120), dpi);
                                var count = menu[g].Commands.Count;
                                var w = 2 * Math.PI / (count + 1);
                                var target = RingGeometry.SectorCentre(c + 1, count + 1, MenuNavigator.LevelStart(g, menu.Count, count)) + cmdSkew * w / 2;
                                for (var r = 4.0; r <= radius; r += 6)
                                {
                                    var (x, y) = At(target, r, dpi);
                                    Assert.False(n.Move(x, y, Ms(130 + r)));   // a stroke to a command never changes level
                                }
                                Assert.Equal(menu[g].Commands[c].Id, n.Armed(Ms(120) + Late)?.Id);
                                checkedStrokes++;
                            }
        }
        Assert.True(checkedStrokes > 1000, $"{checkedStrokes} strokes");
    }

    [Fact]
    public void Equal_states_give_equal_views_so_the_overlay_repaints_only_on_change()
    {
        var n = new MenuNavigator(MenuFor(@"C:\p\photo.jpg"), 96);
        OpenCategory(n, 0, Ms(0));
        Move(n, Command(n, 0, 0), Ms(10));
        Assert.Equal(n.View(Ms(20)), n.View(Ms(30)));
        Assert.NotEqual(n.View(Ms(20)), n.View(Late)); // arming changes the view
    }

    [Fact]
    public void Dpi_of_the_monitor_the_ring_moved_to_is_used()
    {
        var n = new MenuNavigator(MenuFor(@"C:\p\photo.png"), 96);
        OpenCategory(n, 0, Ms(0));
        n.Shown(0, 0, Ms(0), 192);                        // re-centred onto a 200 % monitor
        Move(n, Command(n, 0, 0, 69, 96), Ms(10));        // 69 px: in the 192-DPI ring (56..220 px)
        Assert.Equal("convert.to-jpeg", n.Hot?.Id);
        Move(n, Command(n, 0, 0, 40, 96), Ms(20));        // 40 px: inside the 192-DPI dead zone (56 px), though outside 96 DPI's
        Assert.Null(n.Hot);
    }
}
