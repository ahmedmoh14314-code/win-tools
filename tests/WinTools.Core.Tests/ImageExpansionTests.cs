using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Interaction;

namespace WinTools.Core.Tests;

/// <summary>HEIC, AVIF, the edits and Collage as the catalog and the menu see them (ADR-0031).</summary>
public sealed class ImageExpansionTests
{
    private static byte[] Ftyp(string major, params string[] compatible)
    {
        var body = new List<byte>();
        body.AddRange(Encoding.ASCII.GetBytes(major));
        body.AddRange(new byte[4]);
        foreach (var b in compatible) body.AddRange(Encoding.ASCII.GetBytes(b));
        var size = body.Count + 8;
        return [(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size, .. "ftyp"u8, .. body];
    }

    [Theory]
    [InlineData("heic", "", ImageFormat.Heif)]
    [InlineData("mif1", "heic", ImageFormat.Heif)]
    [InlineData("msf1", "hevc", ImageFormat.Heif)]
    [InlineData("avif", "mif1", ImageFormat.Avif)]
    [InlineData("mif1", "avif", ImageFormat.Avif)]
    [InlineData("isom", "mp42", ImageFormat.Unknown)] // a video stays a video
    [InlineData("qt  ", "", ImageFormat.Unknown)]
    public void The_ftyp_brands_name_the_format(string major, string compatible, ImageFormat expected) =>
        Assert.Equal(expected, FileSignature.Detect(Ftyp(major, compatible.Length == 0 ? [] : [compatible])));

    [Fact]
    public void An_iphones_ftyp_fits_the_header_the_host_reads()
    {
        var ftyp = Ftyp("mif1", "MiHE", "MiPr", "miaf", "MiHB", "heic");
        Assert.True(ftyp.Length <= FileSignature.HeaderLength);
        Assert.Equal(ImageFormat.Heif, FileSignature.Detect(ftyp));
    }

    [Theory]
    [InlineData(1, 8, 8)]  // a quarter turn anticlockwise from upright
    [InlineData(8, 8, 3)]  // two make a half turn
    [InlineData(8, 3, 6)]  // three make a quarter turn clockwise, as an iPhone's irot 3
    [InlineData(2, 2, 1)]  // a mirror undoes itself
    [InlineData(4, 2, 3)]  // top-bottom and left-right make a half turn
    [InlineData(8, 2, 7)]  // the orders differ: a turn, then a mirror
    [InlineData(2, 8, 5)]
    public void Rotations_and_mirrors_compose_into_one_orientation(int first, int then, int expected) =>
        Assert.Equal(expected, Heif.Compose(first, then));

    [Theory]
    [InlineData(ImageFormat.Heif, ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Avif, ImageFormat.Avif)]
    [InlineData(ImageFormat.Png, ImageFormat.Png)]
    public void An_edit_writes_the_format_the_product_can_write(ImageFormat source, ImageFormat written)
    {
        foreach (var id in new[] { "rotate.right", "resize.percent-50", "flip.vertical", "crop.1x1", "adjust.blur" })
        {
            var op = OperationCatalog.Get(id);
            Assert.True(op.AppliesTo(source));
            Assert.Equal(written, op.OutputFormat(source));
        }
    }

    [Theory]
    [InlineData(ImageFormat.Heif)]
    [InlineData(ImageFormat.Avif)]
    public void Heic_and_avif_are_never_recompressed_or_stripped_in_place(ImageFormat source)
    {
        Assert.False(OperationCatalog.Get("metadata.strip").AppliesTo(source));
        Assert.DoesNotContain(OperationCatalog.All, o => o.Kind == OperationKind.Compress && o.AppliesTo(source));
        Assert.True(OperationCatalog.Get("metadata.info").AppliesTo(source));
    }

    [Fact]
    public void Heic_can_be_read_but_not_written()
    {
        Assert.True(ImageFormats.CanDecode(ImageFormat.Heif));
        Assert.False(ImageFormats.CanEncode(ImageFormat.Heif));
        Assert.DoesNotContain(OperationCatalog.All, o => o.Kind == OperationKind.Convert && o.Target == ImageFormat.Heif);
    }

    [Theory]
    [InlineData(@"C:\p\IMG_0001.HEIC")]
    [InlineData(@"C:\p\a.heif")]
    [InlineData(@"C:\p\a.hif")]
    public void Every_heif_name_gets_the_ring_of_a_heic(string path)
    {
        var menu = RadialMenu.Build(CapabilityResolver.Images(null), [path]);
        Assert.Equal(["Convert", "Tools", "Resize", "Edit"], menu.Select(g => g.Label));
        Assert.Equal(["To JPEG", "To PNG", "To WebP", "To BMP", "To TIFF", "To AVIF"], menu[0].Commands.Select(c => c.Label));
        Assert.Equal(["Image info"], menu[1].Commands.Select(c => c.Label));
    }

    [Fact]
    public void The_edit_commands_are_reachable_and_the_top_ring_keeps_room_for_its_labels()
    {
        var menu = RadialMenu.Build(CapabilityResolver.For(null), [@"C:\p\a.jpg"]);
        var edit = menu.Single(g => g.Category.Id == ImageCapabilities.EditCategory).Commands;
        Assert.Equal(["Left", "Right", "180°", "Flip left-right", "Upside down"], edit.Take(5).Select(c => c.Label));
        Assert.Equal(["Crop 1:1", "Crop 4:3", "Crop 16:9", "Crop 9:16", "Black & white", "Brighter", "Darker", "More contrast", "More vivid", "Sharpen", "Blur"],
            RadialMenu.Overflow(edit[5]).Select(c => c.Label));
        Assert.Equal(6, menu.Count); // a seventh group leaves no room for "Compress" (UiSurfaceTests)
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void Collage_takes_two_to_sixteen_images(int count, bool offered)
    {
        var paths = Enumerable.Range(0, count).Select(i => $@"C:\p\{i}.jpg").ToArray();
        Assert.Equal(offered, CapabilityResolver.For(null).Resolve(paths).Any(c => c.Id == OperationCatalog.CollageId));
    }

    [Fact]
    public void Collage_is_a_command_of_the_whole_selection_under_tools_and_refuses_other_files()
    {
        var collage = ImageCapabilities.Catalog(null).Single(c => c.Id == OperationCatalog.CollageId);
        Assert.Equal((CapabilityShape.Selection, CapabilityOutput.File, "tools"), (collage.Shape, collage.Produces, collage.Category));
        Assert.DoesNotContain(CapabilityResolver.For(null).Resolve([@"C:\p\a.jpg", @"C:\p\b.pdf"]), c => c.Id == OperationCatalog.CollageId);
    }

    [Theory]
    [InlineData(ImageFormat.Jpeg, ImageFormat.Jpeg, ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.Png)]
    [InlineData(ImageFormat.Heif, ImageFormat.Jpeg, ImageFormat.Png)]
    public void A_collage_is_a_jpeg_only_when_every_image_is(ImageFormat a, ImageFormat b, ImageFormat expected) =>
        Assert.Equal(expected, Operation.CollageFormat([a, b]));

    [Fact]
    public void Every_new_command_says_what_it_did_in_the_files_name()
    {
        Assert.Equal("cropped 16x9", OperationCatalog.Get("crop.16x9").NameSuffix);
        Assert.Equal("flipped vertically", OperationCatalog.Get("flip.vertical").NameSuffix);
        Assert.Equal("black and white", OperationCatalog.Get("adjust.black-and-white").NameSuffix);
        Assert.Equal("collage", OperationCatalog.Get(OperationCatalog.CollageId).NameSuffix);
        Assert.Null(OperationCatalog.Convert(ImageFormat.Avif).NameSuffix);
    }
}
