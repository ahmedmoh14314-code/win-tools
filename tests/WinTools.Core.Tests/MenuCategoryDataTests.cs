using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Interaction;

namespace WinTools.Core.Tests;

/// <summary>V2 PR 1.3: the radial menu's categories are module data, not a fixed list.</summary>
public sealed class MenuCategoryDataTests
{
    private static CapabilityDescriptor Any(string id, string category, string module = "files") =>
        new(id, id, category, module, new InputRule(FileKinds.All.Select(k => k.Id).ToHashSet()), CapabilityShape.PerFile, CapabilityOutput.Facts, false);

    [Fact]
    public void A_module_category_may_not_take_the_reserved_more_id()
    {
        // "more" is the menu's own overflow category: a module that declares it gets Other, never a second "more" group.
        CapabilityCategory clash = new("more", "Lots", "x", 5);
        var menu = RadialMenu.Build([Any("x.one", "more")], [clash, clash]); // a duplicate is tolerated too
        Assert.Equal(["Other"], menu.Select(g => g.Label));
    }

    [Fact]
    public void A_new_module_category_takes_its_declared_place_with_its_icon()
    {
        CapabilityCategory files = new("file", "File", "file", 50); // after Rotate (40); Tools comes second since the wheel PR
        var resolver = new CapabilityResolver([.. ImageCapabilities.Catalog(null), Any("files.sha256", "file")], [.. ImageCapabilities.Categories, files]);
        var menu = RadialMenu.Build(resolver, [@"C:\p\a.png"]);
        Assert.Equal(["Convert", "Tools", "Compress", "Resize", "Rotate", "File"], menu.Select(g => g.Label));
        Assert.Equal("file", menu.Single(g => g.Label == "File").Category.Icon);
    }

    [Fact]
    public void Modules_that_name_the_same_category_share_one_group()
    {
        var resolver = new CapabilityResolver([.. ImageCapabilities.Catalog(null), Any("files.copy-path", "tools")], ImageCapabilities.Categories);
        var tools = RadialMenu.Build(resolver, [@"C:\p\a.png"]).Single(g => g.Category.Id == "tools");
        Assert.Equal(["metadata.strip", "metadata.info", "files.copy-path"], tools.Commands.Select(c => c.Id));
    }

    [Fact]
    public void A_capability_in_an_undeclared_category_goes_last_under_other()
    {
        var resolver = new CapabilityResolver([.. ImageCapabilities.Catalog(null), Any("x.y", "nobody-declared-this")], ImageCapabilities.Categories);
        var menu = RadialMenu.Build(resolver, [@"C:\p\a.png"]);
        Assert.Equal(CapabilityCategory.Other, menu[^1].Category);
        Assert.Equal(["x.y"], menu[^1].Commands.Select(c => c.Id));
    }

    [Fact]
    public void A_category_declared_twice_keeps_its_first_declaration()
    {
        var resolver = new CapabilityResolver(ImageCapabilities.Catalog(null), [.. ImageCapabilities.Categories, new("convert", "Change type", "x", 1)]);
        Assert.Equal("Convert", resolver.Categories.Single(c => c.Id == "convert").Label);
    }

    [Fact]
    public void The_navigator_passes_icon_keys_to_the_view()
    {
        var menu = RadialMenu.Build(CapabilityResolver.Images(null), [@"C:\p\a.jpg"]);
        var view = new MenuNavigator(menu, 96).View(TimeSpan.Zero);
        Assert.Equal(["convert", "tools", "compress", "resize", "rotate"], view.Categories);
    }
}
