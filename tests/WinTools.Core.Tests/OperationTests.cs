using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Output;

namespace WinTools.Core.Tests;

public sealed class ResizeSpecTests
{
    [Theory]
    [InlineData(4000, 3000, 50, 2000, 1500)]
    [InlineData(101, 51, 50, 51, 26)]   // odd sizes round half away from zero
    [InlineData(3, 3, 25, 1, 1)]        // never below 1 px
    public void Percent_scales_both_sides(int w, int h, int percent, int ew, int eh)
    {
        Assert.Equal((ew, eh), new ResizeSpec(ResizeMode.Percent, Percent: percent).TargetFor(w, h));
    }

    [Theory]
    [InlineData(4000, 3000, 1920, 1080, 1440, 1080)]  // landscape limited by height
    [InlineData(3000, 4000, 1920, 1080, 810, 1080)]   // portrait
    [InlineData(4000, 1000, 1080, 1080, 1080, 270)]   // panorama into a square
    public void Box_fits_inside_and_keeps_the_aspect_ratio(int w, int h, int bw, int bh, int ew, int eh)
    {
        Assert.Equal((ew, eh), new ResizeSpec(ResizeMode.Box, Width: bw, Height: bh).TargetFor(w, h));
    }

    [Fact]
    public void Images_that_already_fit_are_not_upscaled()
    {
        Assert.Null(new ResizeSpec(ResizeMode.Box, Width: 1920, Height: 1080).TargetFor(800, 600));
        Assert.Null(new ResizeSpec(ResizeMode.Width, Width: 2000).TargetFor(800, 600));
        Assert.Null(new ResizeSpec(ResizeMode.Percent, Percent: 25).TargetFor(1, 1)); // same size: nothing to do
        Assert.Equal((2000, 1500), new ResizeSpec(ResizeMode.Width, Width: 2000, NoUpscale: false).TargetFor(800, 600));
    }

    [Fact]
    public void Exact_width_height_and_stretch()
    {
        Assert.Equal((800, 600), new ResizeSpec(ResizeMode.Width, Width: 800).TargetFor(1600, 1200));
        Assert.Equal((300, 600), new ResizeSpec(ResizeMode.Height, Height: 600).TargetFor(600, 1200));
        Assert.Equal((500, 500), new ResizeSpec(ResizeMode.Box, Width: 500, Height: 500, KeepAspect: false).TargetFor(1600, 1200));
    }
}

public sealed class OperationCatalogTests
{
    private static Operation Op(string id) => OperationCatalog.Get(id);

    [Theory]
    [InlineData("compress.high", ImageFormat.Jpeg, true)]
    [InlineData("compress.small", ImageFormat.WebP, true)]
    [InlineData("compress.balanced", ImageFormat.Png, false)]   // no lossy PNG "compression"
    [InlineData("compress.lossless", ImageFormat.Png, true)]
    [InlineData("compress.lossless", ImageFormat.Jpeg, false)]
    [InlineData("compress.high", ImageFormat.Bmp, false)]       // meaningless for BMP/TIFF: not offered
    [InlineData("compress.high", ImageFormat.Tiff, false)]
    [InlineData("resize.percent-50", ImageFormat.Tiff, true)]
    [InlineData("rotate.left", ImageFormat.Bmp, true)]
    [InlineData("rotate.left", ImageFormat.Unknown, false)]
    public void Operations_apply_only_where_they_mean_something(string id, ImageFormat source, bool applies)
    {
        Assert.Equal(applies, Op(id).AppliesTo(source));
    }

