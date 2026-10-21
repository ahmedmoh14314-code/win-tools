using WinTools.Core.Files;
using WinTools.Core.Settings;

namespace WinTools.Core.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void Defaults_are_safe()
    {
        var s = new AppSettings();
        Assert.Equal(("Space", 0x20, OutputPolicy.SameFolder, 90, 90, true, true), (s.TriggerKey, s.TriggerVirtualKey, s.OutputPolicy, s.JpegQuality, s.WebPQuality, s.KeepAspectRatio, s.NoUpscale));
        Assert.Equal(AppSettings.CurrentSchema, s.Schema);
    }

    [Fact]
    public void Out_of_range_values_are_repaired_not_rejected()
    {
        var s = new AppSettings { TriggerKey = "Ctrl", JpegQuality = 0, WebPQuality = 400, ResizeWidth = -5, Theme = "Neon",
            OutputPolicy = OutputPolicy.Folder, OutputFolder = "relative\\path" }.Normalized();
        Assert.Equal(("Space", 1, 100, 0, "System", OutputPolicy.SameFolder), (s.TriggerKey, s.JpegQuality, s.WebPQuality, s.ResizeWidth, s.Theme, s.OutputPolicy));
        Assert.Null(s.OutputFolder);
    }

    [Fact]
    public void Output_folder_is_kept_when_valid()
    {
        var s = new AppSettings { OutputPolicy = OutputPolicy.Folder, OutputFolder = @"D:\Converted\" }.Normalized();
        Assert.Equal((OutputPolicy.Folder, @"D:\Converted"), (s.OutputPolicy, s.OutputFolder));
        Assert.Equal(OutputPolicy.SameFolder, new AppSettings { OutputPolicy = OutputPolicy.Folder, OutputFolder = @"D:\dir.\x" }.Normalized().OutputPolicy);
    }

    [Fact]
    public void Json_round_trips_and_garbage_is_rejected()
    {
        var s = new AppSettings { TriggerKey = "F9", JpegQuality = 77, OutputPolicy = OutputPolicy.Folder, OutputFolder = @"C:\Out" };
        Assert.Equal(s.Normalized(), AppSettings.FromJson(s.ToJson()));
        Assert.Null(AppSettings.FromJson("{ not json"));
        Assert.Null(AppSettings.FromJson("[1,2,3]"));
        Assert.Equal(0x78, AppSettings.FromJson("{\"triggerKey\":\"F9\"}")!.TriggerVirtualKey);
    }

    [Theory]
    [InlineData(1600, 1600, true, true, "resize.box-1600x1600")]
    [InlineData(800, 0, true, true, "resize.width-800")]
    [InlineData(0, 600, true, false, "resize.height-600-upscale")]
    [InlineData(800, 600, false, true, "resize.box-800x600-stretch")]
    [InlineData(0, 0, true, true, null)]
    public void Custom_resize_id_follows_the_settings(int w, int h, bool keepAspect, bool noUpscale, string? expected)
    {
        var id = new AppSettings { ResizeWidth = w, ResizeHeight = h, KeepAspectRatio = keepAspect, NoUpscale = noUpscale }.CustomResizeId();
        Assert.Equal(expected, id);
        if (id is not null) Assert.NotNull(Capabilities.OperationCatalog.Find(id));
    }
}

