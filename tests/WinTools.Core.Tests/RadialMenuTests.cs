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

    [Fact]
    public void Every_catalog_kind_has_a_category()
    {
        foreach (var kind in Enum.GetValues<OperationKind>())
            Assert.Contains(RadialMenu.CategoryOf(kind), Enum.GetValues<MenuCategory>());
        Assert.Equal(MenuCategory.Tools, RadialMenu.CategoryOf(OperationKind.StripMetadata));
        Assert.Equal(MenuCategory.Tools, RadialMenu.CategoryOf(OperationKind.Info));
    }
}

public sealed class CommandArcTests
{
    [Fact]
    public void Arc_is_centred_on_its_category()
    {
        var (start, sweep) = RingGeometry.CommandArc(group: 1, groupCount: 4, commandCount: 3); // category 90°..180°
        Assert.Equal(Math.PI / 4, sweep, 9);
        Assert.Equal(3 * Math.PI / 4, RingGeometry.CommandCentre(start, sweep, 1), 9); // the middle command points the same way
    }

    [Fact]
    public void Many_commands_share_the_full_circle_without_a_gap()
    {
        var (start, sweep) = RingGeometry.CommandArc(0, 5, 10);
        Assert.Equal(2 * Math.PI / 10, sweep, 9);
        for (var deg = 0; deg < 360; deg += 7)
        {
            var (dx, dy) = RingGeometry.PointAt(deg * Math.PI / 180, 150);
            Assert.InRange(RingGeometry.HitCommand((int)dx, (int)dy, 110, 190, start, sweep, 10), 0, 9);
        }
    }

    [Theory]
    [InlineData(60, -1)]    // inside the category ring
    [InlineData(150, 0)]    // in the band
    [InlineData(200, -1)]   // beyond the band
    public void Only_the_band_hits_a_command(double radius, int expected)
    {
        var (start, sweep) = RingGeometry.CommandArc(0, 4, 1);
        var (dx, dy) = RingGeometry.PointAt(RingGeometry.CommandCentre(start, sweep, 0), radius);
        Assert.Equal(expected, RingGeometry.HitCommand((int)dx, (int)dy, 110, 190, start, sweep, 1));
    }

    [Fact]
    public void Arc_wrapping_past_twelve_oclock_is_hit_on_both_sides()
    {
        var (start, sweep) = RingGeometry.CommandArc(0, 5, 3); // category at 36°, arc 36° ± 67.5°: crosses 0
        Assert.True(start < 0);
        var (lx, ly) = RingGeometry.PointAt(RingGeometry.CommandCentre(start, sweep, 0), 150);
        var (rx, ry) = RingGeometry.PointAt(RingGeometry.CommandCentre(start, sweep, 2), 150);
        Assert.Equal(0, RingGeometry.HitCommand((int)lx, (int)ly, 110, 190, start, sweep, 3));
        Assert.Equal(2, RingGeometry.HitCommand((int)rx, (int)ry, 110, 190, start, sweep, 3));
        var (gx, gy) = RingGeometry.PointAt(Math.PI, 150); // opposite side: the gap
        Assert.Equal(-1, RingGeometry.HitCommand((int)gx, (int)gy, 110, 190, start, sweep, 3));
    }
}

public sealed class MenuNavigatorTests
{
    private static readonly TimeSpan Late = MenuNavigator.ArmDelay + TimeSpan.FromMilliseconds(1);
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static MenuNavigator Navigator() =>
        new(RadialMenu.Build(CapabilityResolver.Default.Resolve([@"C:\p\photo.jpg"])), dpi: 96);

    /// <summary>Offset of the middle of category g.</summary>
    private static (int, int) Category(MenuNavigator n, int g)
    {
        var (dx, dy) = RingGeometry.PointAt(RingGeometry.SectorCentre(g, n.Groups.Count), (RingGeometry.InnerRadius96 + RingGeometry.OuterRadius96) / 2.0);
        return ((int)Math.Round(dx), (int)Math.Round(dy));
    }

    /// <summary>Offset of the middle of command c of category g.</summary>
    private static (int, int) Command(MenuNavigator n, int g, int c)
    {
        var (start, sweep) = RingGeometry.CommandArc(g, n.Groups.Count, n.Groups[g].Commands.Count);
        var (dx, dy) = RingGeometry.PointAt(RingGeometry.CommandCentre(start, sweep, c), (RingGeometry.OuterRadius96 + RingGeometry.CommandRadius96) / 2.0);
        return ((int)Math.Round(dx), (int)Math.Round(dy));
    }