    [Fact]
    public void Non_convert_operations_keep_the_format_and_name_their_output()
    {
        Assert.Equal(ImageFormat.WebP, Op("resize.percent-50").OutputFormat(ImageFormat.WebP));
        Assert.Equal(ImageFormat.Png, Op("convert.to-png").OutputFormat(ImageFormat.Jpeg));
        Assert.Equal(@"C:\p\photo (resized 50%).jpg", OutputNaming.Candidates(@"C:\p\photo.jpg", ImageFormat.Jpeg, Op("resize.percent-50").NameSuffix).First());
        Assert.Equal(@"C:\p\photo (1920x1080) (1).png", OutputNaming.Candidates(@"C:\p\photo.png", ImageFormat.Png, Op("resize.box-1920x1080").NameSuffix).Skip(1).First());
        Assert.Equal("rotated left", Op("rotate.left").NameSuffix);
        Assert.Equal("compressed", Op("compress.lossless").NameSuffix);
        Assert.Null(Op("convert.to-webp").NameSuffix);
    }

    [Theory]
    [InlineData("resize.width-800", ResizeMode.Width, 800, 0, true)]
    [InlineData("resize.height-600", ResizeMode.Height, 0, 600, true)]
    [InlineData("resize.box-800x600", ResizeMode.Box, 800, 600, true)]
    [InlineData("resize.box-800x600-stretch", ResizeMode.Box, 800, 600, false)]
    public void Parameterised_resize_ids_parse(string id, ResizeMode mode, int w, int h, bool keepAspect)
    {
        var spec = OperationCatalog.Find(id)!.Resize!;
        Assert.Equal((mode, w, h, keepAspect), (spec.Mode, spec.Width, spec.Height, spec.KeepAspect));
    }

    [Theory]
    [InlineData("resize.width-0")]
    [InlineData("resize.width-70000")]
    [InlineData("resize.width--5")]
    [InlineData("resize.box-800")]
    [InlineData("resize.box-800x600x2")]
    [InlineData("resize.width-1e3")]
    [InlineData("delete.everything")]
    [InlineData("resize.box-0064x0010")]
    [InlineData("")]
    public void Malformed_ids_are_unknown(string id)
    {
        Assert.Null(OperationCatalog.Find(id));
        Assert.Null(OperationCatalog.Find(null));
    }

    [Fact]
    public void Resolver_offers_every_operation_kind_for_a_png()
    {
        var ids = CapabilityResolver.Default.Resolve([@"C:\p\a.png"]).Select(o => o.Id).ToArray();
        Assert.Contains("resize.percent-25", ids);
        Assert.Contains("resize.box-1080x1080", ids);
        Assert.Contains("compress.lossless", ids);
        Assert.DoesNotContain("compress.balanced", ids);
        Assert.Contains("rotate.180", ids);
    }

    [Fact]
    public void Mixed_png_and_jpeg_share_resize_and_rotate_but_no_compression()
    {
        var ids = CapabilityResolver.Default.Resolve([@"C:\p\a.png", @"C:\p\b.jpg"]).Select(o => o.Id).ToArray();
        Assert.Contains("resize.percent-50", ids);
        Assert.Contains("rotate.right", ids);
        Assert.DoesNotContain(ids, i => i.StartsWith("compress.", StringComparison.Ordinal));
    }
}

public sealed class IccProfileTests
{
    private static byte[] Header(string space, int declared = 132, string magic = "acsp")
    {
        var p = new byte[132];
        (p[0], p[1], p[2], p[3]) = ((byte)(declared >> 24), (byte)(declared >> 16), (byte)(declared >> 8), (byte)declared);
        System.Text.Encoding.ASCII.GetBytes(space).CopyTo(p, 16);
        System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(p, 36);
        return p;
    }

    [Fact]
    public void Only_well_formed_rgb_profiles_are_usable()
    {
        Assert.True(IccProfile.IsUsableRgb(Header("RGB ")));
        Assert.False(IccProfile.IsUsableRgb(Header("CMYK")));
        Assert.False(IccProfile.IsUsableRgb(Header("GRAY")));
        Assert.False(IccProfile.IsUsableRgb(Header("RGB ", declared: 4000)));   // size mismatch
        Assert.False(IccProfile.IsUsableRgb(Header("RGB ", magic: "xxxx")));    // not a profile
        Assert.False(IccProfile.IsUsableRgb(new byte[40]));
    }
}
