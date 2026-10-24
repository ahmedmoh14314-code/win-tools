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
    public void Unknown_names_are_plain_files(string path)
    {
        // No kind claims them by name; since the General Files module (V2 PR 1.4) they are still files.
        Assert.Null(FileKinds.ByExtensionOf(path));
        Assert.Same(FileKinds.AnyFile, FileKinds.IdentifyByName(path));
    }

    [Fact]
    public void Kind_ids_and_extensions_are_unique()
    {
        Assert.Equal(FileKinds.All.Count, FileKinds.All.Select(k => k.Id).Distinct().Count());
        var extensions = FileKinds.All.SelectMany(k => k.Extensions).ToArray();
        Assert.Equal(extensions.Length, extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(extensions, e => Assert.True(e.StartsWith('.') && e.All(c => !char.IsUpper(c)), e));
    }

    private static readonly IReadOnlySet<string> AnyKind = FileKinds.All.Select(k => k.Id).ToHashSet();

    [Fact]
    public void The_image_catalog_is_v1s_operations_in_order()
    {
        Assert.Equal(OperationCatalog.All.Select(o => o.Id), ImageCapabilities.Catalog(customResizeId: null).Select(c => c.Id));
    }

    [Theory]
    [InlineData("convert.to-png", "convert", "File", "jpeg webp bmp tiff")]
    [InlineData("compress.high", "compress", "File", "jpeg webp")]
    [InlineData("compress.lossless", "compress", "File", "png")]
    [InlineData("resize.percent-50", "resize", "File", "png jpeg webp bmp tiff")]
    [InlineData("rotate.right", "rotate", "File", "png jpeg webp bmp tiff")]
    [InlineData("metadata.strip", "tools", "File", "png jpeg webp tiff")]
    [InlineData("metadata.info", "tools", "Facts", "png jpeg webp bmp tiff")]
    public void Each_image_capability_declares_its_category_output_and_the_kinds_it_reads(string id, string category, string produces, string kinds)
    {
        var c = ImageCapabilities.Catalog(null).Single(c => c.Id == id);
        Assert.Equal((ImageCapabilities.Module, category, produces, CapabilityShape.PerFile), (c.Module, c.Category, c.Produces.ToString(), c.Shape));
        Assert.Equal(kinds.Split(' ').Order(), c.Input.Kinds.Order());   // declarative: the supported inputs can be listed
        Assert.Equal(id != "metadata.info", c.LongRunning);              // Image info answers at once; everything else shows progress
    }

    [Fact]
    public void A_capability_of_another_module_is_not_unwrapped_as_an_image_operation()
    {
        var foreign = new CapabilityDescriptor("files.sha256", "SHA-256", "tools", "files", new InputRule(AnyKind), CapabilityShape.PerFile, CapabilityOutput.Facts, false,
            Payload: OperationCatalog.Get("rotate.right")); // even one that carries an Operation by mistake
        Assert.False(ImageCapabilities.TryOperationOf(foreign, out _));
        Assert.Throws<ArgumentException>(() => ImageCapabilities.OperationOf(foreign));
    }

    [Fact]
    public void A_null_or_extensionless_name_has_no_kind()
    {
        Assert.Null(FileKinds.IdentifyByName(null));
        Assert.Null(FileKinds.IdentifyByName(""));
        Assert.Same(FileKinds.Png, FileKinds.Find("png"));
    }

    [Fact]
    public void The_resolver_honours_a_capabilitys_selection_size()
    {
        var single = new CapabilityDescriptor("x.one", "One", "tools", "test", new InputRule(AnyKind, MinCount: 1, MaxCount: 1), CapabilityShape.Selection, CapabilityOutput.Facts, false);
        var several = new CapabilityDescriptor("x.many", "Many", "tools", "test", new InputRule(AnyKind, MinCount: 2), CapabilityShape.Selection, CapabilityOutput.File, true);
        var resolver = new CapabilityResolver([single, several]);
        Assert.Equal(["x.one"], resolver.Resolve([@"C:\a.png"]).Select(c => c.Id));
        Assert.Equal(["x.many"], resolver.Resolve([@"C:\a.png", @"C:\b.jpg"]).Select(c => c.Id));
    }

    [Fact]
    public void Catalogs_of_several_modules_are_resolved_together_by_kind()
    {
        var pngOnly = new CapabilityDescriptor("y.png", "PNG thing", "tools", "test", new InputRule(new HashSet<string> { "png" }), CapabilityShape.PerFile, CapabilityOutput.File, true);
        var resolver = new CapabilityResolver([.. ImageCapabilities.Catalog(null), pngOnly]);
        Assert.Contains("y.png", resolver.Resolve([@"C:\a.png"]).Select(c => c.Id));
        Assert.DoesNotContain("y.png", resolver.Resolve([@"C:\a.png", @"C:\b.jpg"]).Select(c => c.Id));
        Assert.Contains("convert.to-tiff", resolver.Resolve([@"C:\a.png", @"C:\b.jpg"]).Select(c => c.Id));
    }
}