    private static void Move(MenuNavigator n, (int X, int Y) p, TimeSpan t) => n.Move(p.X, p.Y, t);

    [Fact]
    public void Nothing_is_active_or_armed_at_the_start()
    {
        var n = Navigator();
        n.Move(0, 0, TimeSpan.Zero);
        Assert.Null(n.Active);
        Assert.Null(n.Armed(TimeSpan.FromSeconds(5)));
        Assert.Equal(-1, n.View(TimeSpan.Zero).ActiveGroup);
    }

    [Fact]
    public void Category_then_outward_to_a_command_arms_it_after_the_delay()
    {
        var n = Navigator();
        Move(n, Category(n, 0), Ms(100));
        Assert.Equal(MenuCategory.Convert, n.Active!.Category);
        Move(n, Command(n, 0, 1), Ms(180));
        Assert.Equal("convert.to-webp", n.Hot!.Id);
        Assert.Null(n.Armed(Ms(100 + 149)));                 // visible for 149 ms: not yet
        Assert.Equal("convert.to-webp", n.Armed(Ms(100) + Late)!.Id);
        Assert.True(n.View(Ms(100) + Late).Armed);
    }

    [Fact]
    public void Drop_on_a_category_or_the_dead_zone_runs_nothing()
    {
        var n = Navigator();
        Move(n, Category(n, 2), Ms(10));
        Assert.Null(n.Armed(TimeSpan.FromSeconds(3)));
        n.Move(0, 0, TimeSpan.FromSeconds(3));
        Assert.Null(n.Armed(TimeSpan.FromSeconds(4)));
        Assert.Equal(MenuCategory.Resize, n.Active!.Category); // the arc stays open while passing the centre
    }

