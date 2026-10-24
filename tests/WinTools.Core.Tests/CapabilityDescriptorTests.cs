using WinTools.Core.Capabilities;
using WinTools.Core.Files;

namespace WinTools.Core.Tests;

/// <summary>V2 PR 1.1: file kinds and module-neutral capability descriptors (V2 plan §5).</summary>
public sealed class CapabilityDescriptorTests
{
    [Theory]
    [InlineData(@"C:\p\a.png", "png")]
    [InlineData(@"C:\p\a.PNG", "png")]
    [InlineData(@"C:\p\a.jpg", "jpeg")]
    [InlineData(@"C:\p\a.Jpeg", "jpeg")]
    [InlineData(@"C:\p\a.jpe", "jpeg")]
    [InlineData(@"C:\p\a.jfif", "jpeg")]
    [InlineData(@"C:\p\a.webp", "webp")]
    [InlineData(@"C:\p\a.bmp", "bmp")]
    [InlineData(@"C:\p\a.dib", "bmp")]
    [InlineData(@"C:\p\a.tif", "tiff")]
    [InlineData(@"C:\p\a.TIFF", "tiff")]
    public void A_kind_is_named_by_its_extension_in_any_case(string path, string kind)
    {
        Assert.Equal(kind, FileKinds.IdentifyByName(path)?.Id);
        Assert.Equal(FileFamily.Image, FileKinds.IdentifyByName(path)!.Family);
    }

    [Theory]
    [InlineData(@"C:\p\notes.txt")]
    [InlineData(@"C:\p\archive.png.zip")]
    [InlineData(@"C:\p\no-extension")]
    [InlineData(@"C:\p\trailing.")]
    [InlineData(@"C:\p\.png.folder\file")]
    public void Unknown_names_have_no_kind(string path)
    {
        Assert.Null(FileKinds.IdentifyByName(path));
    }

    [Fact]
    public void Kind_ids_and_extensions_are_unique()
    {
        Assert.Equal(FileKinds.All.Count, FileKinds.All.Select(k => k.Id).Distinct().Count());
        var extensions = FileKinds.All.SelectMany(k => k.Extensions).ToArray();
        Assert.Equal(extensions.Length, extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(extensions, e => Assert.True(e.StartsWith('.') && e.All(c => !char.IsUpper(c)), e));
    }

    [Fact]
    public void Every_image_operation_is_a_capability_of_the_image_module_carrying_its_operation()
    {
        var catalog = ImageCapabilities.Catalog(customResizeId: null);
        Assert.Equal(OperationCatalog.All.Select(o => o.Id), catalog.Select(c => c.Id));
        Assert.All(catalog, c =>
        {
            var op = ImageCapabilities.OperationOf(c);
            Assert.Equal((ImageCapabilities.Module, op.Id, op.Label, CapabilityShape.PerFile), (c.Module, c.Id, c.Label, c.Shape));
            Assert.Equal(op.WritesFile ? CapabilityOutput.File : CapabilityOutput.Facts, c.Produces);
        });
    }

    [Fact]
    public void Only_the_image_module_can_unwrap_its_payload()
    {
        var foreign = new CapabilityDescriptor("files.sha256", "SHA-256", "tools", "files", new InputRule(_ => true), CapabilityShape.PerFile, CapabilityOutput.Facts, false);
        Assert.Throws<ArgumentException>(() => ImageCapabilities.OperationOf(foreign));
    }

    [Fact]
    public void The_resolver_honours_a_capabilitys_selection_size()
    {
        var single = new CapabilityDescriptor("x.one", "One", "tools", "test", new InputRule(_ => true, MinCount: 1, MaxCount: 1), CapabilityShape.Selection, CapabilityOutput.Facts, false);
        var several = new CapabilityDescriptor("x.many", "Many", "tools", "test", new InputRule(_ => true, MinCount: 2), CapabilityShape.Selection, CapabilityOutput.File, true);
        var resolver = new CapabilityResolver([single, several]);
        Assert.Equal(["x.one"], resolver.Resolve([@"C:\a.png"]).Select(c => c.Id));
        Assert.Equal(["x.many"], resolver.Resolve([@"C:\a.png", @"C:\b.jpg"]).Select(c => c.Id));
    }

    [Fact]
    public void Catalogs_of_several_modules_are_resolved_together_by_kind()
    {
        var pngOnly = new CapabilityDescriptor("y.png", "PNG thing", "tools", "test", new InputRule(k => k == FileKinds.Png), CapabilityShape.PerFile, CapabilityOutput.File, true);
        var resolver = new CapabilityResolver([.. ImageCapabilities.Catalog(null), pngOnly]);
        Assert.Contains("y.png", resolver.Resolve([@"C:\a.png"]).Select(c => c.Id));
        Assert.DoesNotContain("y.png", resolver.Resolve([@"C:\a.png", @"C:\b.jpg"]).Select(c => c.Id));
        Assert.Contains("convert.to-tiff", resolver.Resolve([@"C:\a.png", @"C:\b.jpg"]).Select(c => c.Id));
    }
}