public sealed class MetadataTests
{
    /// <summary>EXIF TIFF block: Make="Cam", Orientation, and a GPS IFD pointer to a one-entry GPS IFD.</summary>
    internal static byte[] RichExif(int orientation)
    {
        var o = new List<byte>();
        void U16(int v) { o.Add((byte)v); o.Add((byte)(v >> 8)); }
        void U32(int v) { o.Add((byte)v); o.Add((byte)(v >> 8)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 24)); }
        o.AddRange("II*\0"u8.ToArray()); U32(8);
        U16(3);
        U16(0x010F); U16(2); U32(4); o.AddRange("Cam\0"u8.ToArray());          // Make, inline ASCII
        U16(0x0112); U16(3); U32(1); U16(orientation); U16(0);                  // Orientation
        U16(0x8825); U16(4); U32(1); U32(8 + 2 + 3 * 12 + 4);                   // GPS IFD pointer
        U32(0);
        U16(1); U16(0x0000); U16(1); U32(4); o.AddRange(new byte[] { 2, 3, 0, 0 }); U32(0); // GPSVersionID
        return [.. o];
    }

    internal static byte[] Segment(byte marker, ReadOnlySpan<byte> payload) =>
        [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];

    internal static byte[] Jpeg(params byte[][] segments)
    {
        // SOI, segments, a tiny fake frame/scan (structure only), EOI
        byte[] dqt = [0xFF, 0xDB, 0, 4, 0, 1], sos = [0xFF, 0xDA, 0, 2, 0x11, 0x22, 0xFF, 0x00, 0x33];
        return [0xFF, 0xD8, .. segments.SelectMany(s => s), .. dqt, .. sos, 0xFF, 0xD9];
    }

    [Fact]
    public void Jpeg_metadata_is_found_and_removed_while_icc_jfif_and_orientation_stay()
    {
        var exif = Segment(0xE1, [.. "Exif\0\0"u8, .. RichExif(6)]);
        var xmp = Segment(0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. "<x:xmpmeta/>"u8]);
        var icc = Segment(0xE2, [.. "ICC_PROFILE\0"u8, 1, 1, 9, 9, 9]);
        var jfif = Segment(0xE0, [.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        var com = Segment(0xFE, "a comment"u8);
        var jpeg = Jpeg(jfif, exif, xmp, icc, com);

        var found = Metadata.Inspect(jpeg, ImageFormat.Jpeg);
        Assert.Equal(new MetadataFound(Exif: true, Gps: true, Xmp: true, Icc: true, Text: true), found);

        var stripped = Metadata.Strip(jpeg, ImageFormat.Jpeg, orientation: 6)!;
        var after = Metadata.Inspect(stripped, ImageFormat.Jpeg);
        Assert.False(after.AnyRemovable);
        Assert.True(after.Icc);
        Assert.True(stripped.AsSpan().IndexOf(jfif) >= 0);
        Assert.True(stripped.AsSpan().IndexOf(Metadata.OrientationOnlyExif(6)) >= 0); // orientation kept on its own
        Assert.Equal(-1, stripped.AsSpan().IndexOf("Cam\0"u8));
        Assert.EndsWith("FF-D9", BitConverter.ToString(stripped), StringComparison.Ordinal);
    }

    [Fact]
    public void Jpeg_with_upright_orientation_gets_no_exif_at_all()
    {
        var jpeg = Jpeg(Segment(0xE1, [.. "Exif\0\0"u8, .. RichExif(1)]));
        var stripped = Metadata.Strip(jpeg, ImageFormat.Jpeg, orientation: 1)!;
        Assert.Equal(-1, stripped.AsSpan().IndexOf("Exif\0\0"u8));
    }

    [Fact]
    public void Png_text_exif_and_time_chunks_are_removed_colour_chunks_kept()
    {
        static byte[] Chunk(string t, byte[] data)
        {
            var td = System.Text.Encoding.ASCII.GetBytes(t).Concat(data).ToArray();
            var crc = ContainerCheck.Crc32(td);
            return [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length, .. td, (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
        }
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", new byte[13]), .. Chunk("gAMA", [0, 1, 0x86, 0xA0]),
            .. Chunk("eXIf", RichExif(1)), .. Chunk("iTXt", [.. "XML:com.adobe.xmp\0"u8, 0, 0, 0, 0]), .. Chunk("tEXt", "Author\0me"u8.ToArray()),
            .. Chunk("IDAT", [1]), .. Chunk("tIME", new byte[7]), .. Chunk("IEND", [])];
        Assert.Equal(new MetadataFound(true, true, true, false, true), Metadata.Inspect(png, ImageFormat.Png));
        var stripped = Metadata.Strip(png, ImageFormat.Png, 1)!;
        Assert.False(Metadata.Inspect(stripped, ImageFormat.Png).AnyRemovable);
        Assert.True(stripped.AsSpan().IndexOf("gAMA"u8) > 0);
        Assert.Equal(-1, stripped.AsSpan().IndexOf("tEXt"u8));
    }

    [Fact]
    public void Webp_exif_and_xmp_chunks_are_removed_and_flags_updated()
    {
        static byte[] Chunk(string id, byte[] data) => [.. System.Text.Encoding.ASCII.GetBytes(id), .. BitConverter.GetBytes(data.Length), .. data, .. (data.Length % 2 == 1 ? new byte[] { 0 } : [])];
        var vp8x = new byte[10]; vp8x[0] = 0x0C;               // EXIF + XMP flags
        var exif = RichExif(1);
        byte[] body = [.. "WEBP"u8, .. Chunk("VP8X", vp8x), .. Chunk("VP8L", new byte[5]), .. Chunk("EXIF", exif), .. Chunk("XMP ", "<x/>"u8.ToArray())];
        byte[] webp = [.. "RIFF"u8, .. BitConverter.GetBytes(body.Length), .. body];
        Assert.True(Metadata.Inspect(webp, ImageFormat.WebP).Gps);

        var stripped = Metadata.Strip(webp, ImageFormat.WebP, 1)!;

        Assert.False(Metadata.Inspect(stripped, ImageFormat.WebP).AnyRemovable);
        Assert.Equal(0, stripped[20] & 0x0C);                                  // VP8X flags cleared
        Assert.Equal(stripped.Length - 8, BitConverter.ToInt32(stripped, 4)); // RIFF size consistent
        Assert.True(ContainerCheck.IsComplete(stripped, ImageFormat.WebP, out var r), r);
    }
}