    [Fact]
    public void Jumping_straight_into_the_band_without_a_category_selects_nothing()
    {
        var n = Navigator();
        Move(n, Command(n, 0, 0), Ms(500));
        Assert.Null(n.Hot);
        Assert.Null(n.Armed(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Switching_category_restarts_the_delay_and_hides_the_old_commands()
    {
        var n = Navigator();
        Move(n, Category(n, 0), Ms(0));
        // Compress lies inside Convert's arc, so it is opened the deliberate way: near the hub.
        var (hx, hy) = RingGeometry.PointAt(RingGeometry.SectorCentre(1, 5), RingGeometry.InnerRadius96 + 6);
        n.Move((int)hx, (int)hy, Ms(400));
        Assert.Equal(MenuCategory.Compress, n.Active!.Category);
        Move(n, Command(n, 1, 0), Ms(450));
        Assert.Equal("compress.high", n.Hot!.Id);
        Assert.Null(n.Armed(Ms(500)));                       // compress opened 100 ms ago
        Assert.NotNull(n.Armed(Ms(400) + Late));
    }

    [Fact]
    public void Moving_between_commands_of_the_open_category_keeps_them_armed()
    {
        var n = Navigator();
        Move(n, Category(n, 3), Ms(0));
        Move(n, Command(n, 3, 0), Ms(300));
        Move(n, Command(n, 3, 2), Ms(320));
        Assert.Equal("rotate.180", n.Armed(Ms(320))!.Id);
    }

    [Fact]
    public void Back_in_the_category_ring_the_command_is_no_longer_hot()
    {
        var n = Navigator();
        Move(n, Category(n, 0), Ms(0));
        Move(n, Command(n, 0, 0), Ms(300));
        Move(n, Category(n, 0), Ms(320));
        Assert.Null(n.Armed(Ms(400)));
    }

    [Fact]
    public void A_gap_beside_a_short_arc_selects_nothing()
    {
        var n = Navigator();
        Move(n, Category(n, 4), Ms(0)); // Tools: two commands, 90° of 360°
        var (gx, gy) = RingGeometry.PointAt(RingGeometry.SectorCentre(4, 5) + Math.PI, 150);
        n.Move((int)gx, (int)gy, Ms(400));
        Assert.Null(n.Hot);
        Assert.Equal(MenuCategory.Tools, n.Active!.Category);
    }

    [Fact]
    public void Equal_states_give_equal_views_so_the_overlay_repaints_only_on_change()
    {
        var n = Navigator();
        Move(n, Category(n, 0), Ms(0));
        Move(n, Command(n, 0, 0), Ms(10));
        Assert.Equal(n.View(Ms(20)), n.View(Ms(30)));
        Assert.NotEqual(n.View(Ms(20)), n.View(Late)); // arming changes the view
    }

    [Theory]
    [InlineData(96u)]
    [InlineData(120u)]
    [InlineData(192u)]
    public void Straight_path_to_a_far_command_does_not_switch_to_the_category_it_crosses(uint dpi)
    {
        // Found in Explorer: Convert's arc (4 × 45°) is wider than its 72° sector, so the straight line from Convert out
        // to "To JPEG" crosses Tools in the category ring. That must not close Convert.
        var n = new MenuNavigator(RadialMenu.Build(CapabilityResolver.Default.Resolve([@"C:\p\photo.png"])), dpi);
        double s = dpi / 96.0, mid = (RingGeometry.InnerRadius96 + RingGeometry.OuterRadius96) / 2.0 * s, band = (RingGeometry.OuterRadius96 + RingGeometry.CommandRadius96) / 2.0 * s;
        var (ax, ay) = RingGeometry.PointAt(RingGeometry.SectorCentre(0, 5), mid);
        var (start, sweep) = RingGeometry.CommandArc(0, 5, 4);
        var (bx, by) = RingGeometry.PointAt(RingGeometry.CommandCentre(start, sweep, 0), band);
        for (var i = 0; i <= 20; i++)
        {
            n.Move((int)Math.Round(ax + (bx - ax) * i / 20), (int)Math.Round(ay + (by - ay) * i / 20), Ms(10 * i));
            Assert.Equal(MenuCategory.Convert, n.Active!.Category);
        }
        Assert.Equal("convert.to-jpeg", n.Armed(Ms(400))!.Id);
    }

    [Fact]
    public void Another_category_opens_near_the_hub_or_outside_the_open_arc()
    {
        var n = Navigator(); // JPEG: Convert, Compress, Resize, Rotate, Tools
        Move(n, Category(n, 0), Ms(0));
        // Tools lies inside Convert's arc: passing its outer part keeps Convert, its inner part opens it.
        var (ox, oy) = RingGeometry.PointAt(RingGeometry.SectorCentre(4, 5) + 0.3, RingGeometry.OuterRadius96 - 4);
        n.Move((int)ox, (int)oy, Ms(100));
        Assert.Equal(MenuCategory.Convert, n.Active!.Category);
        var (ix, iy) = RingGeometry.PointAt(RingGeometry.SectorCentre(4, 5), RingGeometry.InnerRadius96 + 6);
        n.Move((int)ix, (int)iy, Ms(200));
        Assert.Equal(MenuCategory.Tools, n.Active!.Category);
        // Resize lies outside Tools' arc: anywhere in its sector opens it.
        var (rx, ry) = RingGeometry.PointAt(RingGeometry.SectorCentre(2, 5), RingGeometry.OuterRadius96 - 4);
        n.Move((int)rx, (int)ry, Ms(300));
        Assert.Equal(MenuCategory.Resize, n.Active!.Category);
    }

    [Theory]
    [InlineData(96u)]
    [InlineData(120u)]
    [InlineData(144u)]
    [InlineData(192u)]
    public void Geometry_scales_with_dpi(uint dpi)
    {
        var groups = RadialMenu.Build(CapabilityResolver.Default.Resolve([@"C:\p\photo.png"]));
        var n = new MenuNavigator(groups, dpi);
        double s = dpi / 96.0;
        var (cx, cy) = RingGeometry.PointAt(RingGeometry.SectorCentre(0, groups.Count), 70 * s);
        n.Move((int)cx, (int)cy, TimeSpan.Zero);
        Assert.Equal(MenuCategory.Convert, n.Active!.Category);
        var (start, sweep) = RingGeometry.CommandArc(0, groups.Count, groups[0].Commands.Count);
        var (kx, ky) = RingGeometry.PointAt(RingGeometry.CommandCentre(start, sweep, 0), 150 * s);
        n.Move((int)kx, (int)ky, Late);
        Assert.Equal("convert.to-jpeg", n.Armed(Late)!.Id);
    }
}
