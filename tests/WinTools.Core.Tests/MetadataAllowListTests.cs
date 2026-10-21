using System.Text;
using WinTools.Core.Files;

namespace WinTools.Core.Tests;

/// <summary>Hostile and unusual metadata layouts: removal works from an allow-list and the residue check walks the whole file.</summary>
public sealed class MetadataAllowListTests
{
    private static byte[] PngChunk(string type, byte[] data)
    {
        byte[] td = [.. Encoding.ASCII.GetBytes(type), .. data];
        var crc = ContainerCheck.Crc32(td);
        return [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length, .. td, (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
    }

    private static byte[] Png(params byte[][] chunks) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. chunks.SelectMany(c => c)];

    private static readonly byte[] Ihdr = PngChunk("IHDR", new byte[13]), Idat = PngChunk("IDAT", [1]), Iend = PngChunk("IEND", []);

    private static byte[] WebPChunk(string id, byte[] data) => [.. Encoding.ASCII.GetBytes(id), .. BitConverter.GetBytes(data.Length), .. data, .. (data.Length % 2 == 1 ? new byte[] { 0 } : [])];

    private static byte[] WebP(byte[] body, byte[]? after = null)
    {
        byte[] riff = [.. "WEBP"u8, .. body];
        return [.. "RIFF"u8, .. BitConverter.GetBytes(riff.Length), .. riff, .. after ?? []];
    }

    [Fact]
    public void Png_content_credentials_private_and_legacy_exif_chunks_are_removed()
    {
        var png = Png(Ihdr, PngChunk("caBX", "c2pa manifest: Jane Doe"u8.ToArray()), PngChunk("zxIf", [1, 2]), PngChunk("prVW", [3]), Idat, Iend);
        var found = Metadata.Inspect(png, ImageFormat.Png);
        Assert.True(found.Other);
        Assert.True(found.AnyRemovable); // a caBX-only file is not "nothing to do"
        Assert.NotEmpty(Metadata.Residue(png, ImageFormat.Png));

        var stripped = Metadata.Strip(png, ImageFormat.Png, 1)!;
        Assert.Empty(Metadata.Residue(stripped, ImageFormat.Png));
        Assert.Equal(-1, stripped.AsSpan().IndexOf("Jane"u8));
        Assert.Equal(Png(Ihdr, Idat, Iend), stripped);
    }

    [Fact]
    public void Png_data_after_iend_counts_as_metadata_and_is_dropped()
    {
        var png = Png(Ihdr, Idat, Iend, PngChunk("tEXt", "Author\0me"u8.ToArray()));
        Assert.True(Metadata.Inspect(png, ImageFormat.Png).AnyRemovable);
        Assert.Contains("data after IEND", Metadata.Residue(png, ImageFormat.Png));
        Assert.Equal(Png(Ihdr, Idat, Iend), Metadata.Strip(png, ImageFormat.Png, 1));
    }

    [Fact]
    public void Png_exif_orientation_is_kept_as_an_orientation_only_chunk_before_the_image_data()
    {
        var png = Png(Ihdr, PngChunk("eXIf", MetadataTests.RichExif(6)), Idat, Iend);
        var stripped = Metadata.Strip(png, ImageFormat.Png, 1)!;
        Assert.Equal(Png(Ihdr, PngChunk("eXIf", Metadata.OrientationOnlyExif(6)), Idat, Iend), stripped);
        Assert.Empty(Metadata.Residue(stripped, ImageFormat.Png));
        Assert.False(Metadata.Inspect(stripped, ImageFormat.Png).AnyRemovable);
    }

    [Fact]
    public void Truncated_png_is_not_cleaned_into_something_else()
    {
        var png = Png(Ihdr, Idat);
        Assert.Null(Metadata.Strip(png, ImageFormat.Png, 1));
        Assert.NotEmpty(Metadata.Residue(png, ImageFormat.Png));
    }

    [Fact]
    public void Webp_c2pa_chunk_and_data_after_the_riff_are_removed()
    {
        var vp8x = new byte[10];
        var webp = WebP([.. WebPChunk("VP8X", vp8x), .. WebPChunk("VP8L", new byte[5]), .. WebPChunk("C2PA", "manifest"u8.ToArray())], after: [.. "ZZZZ"u8, 4, 0, 0, 0, 9, 9, 9, 9]);
        Assert.True(Metadata.Inspect(webp, ImageFormat.WebP).Other);

        var stripped = Metadata.Strip(webp, ImageFormat.WebP, 1)!;
        Assert.Empty(Metadata.Residue(stripped, ImageFormat.WebP));
        Assert.Equal(WebP([.. WebPChunk("VP8X", vp8x), .. WebPChunk("VP8L", new byte[5])]), stripped);
    }

    [Fact]
    public void Webp_chunk_beyond_the_declared_riff_size_is_never_parsed()
    {
        var webp = WebP([.. WebPChunk("VP8L", new byte[5])], after: [.. "VP8X"u8, 0, 0, 0, 0]); // a 0-byte VP8X outside the RIFF
        var stripped = Metadata.Strip(webp, ImageFormat.WebP, 1)!; // must not throw
        Assert.Equal(WebP([.. WebPChunk("VP8L", new byte[5])]), stripped);
    }

    [Fact]
    public void Webp_exif_with_more_than_the_orientation_is_metadata()
    {
        byte[] padded = [.. Metadata.OrientationOnlyExif(6), .. "GPS 51.5N"u8];
        var vp8x = new byte[10]; vp8x[0] = 0x08;
        var webp = WebP([.. WebPChunk("VP8X", vp8x), .. WebPChunk("VP8L", new byte[5]), .. WebPChunk("EXIF", padded)]);
        Assert.True(Metadata.Inspect(webp, ImageFormat.WebP).Exif);

        var clean = WebP([.. WebPChunk("VP8X", vp8x), .. WebPChunk("VP8L", new byte[5]), .. WebPChunk("EXIF", Metadata.OrientationOnlyExif(6))]);
        Assert.False(Metadata.Inspect(clean, ImageFormat.WebP).AnyRemovable);
        Assert.Empty(Metadata.Residue(clean, ImageFormat.WebP));
    }

    [Fact]
    public void Jpeg_comment_between_scans_vendor_app14_thumbnail_and_trailing_data_are_removed()
    {
        var jfifThumb = MetadataTests.Segment(0xE0, [.. "JFIF\0"u8, 1, 2, 1, 0, 72, 0, 72, 1, 1, 0xAA, 0xBB, 0xCC]); // 1×1 thumbnail
        var vendor14 = MetadataTests.Segment(0xEE, "Vendor-secret"u8);
        var adobe = MetadataTests.Segment(0xEE, [.. "Adobe"u8, 0, 100, 0, 0, 0, 0, 1, 0xEE, 0xEE]); // standard 12 bytes + 2 extra
        byte[] dqt = [0xFF, 0xDB, 0, 4, 0, 1], sos = [0xFF, 0xDA, 0, 2];
        byte[] jpeg = [0xFF, 0xD8, .. jfifThumb, .. vendor14, .. adobe, .. dqt, .. sos, 0x11, 0xFF, 0x00, 0x22, 0xFF, 0xD0, 0x33,
            .. MetadataTests.Segment(0xFE, "between scans"u8), .. sos, 0x44, 0xFF, 0xD9, .. "trailing secret"u8];

        var found = Metadata.Inspect(jpeg, ImageFormat.Jpeg);
        Assert.True(found.Text);  // the comment between the scans is seen
        Assert.True(found.Other); // thumbnail, vendor APP14, trailing data

        var stripped = Metadata.Strip(jpeg, ImageFormat.Jpeg, 1)!;
        Assert.Empty(Metadata.Residue(stripped, ImageFormat.Jpeg));
        byte[] expected = [0xFF, 0xD8, .. MetadataTests.Segment(0xE0, [.. "JFIF\0"u8, 1, 2, 1, 0, 72, 0, 72, 0, 0]),
            .. MetadataTests.Segment(0xEE, [.. "Adobe"u8, 0, 100, 0, 0, 0, 0, 1]), .. dqt, .. sos, 0x11, 0xFF, 0x00, 0x22, 0xFF, 0xD0, 0x33,
            .. sos, 0x44, 0xFF, 0xD9];
        Assert.Equal(expected, stripped); // scan data, stuffing and restart markers byte for byte
    }

    [Fact]
    public void Truncated_jpeg_is_not_cleaned_and_its_residue_says_so()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xDB, 0, 4, 0, 1, 0xFF, 0xDA, 0, 2, 0x11, 0x22];
        Assert.Null(Metadata.Strip(jpeg, ImageFormat.Jpeg, 1));
        Assert.NotEmpty(Metadata.Residue(jpeg, ImageFormat.Jpeg));
    }

    [Fact]
    public void Residue_check_catches_what_a_faulty_removal_would_leave()
    {
        var exif = MetadataTests.Segment(0xE1, [.. "Exif\0\0"u8, .. MetadataTests.RichExif(1)]);
        Assert.NotEmpty(Metadata.Residue(MetadataTests.Jpeg(exif), ImageFormat.Jpeg));
        var twoOrientations = MetadataTests.Segment(0xE1, [.. "Exif\0\0"u8, .. Metadata.OrientationOnlyExif(6)]);
        Assert.NotEmpty(Metadata.Residue(MetadataTests.Jpeg(twoOrientations, twoOrientations), ImageFormat.Jpeg));
        Assert.Empty(Metadata.Residue(MetadataTests.Jpeg(twoOrientations), ImageFormat.Jpeg));
    }

    [Theory]
    [InlineData(@"\??\Volume{00000000-0000-0000-0000-000000000000}\dir\x.png")]
    [InlineData(@"\??\GLOBALROOT\Device\HarddiskVolume3\dir\x.png")]
    public void Nt_device_namespace_paths_are_refused(string path)
    {
        Assert.False(PathPolicy.IsSupported(path));
    }
}
